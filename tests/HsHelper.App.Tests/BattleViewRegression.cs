using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;

public static class BattleViewRegression
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Chinese battle view uses observed hero, mana, weapon and power values", ObservedBattle);
        yield return ("battle view never substitutes printed stats for unknown current attributes", UnknownAttributes);
        yield return ("battle view excludes hidden opposing hands and unconfirmed perspective", Perspective);
        yield return ("battle view rejects cross-state deck and public-play ledgers", FutureLedgers);
        yield return ("battle view snapshots copy producer-owned lists and deck counts", ImmutableProjection);
        yield return ("local result formatting preserves unknown and conditional verification", ResultSemantics);
    }

    private static void ObservedBattle()
    {
        var view = BattleViewBuilder.Build(Frame(), Cards(), Build);
        Check(view.Local.Hero is { Name: "猎人英雄", Health: 26, Armor: 5 }, "Current hero values or Chinese name were lost.");
        Check(view.Opponent.Hero is { Health: 29, Armor: 2 }, "Opposing hero values were not projected.");
        Check(view.Local is { ManaTotal: 6, ManaSpent: 2, ManaLocked: 1, TemporaryMana: 1, ManaAvailable: 4 }, "Observed mana arithmetic changed.");
        Check(view.Local.Weapon is { Attack: 4, Durability: 2 } && view.Local.HeroPower is { Cost: 1, IsExhausted: false }, "Weapon or power current attributes were lost.");
        Check(view.Local.HeroPower!.Status.Contains("未疲劳", StringComparison.Ordinal) && view.Local.HeroPower.Status.Contains("尚需校验", StringComparison.Ordinal), "Power readiness was advertised as executable availability.");
        Check(view.Local.Board.Single() is { Name: "本地随从", Attack: 3, Health: 4 }, "Current board was not separated from hero/weapon/power.");
        Check(view.Opponent.Hand.Count == 0 && view.Opponent.HandCount == 1, "Opposing hand identity was exposed or count was lost.");
        Check(view.Local.PublicPlays.Count == 0 && view.Opponent.PublicPlays.Select(play => play.Sequence).SequenceEqual([60L, 80L]), "Repeated public plays or their order were lost.");
        Check(view.DeckIsExact && view.Local.DeckCount == 10 && view.RemainingDeck.Sum(card => card.Copies) == 10, "Bound deck composition was not exact.");
    }

    private static void UnknownAttributes()
    {
        var frame = Frame();
        var entities = frame.Entities.ToDictionary();
        entities[10] = entities[10] with { Tags = entities[10].Tags.Where(pair => pair.Key != "DAMAGE").ToFrozenDictionary() };
        entities[1] = entities[1] with { Tags = entities[1].Tags.Where(pair => pair.Key != "TEMP_RESOURCES").ToFrozenDictionary() };
        var view = BattleViewBuilder.Build(frame with { Entities = entities.ToFrozenDictionary(), Quality = frame.Quality with { IsComplete = false } }, Cards(), Build);
        Check(view.Local.Hand.Single().Cost is null, "Printed card cost replaced missing observed cost.");
        Check(view.Local.Hero is { Health: null, Armor: 5 }, "Unknown hero damage was silently assumed zero.");
        Check(view.Local.ManaAvailable is null && view.Local.ManaTotal == 6 && view.Local.TemporaryMana is null, "Unobserved mana component was silently filled.");
        Check(view.Opponent.HandCount is null, "Incomplete entity inventory became a precise opposing hand count.");
        Check(!view.DeckIsExact, "Incomplete log evidence advertised exact composition.");
    }

    private static void Perspective()
    {
        var frame = Frame();
        var cards = Cards();
        var json = JsonSerializer.Serialize(BattleViewBuilder.Build(frame, cards, Build));
        Check(!json.Contains("UI_PRIVATE", StringComparison.Ordinal) && !json.Contains("隐藏探针", StringComparison.Ordinal) && !json.Contains("ACCOUNT_SECRET", StringComparison.Ordinal), "Private identity/player tags entered battle projection.");
        Check(!BattleViewBuilder.VisibleCardIds(frame, Build).Contains("UI_PRIVATE"), "Name prefetch queried a hidden opposing card.");
        var unknown = frame with { Quality = frame.Quality with { HasLocalPerspective = false } };
        var view = BattleViewBuilder.Build(unknown, cards, Build);
        Check(!view.HasLocalPerspective && view.Local.Hand.Count == 0 && view.Opponent.Hand.Count == 0 && view.Local.PlayerId is null,
            "LocalPlayerId was trusted without confirmed perspective.");
        Check(view.RemainingDeck.Count == 0 && view.Local.PublicPlays.Count == 0, "Unconfirmed perspective exposed player-specific projections.");
        Check(!BattleViewBuilder.VisibleCardIds(unknown, Build).Contains("UI_HAND"), "Unconfirmed own hand was queried for Chinese names.");
        var staleChoice = frame with { Choice = new(5, 1, "GENERAL", null, 1, 1, [new(101, "UI_FUTURE_CHOICE", 1, 1)], true) };
        Check(!BattleViewBuilder.VisibleCardIds(staleChoice, Build).Contains("UI_FUTURE_CHOICE"), "Main-phase residual choice entered name prefetch.");
    }

    private static void FutureLedgers()
    {
        var frame = Frame();
        var future = frame with
        {
            DeckTracking = frame.DeckTracking! with { StateVersion = frame.StateVersion + 1, KnownRemaining = new Dictionary<string, int> { ["UI_FUTURE_DECK"] = 10 } },
            PublicPlays = new(frame.MatchId, frame.StateVersion + 1, [new(10, 300, "UI_FUTURE_PLAY", 2)], true, [])
        };
        var view = BattleViewBuilder.Build(future, Cards(), Build);
        Check(view.RemainingDeck.Count == 0 && view.Opponent.PublicPlays.Count == 0 && view.Warnings.Count >= 2,
            "Future nested state was treated as current knowledge.");
        Check(!BattleViewBuilder.VisibleCardIds(future, Build).Any(id => id.StartsWith("UI_FUTURE", StringComparison.Ordinal)), "Future nested identity entered prefetch.");
        var crossBuild = frame with { DeckTracking = frame.DeckTracking! with { CardBuild = "999999" } };
        Check(!BattleViewBuilder.Build(crossBuild, Cards(), Build).DeckIsExact && !BattleViewBuilder.MatchesDeckBinding(crossBuild, Build), "Another card build entered deck probabilities.");
        var valid = BattleViewBuilder.Build(frame, Cards(), Build);
        Check(valid.Opponent.PublicPlays.All(play => play.Sequence <= frame.EventSequence), "A future play sequence entered a matching ledger.");
    }

    private static void ImmutableProjection()
    {
        var warnings = new List<string> { "initial quality" };
        var counts = new Dictionary<string, int> { ["UI_HAND"] = 2, ["UI_OTHER"] = 8 };
        var frame = Frame();
        frame = frame with { Quality = frame.Quality with { Warnings = warnings }, DeckTracking = frame.DeckTracking! with { KnownRemaining = counts } };
        var view = BattleViewBuilder.Build(frame, Cards(), Build);
        warnings.Add("future warning"); counts["UI_HAND"] = 99;
        Check(view.Warnings.SequenceEqual(["initial quality"]) && view.RemainingDeck.Single(card => card.CardId == "UI_HAND").Copies == 2, "Producer mutation changed a captured view.");
        try { ((IList<BattleCardView>)view.Local.Hand).Clear(); throw new Exception("Consumer mutated a view list."); }
        catch (NotSupportedException) { }
    }

    private static void ResultSemantics()
    {
        var unknown = LocalToolResultFormatter.Format("calculate_draw_odds", JsonNode.Parse("""{"is_exact":false,"probability":null,"reason":"gap"}""")!);
        Check(unknown.Contains("未知", StringComparison.Ordinal) && !unknown.Contains('%'), "Unknown odds became a numeric chance.");
        var conditional = LocalToolResultFormatter.Format("evaluate_line", JsonNode.Parse("""{"Verification":"Conditional","IsLegal":true,"IsLethal":true,"AppliedActions":1,"Assumptions":["random branch"],"UnsupportedMechanics":["RANDOM"],"Errors":[],"RuleCoverage":["attack"]}""")!);
        Check(conditional.Contains("条件成立", StringComparison.Ordinal) && conditional.Contains("不能作确定", StringComparison.Ordinal) && conditional.Contains("random branch", StringComparison.Ordinal) && conditional.Contains("RANDOM", StringComparison.Ordinal) && conditional.Contains("未能证明", StringComparison.Ordinal), "Conditional estimate was displayed as a lethal proof.");
        var search = LocalToolResultFormatter.Format("find_lethal", JsonNode.Parse("""{"VerifiedLethalFound":false,"ExploredNodes":5000,"IsExhaustive":false,"Coverage":"bounded","Actions":[],"Evaluation":null}""")!);
        Check(search.Contains("不代表没有斩杀", StringComparison.Ordinal) && search.Contains("5000", StringComparison.Ordinal), "Bounded search absence became proof of no lethal.");
        var choice = LocalToolResultFormatter.Format("validate_choice", JsonNode.Parse("""{"Verification":"Verified","IsLegal":true,"Errors":[],"Assumptions":[],"RuleCoverage":["candidate-count"]}""")!);
        Check(choice.Contains("不模拟选择后效果", StringComparison.Ordinal), "Choice validation claimed effect simulation.");
    }

    public const string Build = "255790";
    public static GameSnapshot Frame()
    {
        var entities = new[]
        {
            Entity(1, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("RESOURCES", "6"), ("RESOURCES_USED", "2"), ("OVERLOAD_LOCKED", "1"), ("TEMP_RESOURCES", "1"), ("BATTLETAG", "ACCOUNT_SECRET")),
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20"), ("RESOURCES", "5"), ("RESOURCES_USED", "1"), ("OVERLOAD_LOCKED", "0"), ("TEMP_RESOURCES", "0")),
            Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "3"), ("HEALTH", "30"), ("DAMAGE", "4"), ("ARMOR", "5"), ("ATK", "0")),
            Entity(20, "HERO_08", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("HEALTH", "30"), ("DAMAGE", "1"), ("ARMOR", "2"), ("ATK", "0")),
            Entity(11, "UI_WEAPON", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "7"), ("ATK", "4"), ("DURABILITY", "2")),
            Entity(12, "HERO_05bp", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "10"), ("COST", "1"), ("EXHAUSTED", "0")),
            Entity(30, "UI_HAND", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "HAND"), ("CARDTYPE", "MINION"), ("ZONE_POSITION", "1")),
            Entity(31, "UI_BODY", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "4"), ("ATK", "3"), ("HEALTH", "4"), ("DAMAGE", "0"), ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1"), ("ZONE_POSITION", "1")),
            Entity(40, "UI_PRIVATE", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "HAND"), ("CARDTYPE", "SPELL"))
        };
        return new("ui-local-match", 7, 100, 4, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        {
            ClientBuild = Build,
            DeckTracking = new("ui-local-match", 7, "deck-v1", Build, 10, new Dictionary<string, int> { ["UI_HAND"] = 2, ["UI_OTHER"] = 8 }, 0, true, []),
            PublicPlays = new("ui-local-match", 7, [new(60, 70, "UI_PUBLIC", 2), new(80, 70, "UI_PUBLIC", 2), new(101, 71, "UI_FUTURE", 2)], true, [])
        };
    }

    public static IReadOnlyDictionary<string, CardRecord> Cards() => new[]
    {
        Card("HERO_05", "猎人英雄", "HERO"), Card("HERO_08", "法师英雄", "HERO"), Card("UI_WEAPON", "测试武器", "WEAPON"),
        Card("HERO_05bp", "稳固射击", "HERO_POWER"), Card("UI_HAND", "测试手牌", "MINION"), Card("UI_BODY", "本地随从", "MINION"),
        Card("UI_OTHER", "其余卡牌", "SPELL"), Card("UI_PRIVATE", "隐藏探针", "SPELL"), Card("UI_PUBLIC", "公开卡牌", "SPELL")
    }.ToDictionary(card => card.Id, StringComparer.Ordinal);

    public static string CardsJson() => JsonSerializer.Serialize(Cards().Values.Select((card, index) => new
    {
        id = card.Id, dbfId = index + 10000, name = card.Name, text = "", type = card.Type, cost = 8, attack = 3, health = 4,
        durability = 3, mechanics = Array.Empty<string>(), cardClass = "HUNTER", set = "SYNTHETIC", collectible = true
    }));

    private static CardRecord Card(string id, string name, string type) => new(id, null, name, "", "HUNTER", "SYNTHETIC", type, 8, true, [], [], """{"attack":99,"health":99,"durability":99}""");
    private static EntitySnapshot Entity(int id, string? cardId, EntityKind kind, params (string Key, string Value)[] tags) =>
        new(id, cardId, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
