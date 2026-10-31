using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class AssembledRuleChecks
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";

    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> provided)
    {
        var cards = Definitions(provided);
        yield return ("assembled sources never spend resources summon or resolve a native effect", () =>
        {
            foreach (var marker in Markers())
            foreach (var id in new[] { "EDR_852", "FIR_921", "GAME_005" })
            {
                var frame = Replace(Frame(id), Mark(Source(id), marker));
                var before = JsonSerializer.Serialize(frame);
                var result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], cards);
                Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 0 &&
                    result.UnsupportedMechanics.Contains("CUSTOM_ASSEMBLED_SOURCE") && result.Result.AvailableMana == 10 &&
                    result.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND" &&
                    result.Result.HeroPowerChanges.Count == 0 && result.Result.DrawPreviews.Count == 0 &&
                    !result.IsLethal && before == JsonSerializer.Serialize(frame), "Custom text was replaced by its base native play.");
            }
        });
        yield return ("an active assembled body cannot support a verified native combat or lethal line", () =>
        {
            var frame = Replace(Frame(), Mark(Entity(20, "EDR_480", "PLAY", 1), new Dictionary<string, string> { [First] = "114254" }));
            var result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.HeroPower, 12)], cards);
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 0 && !result.IsLethal &&
                result.UnsupportedMechanics.Contains("CUSTOM_ASSEMBLED_ACTIVE") &&
                result.Result.Entities.Single(entity => entity.EntityId == 11).RemainingHealth == 2,
                "An assembled active body's printed aura was silently applied or ignored for lethal.");
            Check(!new LineEvaluator().FindLethal(frame, cards, 1, 20).VerifiedLethalFound,
                "Lethal search bypassed the active custom-text boundary.");
            var attacker = Replace(Frame(), Mark(Entity(20, "EDR_480", "PLAY", 1, ("ATK", 9), ("HEALTH", 9)),
                new Dictionary<string, string> { ["4573"] = "1" }));
            result = new LineEvaluator().EvaluateLine(attacker, [new(ActionKind.Attack, 20, 11)], cards);
            Check(result.AppliedActions == 0 && !result.IsLethal && result.UnsupportedMechanics.Contains("CUSTOM_ASSEMBLED_SOURCE"),
                "An offered/assembled source attacked using its base identity.");
        });
        yield return ("an unrelated assembled hand card preserves uncertainty about hand triggers", () =>
        {
            var frame = Replace(Frame(), Mark(Source("ASSEMBLY_VANILLA"), new Dictionary<string, string> { [First] = "1" }));
            var result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.HeroPower, 12)], cards);
            Check(result.Verification == LineVerification.Conditional && result.AppliedActions == 1 && !result.IsLethal &&
                result.UnsupportedMechanics.Contains("CUSTOM_ASSEMBLED_HAND"), "Unknown custom hand triggers allowed a verified lethal.");
        });
        yield return ("explicitly cleared zero halves do not block ordinary native actions", () =>
        {
            var clear = new Dictionary<string, string> { [First] = "0", [Second] = "0", ["4572"] = "0", ["4573"] = "0" };
            var frame = Replace(Frame(), Mark(Source("ASSEMBLY_VANILLA"), clear));
            var result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], cards);
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 1 &&
                result.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "PLAY" &&
                !result.UnsupportedMechanics.Any(value => value.StartsWith("CUSTOM_ASSEMBLED", StringComparison.Ordinal)),
                "Cleared assembly tags suppressed an ordinary source play.");
            result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.HeroPower, 12)], cards);
            Check(result.Verification == LineVerification.Verified && result.IsLethal, "Zero halves introduced spurious hand-trigger uncertainty.");
        });
        yield return ("assembled native choice and direct draw entry points reject base-card effects", () =>
        {
            foreach (var marker in Markers())
            {
                var tracking = ChoiceFrame("CORE_DS1_184", "GRAVEYARD");
                tracking = Replace(tracking, Mark(tracking.Entities[30], marker));
                var choice = new LineEvaluator().EvaluateChoice(tracking, [40], cards, 7);
                Check(!choice.ResolvesEffects && choice.CardOutcomes.Count == 0 && choice.DirectDeckChange is null,
                    "Assembled Tracking was resolved as the native deck draw.");
                var draw = DrawResolutionRules.Evaluate(Replace(Frame("FIR_921"), Mark(Source("FIR_921"), marker)), 30, cards, true);
                Check(!draw.ResolvesEffects && draw.RequestedDraws.Maximum is null, "Direct draw bypassed custom source text.");
                var raptor = ChoiceFrame("CORE_EDR_004_2026", "PLAY");
                raptor = Replace(raptor, Mark(raptor.Entities[30], marker));
                Check(RewindChoiceRules.Evaluate(raptor, raptor.Entities[30], raptor.Choice!.Options[0], cards) is null,
                    "Direct Rewind bypassed custom source text.");
                var malorne = ChoiceFrame("EDR_888", "PLAY");
                malorne = Replace(malorne, Mark(malorne.Entities[30], marker));
                Check(!MalorneChoiceRules.Evaluate(malorne, 40, cards).ResolvesEffects, "Direct Malorne bypassed custom source text.");
            }
        });
        yield return ("zero halves preserve ordinary choice draw and direct source behavior", () =>
        {
            var clear = new Dictionary<string, string> { [First] = "0", [Second] = "0" };
            var tracking = ChoiceFrame("CORE_DS1_184", "GRAVEYARD");
            tracking = Replace(tracking, Mark(tracking.Entities[30], clear));
            Check(new LineEvaluator().EvaluateChoice(tracking, [40], cards, 7).CardOutcomes.Count == 1,
                "Zero halves disabled native Tracking.");
            var draw = DrawResolutionRules.Evaluate(Replace(Frame("FIR_921"), Mark(Source("FIR_921"), clear)), 30, cards, true);
            Check(draw.ResolvesEffects && draw.RequestedDraws is { Minimum: 2, Maximum: 2 }, "Zero halves disabled a native draw preview.");
            var raptor = ChoiceFrame("CORE_EDR_004_2026", "PLAY");
            raptor = Replace(raptor, Mark(raptor.Entities[30], clear));
            Check(RewindChoiceRules.Evaluate(raptor, raptor.Entities[30], raptor.Choice!.Options[0], cards)?.ResolvesEffects == true,
                "Zero halves disabled native Raptor choices.");
            var malorne = ChoiceFrame("EDR_888", "PLAY");
            malorne = Replace(malorne, Mark(malorne.Entities[30], clear));
            Check(MalorneChoiceRules.Evaluate(malorne, 40, cards).ResolvesEffects, "Zero halves disabled native Malorne choices.");
        });
        yield return ("F3TCH3R four offered half markers identify the selected half without acquiring a card", () =>
        {
            var frame = HalfFrame(); var before = JsonSerializer.Serialize(frame);
            foreach (var (entity, half, text) in new[] { (40, "Trigger", "在你施放法术后，"), (41, "Effect", "抽一张牌。") })
            {
                var result = new LineEvaluator().EvaluateChoice(frame, [entity], cards, 7);
                Check(result.Crafting is { Stage: "HalfSelection", ResolvesEffects: true, AddedToHand: false, PrimaryEntityId: null } selected &&
                    selected.OfferedHalf == half && selected.OfferedHalfText == text && selected.ResultingCost is null &&
                    selected.ResultingAttack is null && selected.ResultingHealth is null && selected.RequiresAnotherChoice is null &&
                    result.CardOutcomes.Count == 0 && result.HandCountAfter == 0 && !result.CanEvaluateFollowupActions,
                    "An observed half choice was ignored, added to hand or turned into a fabricated complete minion.");
            }
            Check(before == JsonSerializer.Serialize(frame), "Half selection mutated its authoritative frame.");
        });
        yield return ("F3TCH3R half selection needs four bound consistent buttons rather than positions or target hints", () =>
        {
            var frame = HalfFrame();
            var invalid = new[]
            {
                frame with { Choice = frame.Choice! with { Options = frame.Choice.Options.Take(3).ToArray() } },
                Replace(frame, Mark(frame.Entities[40], new Dictionary<string, string> { [First] = "1" })),
                Replace(frame, Mark(frame.Entities[40], new Dictionary<string, string> { [Second] = "invalid" })),
                Replace(frame, Mark(frame.Entities[40], new Dictionary<string, string> { [First] = "1", [Second] = "0" })),
                Replace(frame, frame.Entities[40] with { CardId = null }),
                frame with { Choice = frame.Choice! with { SourceCardId = null } },
                Replace(frame, Mark(frame.Entities[30], new Dictionary<string, string> { [First] = "1" }))
            };
            foreach (var candidate in invalid)
            {
                var result = CraftChoiceRules.Evaluate(candidate, 41, cards);
                Check(!result.ResolvesEffects && result.Stage == "Unconfirmed" && result.OfferedHalf is null && result.OfferedHalfText is null &&
                    result.PrimaryEntityId is null && result.AddedToHand is null, "Conflicting/missing markers or source identity confirmed a half/primary.");
            }
            var onlyHints = HalfFrame();
            foreach (var id in new[] { 40, 41, 42, 43 })
            {
                var tags = onlyHints.Entities[id].Tags.Where(pair => pair.Key != First && pair.Key != Second).ToDictionary();
                tags["4694"] = "80"; tags["4957"] = "81";
                onlyHints = Replace(onlyHints, onlyHints.Entities[id] with { Tags = tags.ToFrozenDictionary() });
            }
            Check(!CraftChoiceRules.Evaluate(onlyHints, 40, cards).ResolvesEffects, "Unreviewed links substituted for observed half markers.");
        });
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> Markers()
    {
        yield return new Dictionary<string, string> { [First] = "114254", [Second] = "115203" };
        yield return new Dictionary<string, string> { ["4572"] = "1" };
        yield return new Dictionary<string, string> { [Second] = "invalid" };
        yield return new Dictionary<string, string> { [First] = "-1" };
        yield return new Dictionary<string, string> { [First] = "0", ["4572"] = "1" };
    }
    private static GameSnapshot Frame(string source = "ASSEMBLY_VANILLA") => new("assembled-rules", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["HERO_ENTITY"] = "10", ["RESOURCES"] = "10",
            ["RESOURCES_USED"] = "0", ["MAXHANDSIZE"] = "10", ["NUM_CARDS_IN_HAND"] = "1", ["NUM_CARDS_IN_DECK"] = "3", ["3527"] = "2" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "11" }.ToFrozenDictionary()),
            Entity(10, "HERO_05", "PLAY", 1, ("HEALTH", 30), ("CARDTYPE", 3)),
            Entity(11, "HERO_08", "PLAY", 2, ("HEALTH", 2), ("CARDTYPE", 3)),
            Entity(12, "HERO_05bp", "PLAY", 1, ("CARDTYPE", 10), ("COST", 2), ("EXHAUSTED", 0)), Source(source) }
            .ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static GameSnapshot ChoiceFrame(string source, string zone)
    {
        var frame = Replace(Frame(), Entity(30, source, zone, 1));
        frame = Replace(frame, Entity(40, source == "EDR_888" ? "EDR_480" : "EDR_227", "SETASIDE", 1));
        var playerTags = frame.Entities[1].Tags.ToDictionary(); playerTags["NUM_CARDS_IN_HAND"] = "0";
        frame = Replace(frame, frame.Entities[1] with { Tags = playerTags.ToFrozenDictionary() });
        return frame with { Phase = GamePhase.Choice, Choice = new(7, 1, "GENERAL", 30, 1, 1,
            [new(40, frame.Entities[40].CardId, 1, 1)], true) { SourceCardId = source, OfferedAtEventSequence = 90 } };
    }
    private static GameSnapshot HalfFrame()
    {
        var frame = ChoiceFrame("BE_044", "PLAY");
        foreach (var (id, card, half) in new[] { (40, "HALF_TRIGGER_A", "Trigger"), (41, "HALF_EFFECT_A", "Effect"),
            (42, "HALF_TRIGGER_B", "Trigger"), (43, "HALF_EFFECT_B", "Effect") })
            frame = Replace(frame, Mark(Entity(id, card, "SETASIDE", 1), new Dictionary<string, string>
            { [First] = half == "Effect" ? "1" : "0", [Second] = half == "Trigger" ? "1" : "0", ["4694"] = "80", ["4957"] = "81" }));
        return frame with { Choice = frame.Choice! with { Options = new[] { 41, 43, 40, 42 }.Select(id => new ChoiceOptionSnapshot(id, frame.Entities[id].CardId, 1, id)).ToArray() } };
    }
    private static EntitySnapshot Source(string id) => Entity(30, id, "HAND", 1, ("COST", 2), ("ATK", 2), ("HEALTH", 3));
    private static EntitySnapshot Entity(int id, string card, string zone, int owner, params (string Key, int Value)[] values)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = "MINION", ["DAMAGE"] = "0", ["ARMOR"] = "0" };
        foreach (var (key, value) in values) tags[key] = value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Mark(EntitySnapshot entity, IReadOnlyDictionary<string, string> markers)
    { var tags = entity.Tags.ToDictionary(); foreach (var pair in markers) tags[pair.Key] = pair.Value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Definitions(IReadOnlyDictionary<string, CardRecord> provided)
    {
        var cards = provided.ToDictionary();
        foreach (var name in new[] { "beast-hunter-255790.json", "rewind-choice-255790.json", "imbue-malorne-255790.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
                    raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        }
        foreach (var (id, text) in new[] { ("ASSEMBLY_VANILLA", ""), ("HALF_TRIGGER_A", "在你施放法术后，&使它获得+1/+1。"),
            ("HALF_EFFECT_A", "在回合结束时，&抽一张牌。"), ("HALF_TRIGGER_B", "在你使用随从后，&造成1点伤害。"), ("HALF_EFFECT_B", "亡语：&召唤一个2/2的随从。") })
            cards[id] = new(id, null, id, text, "NEUTRAL", "SYNTHETIC", "MINION", 2, false, [], [],
                "{\"attack\":2,\"health\":3,\"mechanics\":[],\"race\":\"BEAST\"}");
        return cards;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
