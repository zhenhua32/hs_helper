using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class WolfBlessingResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Wolf preview lists every observed Beast branch without choosing a random result", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame, cards);
            Check(result.ResolvesEffects && result.IsCandidateSetComplete && result.Level == 2 && result.Branches.Count == 2 &&
                result.Branches[0] is { TargetEntityId: 30, CardId: "WOLF_A", CurrentAttack: 3, CurrentCost: 6, ResultingAttack: 5, ResultingCost: 4 } &&
                result.Branches[1] is { TargetEntityId: 31, CurrentAttack: 7, CurrentCost: 1, ResultingAttack: 9, ResultingCost: 0 } &&
                result.IsRandom && result.IsLegal is null && !result.CanChooseBranch && !result.CanEvaluateFollowupActions &&
                !result.HasAuthoritativeNextState && before == JsonSerializer.Serialize(frame), "Random preview selected a branch or overwrote observed values.");
        });
        yield return ("Wolf preview leaves missing conflicting and overflowing branch numbers unknown", () =>
        {
            var frame = Replace(Frame(), Hand(30, "WOLF_A", null, 0));
            var result = Eval(frame, cards);
            Check(result.Branches[0].CurrentAttack is null && result.Branches[0].ResultingAttack is null && result.Branches[0].ResultingCost == 0,
                "Missing attack borrowed printed attack.");
            frame = Replace(frame, Tagged(Hand(30, "WOLF_A", 3, 6), ("47", "4"), ("48", "bad")));
            result = Eval(frame, cards);
            Check(result.Branches[0].CurrentAttack is null && result.Branches[0].CurrentCost is null &&
                result.Branches[0].ResultingAttack is null && result.Branches[0].ResultingCost is null, "Stats ignored a contradictory alias.");
            frame = Replace(frame, Hand(30, "WOLF_A", int.MaxValue, int.MaxValue));
            result = Eval(frame, cards);
            Check(result.Branches[0].ResultingAttack is null && result.Branches[0].ResultingCost == int.MaxValue - 2,
                "Attack overflow wrapped or destroyed an independently known cost.");
        });
        yield return ("Wolf current-level aliases must be present positive equal and consistent", () =>
        {
            foreach (var tags in new[] { new[] { ("TAG_SCRIPT_DATA_NUM_1", "bad") },
                new[] { ("2", "3") }, new[] { ("TAG_SCRIPT_DATA_NUM_2", "3") },
                new[] { ("TAG_SCRIPT_DATA_NUM_1", "0"), ("TAG_SCRIPT_DATA_NUM_2", "0") },
                new[] { ("TAG_SCRIPT_DATA_NUM_1", "-1"), ("TAG_SCRIPT_DATA_NUM_2", "-1") } })
                Check(!Eval(Replace(Frame(), Tagged(Power(), tags)), cards).ResolvesEffects, "Bad source parameters became a known level.");
            var missingTags = Power().Tags.Where(pair => !pair.Key.StartsWith("TAG_SCRIPT_DATA_NUM_", StringComparison.Ordinal)).ToFrozenDictionary();
            Check(Eval(Replace(Frame(), Power() with { Tags = missingTags }), cards).Level is null, "Missing level became printed level one.");
            var numeric = missingTags.ToDictionary(); numeric["2"] = "2"; numeric["3"] = "2";
            Check(Eval(Replace(Frame(), Power() with { Tags = numeric.ToFrozenDictionary() }), cards).Level == 2, "Consistent numeric parameter aliases were lost.");
        });
        yield return ("Wolf overlays cannot repair nonpositive or invalid original power parameters", () =>
        {
            foreach (var value in new[] { "0", "-1", "bad" })
            {
                var frame = Replace(Frame(), Tagged(Power(), ("TAG_SCRIPT_DATA_NUM_1", value), ("TAG_SCRIPT_DATA_NUM_2", value)));
                Check(!Eval(frame, cards, Overlay(frame, 2, frame.Hand.ToArray())).ResolvesEffects,
                    "A valid overlay level repaired an invalid original source parameter.");
            }
        });
        yield return ("Wolf source and candidate numeric type aliases cannot contradict named types", () =>
        {
            var frame = Replace(Frame(), Tagged(Power(), ("202", "SPELL")));
            Check(!Eval(frame, cards).ResolvesEffects, "Source ignored a contradictory numeric card type.");
            frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("202", "SPELL")));
            var result = Eval(frame, cards);
            Check(!result.IsCandidateSetComplete && !result.ResolvesEffects && result.Branches.All(branch => branch.TargetEntityId != 30),
                "A contradictory numeric candidate type entered the exact Beast pool.");
            frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("202", "4")));
            Check(Eval(frame, cards).IsCandidateSetComplete, "Equivalent named/numeric types were rejected.");
        });
        yield return ("Wolf bound overlays use changed current hand values and membership without old count refill", () =>
        {
            var frame = Frame();
            var overlay = Overlay(frame, 4, [Hand(31, "WOLF_B", 10, 3)]);
            var result = Eval(frame, cards, overlay);
            Check(result.ResolvesEffects && result.Level == 4 && result.Branches.Single() is
                { TargetEntityId: 31, CurrentAttack: 10, CurrentCost: 3, ResultingAttack: 14, ResultingCost: 0 },
                "Current simulation membership/values were replaced by old snapshot hand values.");
            result = Eval(frame, cards, Overlay(frame, 4, [Hand(31, "WOLF_B", null, null)]));
            Check(result.Branches.Single() is { CurrentAttack: null, CurrentCost: null, ResultingAttack: null, ResultingCost: null },
                "An overlay unknown was filled with old or printed stats.");
            result = Eval(frame, cards, Overlay(frame, null, frame.Hand.ToArray()));
            Check(result.Level is null && !result.ResolvesEffects && result.Branches.All(branch => branch.ResultingAttack is null && branch.ResultingCost is null),
                "Unknown simulated level borrowed the old snapshot level.");
        });
        yield return ("Wolf overlays require exact match state and sequence and cannot inject malformed hand sets", () =>
        {
            var frame = Frame(); var valid = Overlay(frame, 2, frame.Hand.ToArray());
            foreach (var overlay in new[] { valid with { MatchId = "other" }, valid with { StateVersion = 9 }, valid with { EventSequence = 101 },
                valid with { HandBeforeBlessing = [Hand(30, "WOLF_A", 3, 6), Hand(30, "WOLF_A", 3, 6)] },
                valid with { HandBeforeBlessing = [Hand(30, "WOLF_A", 3, 6, owner: 2)] },
                valid with { HandBeforeBlessing = [Hand(30, "WOLF_A", 3, 6, zone: "PLAY")] },
                valid with { HandBeforeBlessing = null! }, valid with { CurrentSourceCardId = "HERO_05bp" } })
                Check(!Eval(frame, cards, overlay).ResolvesEffects, "Unbound source/overlay supplied resolved branches.");
        });
        yield return ("Wolf overlay accepts an explicitly current native power after a covered identity replacement", () =>
        {
            var frame = Replace(Frame(), Power() with { CardId = "HERO_05bp" });
            Check(!Eval(frame, cards).ResolvesEffects, "Basic Steady Shot was guessed to be Wolf Blessing.");
            var result = Eval(frame, cards, Overlay(frame, 3, frame.Hand.ToArray()) with { CurrentSourceCardId = "EDR_850p" });
            Check(result.ResolvesEffects && result.SourceCardId == "EDR_850p" && result.Branches[0].ResultingAttack == 6,
                "A bound current native identity/level was replaced by the old source identity.");
        });
        yield return ("Wolf identity gaps do not silently remove unknown random candidates", () =>
        {
            var frame = Replace(Frame(), Hand(30, "WOLF_A", 3, 6) with { CardId = null });
            var result = Eval(frame, cards);
            Check(!result.ResolvesEffects && !result.IsCandidateSetComplete && result.Branches.Count == 1 && result.Branches[0].TargetEntityId == 31,
                "A hidden identity was omitted while claiming a complete candidate pool.");
            var tags = frame.Entities[1].Tags.ToDictionary(); tags["NUM_CARDS_IN_HAND"] = "9";
            Check(!Eval(Replace(Frame(), Frame().Entities[1] with { Tags = tags.ToFrozenDictionary() }), cards).IsCandidateSetComplete,
                "Conflicting observed hand count was ignored.");
        });
        yield return ("Wolf respects observed Beast race changes and the audited native dual-type card", () =>
        {
            var frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("CARDRACE", "15")));
            Check(Eval(frame, cards).Branches.All(branch => branch.TargetEntityId != 30), "Printed Beast race overwrote observed Demon.");
            frame = Replace(Frame(), Tagged(Hand(30, "CORE_TTN_866", 4, 7), ("CARDRACE", "15")));
            Check(Eval(frame, cards).Branches.Any(branch => branch.TargetEntityId == 30), "Audited Demon/Beast lost its secondary Beast type.");
            frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("200", "15")));
            Check(!Eval(frame, cards).IsCandidateSetComplete, "Conflicting race aliases were repaired by printed Beast metadata.");
        });
        yield return ("Wolf custom text and unreviewed immunity never masquerade as final branch stats", () =>
        {
            var frame = Replace(Frame(), Tagged(Power(), ("4572", "1")));
            Check(!Eval(frame, cards).ResolvesEffects, "An assembled power executed its base native buff.");
            frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("4573", "bad")));
            var result = Eval(frame, cards, Overlay(frame, 2, [Hand(30, "WOLF_A", 3, 6), Hand(31, "WOLF_B", 7, 1)]));
            Check(result.Branches[0] is { CurrentAttack: 3, CurrentCost: 6, ResultingAttack: null, ResultingCost: null },
                "Overlay discarded raw custom-text evidence.");
            frame = Replace(Frame(), Tagged(Hand(30, "WOLF_A", 3, 6), ("IMMUNE", "1")));
            result = Eval(frame, cards);
            Check(result.Branches.Count == 2 && result.Branches[0].ResultingAttack is null && result.Branches[0].ResultingCost is null,
                "An unreviewed immunity was silently excluded or assumed to allow exact post-buff stats.");
        });
        yield return ("real Draenei Flower Attendant is a known non-Beast and does not invalidate Wolf branches", () =>
        {
            foreach (var observed in new string?[] { "2", "DRAENEI", null })
            {
                var frame = Frame();
                var attendant = Tagged(Hand(32, "FIR_921", 2, 3), ("CARDRACE", observed ?? "2"));
                if (observed is null) attendant = attendant with { Tags = attendant.Tags.Where(pair => pair.Key != "CARDRACE").ToFrozenDictionary() };
                frame = Replace(frame, attendant);
                frame = Replace(frame, Tagged(frame.Entities[1], ("NUM_CARDS_IN_HAND", "3")));
                var result = Eval(frame, cards);
                Check(result.ResolvesEffects && result.IsCandidateSetComplete && result.Branches.Select(branch => branch.TargetEntityId).SequenceEqual([30, 31]),
                    "Fixed Draenei metadata became an unknown Beast pool or an extra random branch.");
            }
        });
        yield return ("Wolf preview is not activation legality and rejects unsafe source perspective or build", () =>
        {
            var frame = Replace(Frame(), Tagged(Power(), ("EXHAUSTED", "1")));
            Check(Eval(frame, cards).ResolvesEffects && Eval(frame, cards).IsLegal is null, "Hypothetical effect preview claimed legal activation.");
            foreach (var invalid in new[] { Frame() with { ClientBuild = "other" }, Frame() with { IsSpectating = true },
                Frame() with { ActivePlayerId = 2 }, Frame() with { Quality = Frame().Quality with { IsStable = false } },
                Replace(Frame(), Power() with { CardId = null }), Replace(Frame(), Tagged(Power(), ("CONTROLLER", "2"))) })
                Check(!Eval(invalid, cards).ResolvesEffects, "Unsafe source or perspective supplied native branches.");
        });
        yield return ("Wolf empty known hand does not claim an activation or fabricate a Beast", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, Overlay(frame, 2, []));
            Check(result.IsCandidateSetComplete && !result.ResolvesEffects && result.Branches.Count == 0 && result.IsLegal is null,
                "An empty hand gained a fabricated random candidate or a proven legal empty activation.");
        });
        yield return ("Wolf branch lists and source bindings remain immutable projections", () =>
        {
            var frame = Frame(); var hand = frame.Hand.ToList(); var result = Eval(frame, cards, Overlay(frame, 2, hand)); hand.Clear();
            Check(result.Branches.Count == 2 && result.MatchId == frame.MatchId && result.StateVersion == frame.StateVersion &&
                result.EventSequence == frame.EventSequence && result.SourceEntityId == 12, "Source list mutation changed an earlier branch projection.");
            try { ((IList<WolfBlessingBranch>)result.Branches).Clear(); throw new Exception("Mutable Wolf branches."); }
            catch (NotSupportedException) { }
        });
    }

    private static WolfBlessingResolutionOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, WolfBlessingResolutionState? overlay = null) =>
        WolfBlessingResolutionRules.Evaluate(frame, 12, cards, overlay);
    private static WolfBlessingResolutionState Overlay(GameSnapshot frame, int? level, IReadOnlyList<EntitySnapshot> hand) =>
        new(frame.MatchId, frame.StateVersion, frame.EventSequence, level, hand);
    private static GameSnapshot Frame() => new("wolf-preview", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["NUM_CARDS_IN_HAND"] = "2", ["HERO_POWER_ENTITY"] = "12" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Power(), Hand(30, "WOLF_A", 3, 6), Hand(31, "WOLF_B", 7, 1) }.ToFrozenDictionary(entity => entity.Id),
        new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Power() => new(12, "EDR_850p", EntityKind.Card, new Dictionary<string, string>
    { ["ZONE"] = "PLAY", ["CONTROLLER"] = "1", ["CARDTYPE"] = "HERO_POWER", ["TAG_SCRIPT_DATA_NUM_1"] = "2", ["TAG_SCRIPT_DATA_NUM_2"] = "2" }.ToFrozenDictionary());
    private static EntitySnapshot Hand(int id, string card, int? attack, int? cost, int owner = 1, string zone = "HAND")
    {
        var tags = new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION", ["CARDRACE"] = "20" };
        if (attack.HasValue) tags["ATK"] = attack.Value.ToString(); if (cost.HasValue) tags["COST"] = cost.Value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Tagged(EntitySnapshot entity, params (string Key, string Value)[] values)
    { var tags = entity.Tags.ToDictionary(); foreach (var (key, value) in values) tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        var cards = new Dictionary<string, CardRecord>(StringComparer.Ordinal);
        foreach (var name in new[] { "beast-hunter-255790.json", "mythical-terror-255790.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Unpinned Wolf fixture.");
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
                    raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        }
        foreach (var id in new[] { "WOLF_A", "WOLF_B" }) cards[id] = new(id, null, id, "", "NEUTRAL", "SYNTHETIC", "MINION", 777, false, [], [],
            "{\"race\":\"BEAST\",\"attack\":777,\"health\":777}");
        return cards;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
