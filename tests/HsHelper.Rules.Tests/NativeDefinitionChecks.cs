using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class NativeDefinitionChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Fixed = new(ReadCards);
    private static readonly LineEvaluator Evaluator = new();
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native definitions use complete primary records and canonical object ordering", PrimaryDefinitions);
        yield return ("native definition trust rejects coherent semantic changes and record raw disagreement", DefinitionMutations);
        yield return ("native aura metadata is checked before manual or searched damage for either owner", AuraSources);
        yield return ("native play and hero power reject changed definitions before paying or dispatching by type", ActiveSources);
        yield return ("native entity identity and observed type aliases cannot be repaired from fixed metadata", EntityEvidence);
        yield return ("native death and its actually affected hand targets require complete definitions", DeathSources);
        yield return ("native health payment and stat enchantments cannot inherit effects from their IDs alone", EnchantmentSources);
        yield return ("direct native generation draw discovery and quest APIs share complete source trust", DirectEffects);
        yield return ("current choice sources and fixed offered grants reject altered complete definitions", ChoiceSources);
        yield return ("unrelated hand deck history and unused powers do not create a global native gate", RelevantScope);
    }

    private static void PrimaryDefinitions()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-definitions-255790.json")));
        Check(fixture.RootElement.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            fixture.RootElement.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8",
            "Native records lost their fixed primary provenance.");
        foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
        {
            var original = ReadCard(raw);
            Check(NativeCardDefinitions.IsAudited("255790", original.Id, original), "Primary record rejected: " + original.Id);
            var reordered = new JsonObject();
            foreach (var pair in JsonNode.Parse(original.RawJson)!.AsObject().Reverse()) reordered.Add(pair.Key, pair.Value?.DeepClone());
            Check(NativeCardDefinitions.IsAudited("255790", original.Id, original with { RawJson = reordered.ToJsonString(), StandardLegality = CardLegality.Unknown }),
                "Harmless serialization order or derived pool data changed definition identity: " + original.Id);
            Check(!NativeCardDefinitions.IsAudited("255791", original.Id, original) && !NativeCardDefinitions.IsAudited(null, original.Id, original) &&
                !NativeCardDefinitions.IsAudited("255790", original.Id + "_other", original), "An unbound build or ID borrowed the pinned record.");
        }
    }

    private static void DefinitionMutations()
    {
        foreach (var original in Fixed.Value.Values.Where(card => NativeCardDefinitions.IsRegistered(card.Id)))
        {
            foreach (var mutate in new Action<JsonObject>[]
            {
                raw => raw["dbfId"] = original.DbfId + 1,
                raw => raw["text"] = "不能按原生牌效结算。",
                raw => raw["type"] = original.Type == "MINION" ? "SPELL" : "MINION",
                raw => raw["cost"] = 99,
                raw => raw["name"] = "不同的完整定义",
                raw => raw["cardClass"] = "INVALID_CLASS",
                raw => raw["set"] = "INVALID_SET",
                raw => raw["collectible"] = !original.Collectible,
                raw => raw["mechanics"] = new JsonArray("TRIGGER_VISUAL", "DEATHRATTLE"),
                raw => raw["race"] = "INVALID_RACE",
                raw => raw["newUnreviewedScriptField"] = 1
            })
            {
                var coherent = Changed(original, mutate);
                Check(!NativeCardDefinitions.IsAudited("255790", original.Id, coherent), "A changed whole definition passed: " + original.Id);
            }
            foreach (var inconsistent in new[] { original with { Name = "different" }, original with { Text = "different" }, original with { DbfId = 1 },
                original with { Type = "INVALID" }, original with { Cost = 99 }, original with { Collectible = !original.Collectible },
                original with { Classes = ["HUNTER"] }, original with { Entourage = ["UNKNOWN_DONOR"] }, original with { RawJson = "{}" },
                original with { RawJson = "broken" }, original with { RawJson = new string(' ', 65537) } })
                Check(!NativeCardDefinitions.IsAudited("255790", original.Id, inconsistent), "Record/raw disagreement passed: " + original.Id);
        }
    }

    private static void AuraSources()
    {
        var frame = Parsed(Frame(10, 6, Body(30, "BEAST", 1, 3, 8, ("CARDRACE", "20")),
            Body(60, "EDR_480", 1, 0, 9, ("RUSH", "1"), ("AURA", "1"))));
        var before = JsonSerializer.Serialize(frame);
        var good = Eval(frame, new PlannedAction(ActionKind.Attack, 30, 21));
        Check(good.Verification == LineVerification.Verified && good.IsLethal && good.RuleCoverage.Contains("fixed-native-definition:EDR_480"),
            "A fixed native aura no longer doubles its owner's observed Beast damage.");
        foreach (var bad in Mutations("EDR_480"))
        {
            var cards = With(bad);
            RejectedBefore(Eval(frame, cards, new PlannedAction(ActionKind.Attack, 30, 21)), frame);
            Check(!Evaluator.FindLethal(frame, cards, 2, 100).VerifiedLethalFound, "Lethal search bypassed native aura identity.");
            var enemyAura = Replace(frame, Body(60, "EDR_480", 2, 0, 9, ("AURA", "1")), Body(40, "BEAST", 2, 3, 8, ("CARDRACE", "20")));
            RejectedBefore(Eval(enemyAura, cards, new PlannedAction(ActionKind.Attack, 30, 40)), enemyAura);
        }
        Check(before == JsonSerializer.Serialize(frame), "Native trust mutated the Parser's frame.");
    }

    private static void ActiveSources()
    {
        var arcane = Frame(4, 2, Hand(30, "CORE_DS1_185", 1));
        var good = Eval(arcane, new PlannedAction(ActionKind.PlayCard, 30, 21));
        Check(good.Verification == LineVerification.Verified && good.IsLethal && good.Result.AvailableMana == 3, "Audited Arcane Shot changed.");
        var power = Frame(4, 2, Card(30, "HERO_05bp", 1, "PLAY", "HERO_POWER", ("COST", "2"), ("EXHAUSTED", "0")));
        Check(Eval(power, new PlannedAction(ActionKind.HeroPower, 30)).IsLethal, "Audited current hero power changed.");
        foreach (var (frame, action, id) in new[] { (arcane, new PlannedAction(ActionKind.PlayCard, 30, 21), "CORE_DS1_185"),
            (power, new PlannedAction(ActionKind.HeroPower, 30), "HERO_05bp") })
        {
            foreach (var bad in Mutations(id)) RejectedBefore(Eval(frame, With(bad), action), frame);
            // All three views agree on the false type; dispatch must still reject the fixed identity,
            // before the generic simple-minion or equip branch can use it.
            foreach (var type in new[] { "MINION", "WEAPON" })
            {
                var impostor = Changed(Fixed.Value[id], raw => { raw["type"] = type; raw["text"] = ""; raw["mechanics"] = new JsonArray(); raw["attack"] = 1; raw["health"] = 1; });
                var moved = Replace(frame, Card(30, id, 1, "HAND", type, ("COST", "0"), ("ATK", "1"), ("HEALTH", "1"), ("DAMAGE", "0"), ("CHARGE", "1")));
                RejectedBefore(Eval(moved, With(impostor), new PlannedAction(ActionKind.PlayCard, 30)), moved);
            }
        }
        var modifiedCost = Replace(arcane, Hand(30, "CORE_DS1_185", 3));
        Check(Eval(modifiedCost, new PlannedAction(ActionKind.PlayCard, 30, 21)).Result.AvailableMana == 1, "Native audit replaced an observed current cost with printed cost.");
        var unknownCost = Tag(arcane, 30, "COST", null);
        Check(Eval(unknownCost, new PlannedAction(ActionKind.PlayCard, 30, 21)).UnsupportedMechanics.Contains("UNKNOWN_CURRENT_COST"), "Metadata filled a missing current cost.");
    }

    private static void EntityEvidence()
    {
        var frame = Frame(4, 2, Hand(30, "CORE_DS1_185", 1));
        var absentType = Tag(frame, 30, "CARDTYPE", null);
        Check(Eval(absentType, new PlannedAction(ActionKind.PlayCard, 30, 21)).IsLethal, "Pinned complete native metadata cannot establish an omitted inherent type.");
        foreach (var (tag, value) in new[] { ("CARDTYPE", "MINION"), ("CARDTYPE", "bad"), ("202", "4"), ("202", "bad"),
            ("50", "2"), ("50", "bad"), ("ENTITY_ID", "31"), ("53", "31") })
            RejectedBefore(Eval(Tag(frame, 30, tag, value), new PlannedAction(ActionKind.PlayCard, 30, 21)), frame);
        var unknownKind = Replace(frame, frame.Entities[30] with { Kind = EntityKind.Unknown });
        RejectedBefore(Eval(unknownKind, new PlannedAction(ActionKind.PlayCard, 30, 21)), frame);
        foreach (var key in new[] { "ATK", "HEALTH" })
        {
            var missing = Tag(Frame(10, 2, Body(30, "EDR_480", 1, 3, 8)), 30, key, null);
            var result = Eval(missing, new PlannedAction(ActionKind.Attack, 30, 21));
            Check(result.Verification != LineVerification.Verified && !result.IsLethal, "Fixed metadata supplied an unobserved combat attribute.");
        }
    }

    private static void DeathSources()
    {
        var wolf = Card(12, "EDR_850p", 1, "PLAY", "HERO_POWER", ("COST", "2"), ("EXHAUSTED", "0"),
            ("TAG_SCRIPT_DATA_NUM_1", "1"), ("TAG_SCRIPT_DATA_NUM_2", "1"));
        var frame = Frame(10, 30, Body(30, "EDR_227", 1, 1, 1, ("DEATHRATTLE", "1")), Body(40, "VANILLA", 2, 1, 2), wolf);
        var good = Eval(frame, new PlannedAction(ActionKind.Attack, 30, 40));
        Check(good.Verification == LineVerification.Verified && good.Result.HeroPowerChanges.Count == 1 &&
            good.Result.HeroPowerChanges[0].CurrentLevel == 2, "Audited native death no longer imbues the current power.");
        foreach (var id in new[] { "EDR_227", "EDR_850p" })
            foreach (var bad in Mutations(id))
            {
                var result = Eval(frame, With(bad), new PlannedAction(ActionKind.Attack, 30, 40));
                Check(result.Verification != LineVerification.Verified && !result.IsLethal && result.Result.HeroPowerChanges.Count == 0,
                    "Native death or a changed destination power used only its base ID.");
            }
        var dino = Frame(10, 30, Body(30, "DINO_421", 1, 8, 1, ("DEATHRATTLE", "1")), Body(40, "VANILLA", 2, 1, 30),
            Hand(31, "EDR_852", 3, 2, 3, ("CARDRACE", "20")));
        var buffed = Eval(dino, new PlannedAction(ActionKind.Attack, 30, 40));
        Check(buffed.Verification == LineVerification.Verified && Find(buffed, 31).Attack == 5 && Find(buffed, 31).MaximumHealth == 6,
            "Audited native death failed to use current source and target stats.");
        var badTarget = Changed(Fixed.Value["EDR_852"], raw => raw["text"] = "不同的种族或使用效果");
        var rejected = Eval(dino, With(badTarget), new PlannedAction(ActionKind.Attack, 30, 40));
        Check(rejected.Verification != LineVerification.Verified && rejected.UnsupportedMechanics.Contains("UNAUDITED_NATIVE_DEFINITION") && Find(rejected, 31).Attack == 2,
            "A native death applied a racial buff using a corrupted hand target definition.");
    }

    private static void EnchantmentSources()
    {
        var frame = Frame(0, 5, Hand(30, "CORE_DS1_185", 3), Card(50, "EDR_489e1", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "11")));
        var good = Eval(frame, new PlannedAction(ActionKind.PlayCard, 30, 21));
        Check(good.Verification == LineVerification.Verified && good.IsLethal && good.Result.OpponentHealthPayments.Single().Cost == 3 && good.Result.AvailableMana == 0,
            "Audited observed health payment changed or charged printed cost.");
        foreach (var bad in Mutations("EDR_489e1")) RejectedBefore(Eval(frame, With(bad), new PlannedAction(ActionKind.PlayCard, 30, 21)), frame);
        var stat = Frame(10, 4, Body(30, "BEAST", 1, 4, 8), Card(50, "EDR_850pe1", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "30")));
        Check(Eval(stat, new PlannedAction(ActionKind.Attack, 30, 21)).IsLethal, "Current stat-only enchantment was reapplied or rejected.");
        foreach (var bad in Mutations("EDR_850pe1")) RejectedBefore(Eval(stat, With(bad), new PlannedAction(ActionKind.Attack, 30, 21)), stat);
    }

    private static void DirectEffects()
    {
        var discoveryIds = new[] { "CORE_DS1_184", "TLC_824", "EDR_888", "JAIL_831", "BE_044", "CORE_EDR_004", "CORE_EDR_004_2026" };
        foreach (var id in discoveryIds)
        {
            var frame = Frame(10, 30, Hand(30, id, 1, 3, 3, ("BATTLECRY", "1"), ("REWIND", "1")));
            Check(DiscoveryPlayRules.Evaluate(frame, 30, Fixed.Value).EffectActive == true, "Fixed discovery source was not recognized: " + id);
            foreach (var bad in Mutations(id)) Check(DiscoveryPlayRules.Evaluate(frame, 30, With(bad)).EffectActive is null,
                "Direct discovery accepted altered complete source: " + id);
        }
        var draw = Frame(10, 30, Hand(30, "FIR_921", 4, 3, 4, ("BATTLECRY", "1")));
        Check(DrawResolutionRules.Evaluate(draw, 30, Fixed.Value, true).RequestedDraws.Minimum == 2, "Fixed native draw lost its known condition.");
        var generate = Frame(10, 30, Hand(30, "CORE_UNG_912", 1, 1, 1, ("BATTLECRY", "1")));
        Check(GenerationResolutionRules.Evaluate(generate, 30, Fixed.Value).RequestedCards.Minimum == 1, "Fixed native generation lost its direct count.");
        foreach (var bad in Mutations("FIR_921")) Check(!DrawResolutionRules.Evaluate(draw, 30, With(bad), true).ResolvesEffects, "Direct draw borrowed a corrupted source ID.");
        foreach (var bad in Mutations("CORE_UNG_912")) Check(!GenerationResolutionRules.Evaluate(generate, 30, With(bad)).ResolvesEffects, "Direct generation borrowed a corrupted source ID.");
        var wolf = Frame(10, 30, Card(12, "EDR_850p", 1, "PLAY", "HERO_POWER", ("COST", "2"), ("EXHAUSTED", "0"),
            ("TAG_SCRIPT_DATA_NUM_1", "2"), ("TAG_SCRIPT_DATA_NUM_2", "2")), Hand(30, "EDR_227", 4, 5, 2, ("CARDRACE", "20")));
        Check(WolfBlessingResolutionRules.Evaluate(wolf, 12, Fixed.Value).Branches.Single().ResultingAttack == 7, "Fixed Wolf direct preview changed.");
        foreach (var bad in Mutations("EDR_850p")) Check(!WolfBlessingResolutionRules.Evaluate(wolf, 12, With(bad)).ResolvesEffects, "Direct Wolf borrowed a changed source ID.");
        var quest = Frame(10, 30, Card(50, "TLC_EVENT_400", 1, "SECRET", "SPELL", ("QUEST_PROGRESS", "1"), ("QUEST_PROGRESS_TOTAL", "3")),
            Hand(30, "EDR_227", 4, 5, 2, ("CARDRACE", "20")));
        Check(QuestProgressRules.Evaluate(quest, 50, Fixed.Value, quest.Entities[30]).ResultingProgress == 2, "Native quest direct progress changed.");
        foreach (var bad in Mutations("TLC_EVENT_400")) Check(!QuestProgressRules.Evaluate(quest, 50, With(bad), quest.Entities[30]).CanProjectProgress, "Direct quest borrowed a changed source ID.");
    }

    private static void ChoiceSources()
    {
        foreach (var id in new[] { "CORE_DS1_184", "TLC_824", "JAIL_831", "EDR_888" })
        {
            var frame = Choice(id);
            var good = Evaluator.EvaluateChoice(frame, [81], Fixed.Value);
            Check(good.ResolvesEffects, "Fixed current choice failed: " + id + " " + JsonSerializer.Serialize(good));
            foreach (var bad in Mutations(id))
            {
                var rejected = Evaluator.EvaluateChoice(frame, [81], With(bad));
                Check(rejected.Verification == LineVerification.Unsupported && !rejected.ResolvesEffects && rejected.CardOutcomes.Count == 0,
                    "Offered choice granted effects using a changed complete source: " + id);
            }
        }
        var malorne = Choice("EDR_888");
        foreach (var bad in Mutations("EDR_480"))
        {
            var result = MalorneChoiceRules.Evaluate(malorne, 81, With(bad));
            Check(!result.ResolvesEffects && result.GrantedSetCost is null, "Fixed offered Wild God grant accepted altered metadata.");
        }
    }

    private static void RelevantScope()
    {
        var bad = Changed(Fixed.Value["EDR_480"], raw => raw["text"] = "未覆盖的能力");
        var unrelated = Frame(10, 3, Body(30, "VANILLA", 1, 3, 8), Hand(40, "EDR_480", 9),
            Card(41, "EDR_480", 1, "DECK", "MINION"), Card(42, "EDR_480", 2, "GRAVEYARD", "MINION"),
            Card(12, "HERO_05bp", 1, "PLAY", "HERO_POWER", ("COST", "2"), ("EXHAUSTED", "0")));
        var cards = With(bad); cards["HERO_05bp"] = Changed(cards["HERO_05bp"], raw => raw["text"] = "不同的未使用技能");
        var good = Eval(unrelated, cards, new PlannedAction(ActionKind.Attack, 30, 21));
        Check(good.Verification == LineVerification.Verified && good.IsLethal, "Unused hidden/history/hand/power definitions blocked an unrelated native-free attack.");
        RejectedBefore(Eval(unrelated, cards, new PlannedAction(ActionKind.HeroPower, 12)), unrelated);
        RejectedBefore(Eval(unrelated, cards, new PlannedAction(ActionKind.PlayCard, 40)), unrelated);
        var unknown = Replace(Frame(10, 3, Body(30, "VANILLA", 1, 3, 8)), Card(51, "UNREVIEWED_ENCHANTMENT", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "30")));
        var conditional = Eval(unknown, new PlannedAction(ActionKind.Attack, 30, 21));
        Check(conditional.Verification != LineVerification.Verified && !conditional.IsLethal && conditional.UnsupportedMechanics.Contains("UNMAPPED_ATTACHED_ENCHANTMENT"),
            "The fixed registry made unknown attached effects globally safe.");
    }

    private static IEnumerable<CardRecord> Mutations(string id)
    {
        var original = Fixed.Value[id];
        yield return Changed(original, raw => raw["dbfId"] = original.DbfId + 1);
        yield return Changed(original, raw => raw["text"] = "不能按原生牌效结算。");
        yield return Changed(original, raw => raw["mechanics"] = new JsonArray("TRIGGER_VISUAL", "DEATHRATTLE"));
    }
    private static CardRecord Changed(CardRecord card, Action<JsonObject> mutate)
    {
        var raw = JsonNode.Parse(card.RawJson)!.AsObject(); mutate(raw);
        using var doc = JsonDocument.Parse(raw.ToJsonString()); return ReadCard(doc.RootElement);
    }
    private static Dictionary<string, CardRecord> With(CardRecord card) { var result = Fixed.Value.ToDictionary(); result[card.Id] = card; return result; }
    private static LineEvaluation Eval(GameSnapshot frame, params PlannedAction[] actions) => Eval(frame, Fixed.Value, actions);
    private static LineEvaluation Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, params PlannedAction[] actions) => Evaluator.EvaluateLine(frame, actions, cards);
    private static SimulatedEntity Find(LineEvaluation result, int id) => result.Result.Entities.Single(entity => entity.EntityId == id);
    private static void RejectedBefore(LineEvaluation result, GameSnapshot frame)
    {
        Check(result.Verification == LineVerification.Unsupported && !result.IsLethal && result.AppliedActions == 0 &&
            result.UnsupportedMechanics.Contains("UNAUDITED_NATIVE_DEFINITION") && result.Result.AvailableMana == frame.Entities[2].GetIntTag("RESOURCES") &&
            Find(result, 21).RemainingHealth == frame.Entities[21].RemainingHealth,
            "A native mismatch reached payment, damage or another handler: " + JsonSerializer.Serialize(result));
    }
    private static GameSnapshot Choice(string source)
    {
        var frame = Frame(10, 30, Card(30, source, 1, source is "CORE_DS1_184" or "TLC_824" ? "GRAVEYARD" : "PLAY", Fixed.Value[source].Type),
            Card(81, "EDR_480", 1, "SETASIDE", "MINION", ("COST", "9")), Card(82, "EDR_227", 1, "SETASIDE", "MINION", ("COST", "4")));
        frame = Tag(frame, 2, "3527", "4");
        ChoiceOptionSnapshot[] options = [new(81, "EDR_480", 1, 1), new(82, "EDR_227", 1, 2)];
        if (source == "TLC_824")
        {
            frame = Replace(frame, Card(80, "CORE_UNG_912", 1, "SETASIDE", "MINION", ("COST", "1")));
            options = [new(80, "CORE_UNG_912", 1, 0), ..options];
        }
        if (source == "EDR_888")
        {
            frame = Replace(frame, Card(82, "EDR_489", 1, "SETASIDE", "MINION", ("COST", "7")));
            options = [options[0], new(82, "EDR_489", 1, 2)];
        }
        return frame with { Phase = GamePhase.Choice, Choice = new(7, 1, "GENERAL", 30, 1, 1, options, true)
            { SourceCardId = source, OfferedAtEventSequence = 90 } };
    }
    private static GameSnapshot Frame(int mana, int enemyHealth, params EntitySnapshot[] extra)
    {
        EntitySnapshot Player(int id, int owner, int hero) => new(id, null, EntityKind.Player, new Dictionary<string, string>
        { ["PLAYER_ID"] = owner.ToString(), ["CONTROLLER"] = owner.ToString(), ["HERO_ENTITY"] = hero.ToString(), ["RESOURCES"] = mana.ToString(),
            ["RESOURCES_USED"] = "0", ["TEMP_RESOURCES"] = "0", ["MAXRESOURCES"] = "10", ["MAXHANDSIZE"] = "10", ["NUM_CARDS_IN_DECK"] = "3", ["FATIGUE"] = "0" }.ToFrozenDictionary());
        var all = new[] { Player(2, 1, 11), Player(3, 2, 21), Card(11, "HERO_05", 1, "PLAY", "HERO", ("CLASS", "HUNTER"), ("HEALTH", "30"), ("DAMAGE", "0"), ("ATK", "0"), ("ARMOR", "0")),
            Card(21, "HERO_08", 2, "PLAY", "HERO", ("CLASS", "MAGE"), ("HEALTH", enemyHealth.ToString()), ("DAMAGE", "0"), ("ATK", "0"), ("ARMOR", "0")) }.Concat(extra).ToFrozenDictionary(entity => entity.Id);
        return new("native-definition-checks", 10, 100, 1, 1, 1, GamePhase.Main, all, new(true, true, true, true, 0, [])) { ClientBuild = "255790", Format = "Standard" };
    }
    private static EntitySnapshot Card(int id, string card, int owner, string zone, string type, params (string Key, string Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = type };
        foreach (var (key, value) in extra) tags[key] = value;
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Body(int id, string card, int owner, int attack, int health, params (string Key, string Value)[] extra) =>
        Card(id, card, owner, "PLAY", "MINION", [("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("ARMOR", "0"), ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1"), ..extra]);
    private static EntitySnapshot Hand(int id, string card, int cost, int attack = 3, int health = 3, params (string Key, string Value)[] extra) =>
        Card(id, card, 1, "HAND", Fixed.Value[card].Type, [("COST", cost.ToString()), ("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ..extra]);
    private static GameSnapshot Tag(GameSnapshot frame, int id, string tag, string? value)
    { var entity = frame.Entities[id]; var tags = entity.Tags.ToDictionary(); if (value is null) tags.Remove(tag); else tags[tag] = value; return Replace(frame, entity with { Tags = tags.ToFrozenDictionary() }); }
    private static GameSnapshot Replace(GameSnapshot frame, params EntitySnapshot[] replacements)
    { var entities = frame.Entities.ToDictionary(); foreach (var entity in replacements) entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot Parsed(GameSnapshot frame)
    {
        var engine = new PowerLogStateEngine(1, frame.MatchId);
        foreach (var line in new[] { "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0" }) engine.ProcessLine(line);
        foreach (var entity in frame.Entities.Values.OrderBy(entity => entity.Kind == EntityKind.Player ? 0 : 1).ThenBy(entity => entity.Id))
        {
            engine.ProcessLine(entity.Kind == EntityKind.Player ? $"Player EntityID={entity.Id} PlayerID={entity.GetIntTag("PLAYER_ID")}" : $"FULL_ENTITY - Creating ID={entity.Id} CardID={entity.CardId}");
            foreach (var tag in entity.Tags) engine.ProcessLine($"tag={tag.Key} value={tag.Value}");
        }
        foreach (var line in new[] { "TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value=1", "TAG_CHANGE Entity=3 tag=CURRENT_PLAYER value=0", "TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete" }) engine.ProcessLine(line);
        return engine.Snapshot();
    }
    private static IReadOnlyDictionary<string, CardRecord> ReadCards()
    {
        var result = new Dictionary<string, CardRecord>();
        foreach (var name in new[] { "native-definitions-255790.json", "lethal-search-255790.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            foreach (var raw in doc.RootElement.GetProperty("cards").EnumerateArray()) { var card = ReadCard(raw); result[card.Id] = card; }
        }
        foreach (var (id, race) in new[] { ("VANILLA", (string?)null), ("BEAST", "BEAST") })
        {
            var raw = new JsonObject { ["id"] = id, ["dbfId"] = 1, ["name"] = id, ["text"] = "", ["cardClass"] = "NEUTRAL", ["set"] = "TEST", ["type"] = "MINION", ["cost"] = 1, ["attack"] = 1, ["health"] = 1 };
            if (race is not null) raw["race"] = race;
            using var doc = JsonDocument.Parse(raw.ToJsonString()); result[id] = ReadCard(doc.RootElement);
        }
        return result;
    }
    private static CardRecord ReadCard(JsonElement raw)
    {
        string Text(string name) => raw.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        string[] List(string name) => raw.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        return new(Text("id"), raw.GetProperty("dbfId").GetInt32(), Text("name"), Text("text"), Text("cardClass"), Text("set"), Text("type"),
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), List("classes"), List("entourage"), raw.GetRawText());
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
