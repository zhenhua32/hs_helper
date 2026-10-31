using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterWolfLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        LineEvaluation Evaluate(GameSnapshot frame, params PlannedAction[] actions) =>
            new LineEvaluator().EvaluateLine(frame, actions.Length == 0 ? [new(ActionKind.HeroPower, 12)] : actions, cards);
        yield return ("multiple Wolf candidates expose mutually exclusive outcomes without changing either card", () =>
        {
            var frame = Frame(); var before = System.Text.Json.JsonSerializer.Serialize(frame); var result = Evaluate(frame);
            var preview = result.Result.WolfBlessingPreviews.Single();
            Check(result.Verification == LineVerification.Conditional && result.AppliedActions == 1 && result.Result.AvailableMana == 8 &&
                result.Result.WolfBlessingBoundaryReached && preview.Branches.Count == 2 && preview.IsCandidateSetComplete &&
                preview.Branches.Single(branch => branch.TargetEntityId == 30) is { CurrentAttack: 5, CurrentCost: 1, ResultingAttack: 7, ResultingCost: 0 } &&
                preview.Branches.Single(branch => branch.TargetEntityId == 31) is { CurrentAttack: 9, CurrentCost: 9, ResultingAttack: 11, ResultingCost: 7 } &&
                result.Result.Entities.Single(entity => entity.EntityId == 30) is { Attack: 5, Cost: 1 } &&
                result.Result.Entities.Single(entity => entity.EntityId == 31) is { Attack: 9, Cost: 9 } && !result.IsLethal &&
                before == System.Text.Json.JsonSerializer.Serialize(frame), "A Wolf branch was selected, merged, or written into the authoritative projection.");
        });
        yield return ("a multiple-candidate Wolf boundary rejects subsequent plays at the applied prefix", () =>
        {
            var result = Evaluate(Frame(), new(ActionKind.HeroPower, 12), new(ActionKind.PlayCard, 30));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 1 &&
                result.UnsupportedMechanics.Contains("AWAIT_WOLF_BLESSING_SNAPSHOT") && result.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND",
                "A random discount was guessed to validate the next play.");
        });
        yield return ("Wolf branches use a preceding covered Imbue and current hand rather than old snapshot parameters", () =>
        {
            var frame = HunterGenerationLineChecks.Add(Frame(), HunterGenerationLineChecks.Card(32, "EDR_852", "HAND", 1));
            frame = HunterGenerationLineChecks.Player(frame, ("NUM_CARDS_IN_HAND", "3"));
            var result = Evaluate(frame, new(ActionKind.PlayCard, 32), new(ActionKind.HeroPower, 12));
            var preview = result.Result.WolfBlessingPreviews.Single();
            Check(result.AppliedActions == 2 && preview.Level == 3 && preview.Branches.Count == 2 &&
                preview.Branches.Single(branch => branch.TargetEntityId == 30).ResultingAttack == 8 &&
                preview.Branches.Single(branch => branch.TargetEntityId == 31).ResultingCost == 6,
                "Wolf branch preview reused the pre-Imbue level or an already played hand entity.");
        });
        yield return ("a single known Wolf candidate retains deterministic continuation", () =>
        {
            var frame = Frame(); frame = frame with { Entities = frame.Entities.Where(pair => pair.Key != 31).ToFrozenDictionary() };
            frame = HunterGenerationLineChecks.Player(frame, ("NUM_CARDS_IN_HAND", "1"));
            var result = Evaluate(frame, new(ActionKind.HeroPower, 12), new(ActionKind.PlayCard, 30));
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 2 && !result.Result.WolfBlessingBoundaryReached &&
                result.Result.WolfBlessingPreviews.Single().Branches.Count == 1 && result.Result.Entities.Single(entity => entity.EntityId == 30) is { Attack: 7, Cost: 0, Zone: "PLAY" },
                "The new branch display broke the deterministic single-candidate path.");
        });
        yield return ("conflicting or malformed Wolf parameter aliases fail before payment", () =>
        {
            foreach (var value in new[] { "1", "bad" })
            {
                var frame = Frame(); var tags = frame.Entities[12].Tags.ToDictionary(); tags["2"] = value;
                frame = HunterGenerationLineChecks.Add(frame, frame.Entities[12] with { Tags = tags.ToFrozenDictionary() });
                var result = Evaluate(frame);
                Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 0 && result.Result.AvailableMana == 10,
                    "A conflicting raw alias was discarded to invent an exact Wolf level.");
            }
        });
        yield return ("a Wolf runtime overlay cannot repair an originally contradictory observed hand count", () =>
        {
            var result = Evaluate(HunterGenerationLineChecks.Player(Frame(), ("NUM_CARDS_IN_HAND", "99")));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 0 && result.Result.AvailableMana == 10 &&
                result.Result.WolfBlessingPreviews.Count == 0, "The overlay erased an originally incomplete hand candidate set.");
        });
        yield return ("a hand card is not silently removed from random Wolf candidates by its health field", () =>
        {
            var frame = Frame(); var tags = frame.Entities[31].Tags.ToDictionary(); tags["HEALTH"] = "0"; tags["DAMAGE"] = "0";
            frame = HunterGenerationLineChecks.Add(frame, frame.Entities[31] with { Tags = tags.ToFrozenDictionary() });
            var result = Evaluate(frame);
            Check(result.Verification == LineVerification.Conditional && result.Result.WolfBlessingBoundaryReached &&
                result.Result.WolfBlessingPreviews.Single().Branches.Count == 2 && result.Result.Entities.Single(entity => entity.EntityId == 30).Attack == 5,
                "A hand health field shrank the random set to a deterministic single target.");
        });
        yield return ("covered hand buffs update initially consistent numeric aliases before a Wolf preview", () =>
        {
            var frame = Frame();
            foreach (var id in new[] { 30, 31 })
            {
                var tags = frame.Entities[id].Tags.ToDictionary(); tags["47"] = tags["ATK"]; tags["48"] = tags["COST"];
                frame = HunterGenerationLineChecks.Add(frame, frame.Entities[id] with { Tags = tags.ToFrozenDictionary() });
            }
            frame = HunterGenerationLineChecks.Add(frame, HunterGenerationLineChecks.Card(50, "DINO_421", "PLAY", 9,
                ("ATK", "1"), ("HEALTH", "1"), ("DAMAGE", "0"), ("EXHAUSTED", "0"), ("NUM_TURNS_IN_PLAY", "1")));
            frame = HunterGenerationLineChecks.Add(frame, HunterGenerationLineChecks.Card(51, "VANILLA", "PLAY", 1,
                ("CONTROLLER", "2"), ("ATK", "1"), ("HEALTH", "1"), ("DAMAGE", "0")));
            var result = Evaluate(frame, new(ActionKind.Attack, 50, 51), new(ActionKind.HeroPower, 12));
            var preview = result.Result.WolfBlessingPreviews.Single();
            Check(result.AppliedActions == 2 && preview.Branches.Single(branch => branch.TargetEntityId == 30) is { CurrentAttack: 8, ResultingAttack: 10 } &&
                preview.Branches.Single(branch => branch.TargetEntityId == 31) is { CurrentAttack: 12, ResultingAttack: 14 },
                "A legitimate earlier stat buff left stale aliases or backfilled the pre-buff attack.");
        });
    }
    private static GameSnapshot Frame()
    {
        var frame = HunterGenerationLineChecks.Frame();
        frame = HunterGenerationLineChecks.Add(frame, HunterGenerationLineChecks.Card(30, "EDR_227", "HAND", 1, ("ATK", "5"), ("HEALTH", "2")));
        frame = HunterGenerationLineChecks.Add(frame, HunterGenerationLineChecks.Card(31, "EDR_480", "HAND", 9, ("ATK", "9"), ("HEALTH", "9")));
        frame = HunterGenerationLineChecks.Add(frame, new(12, "EDR_850p", EntityKind.Card, new Dictionary<string, string>
        { ["CONTROLLER"] = "1", ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO_POWER", ["COST"] = "2", ["EXHAUSTED"] = "0",
            ["TAG_SCRIPT_DATA_NUM_1"] = "2", ["TAG_SCRIPT_DATA_NUM_2"] = "2" }.ToFrozenDictionary()));
        return HunterGenerationLineChecks.Player(frame, ("NUM_CARDS_IN_HAND", "2"));
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
