using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterMagmaLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> baseCards)
    {
        var cards = baseCards.ToDictionary();
        foreach (var id in new[] { "VANILLA", "TAUNT", "DEATH" })
        {
            var raw = JsonNode.Parse(cards[id].RawJson)!; raw["dbfId"] = cards[id].DbfId;
            cards[id] = cards[id] with { RawJson = raw.ToJsonString() };
        }
        var evaluator = new LineEvaluator();
        yield return ("Magma multi-target line exposes complete split branches while retaining only the base combat projection", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame);
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.Attack, 30, 40)], cards);
            var split = line.Result.MagmaSplitPreviews.Single();
            Check(line.AppliedActions == 1 && line.Verification == LineVerification.Conditional && !line.IsLethal &&
                line.Result.MagmaSplitBoundaryReached && split.TriggerActive == true && split.ResolvesBranches &&
                split.IsEnumerationComplete && split.RequestedPackets == 2 && split.DamagePerPacket == 1 && split.Branches.Count > 1 &&
                Find(line, 40).RemainingHealth == 4 && Find(line, 11).RemainingHealth == 20 && Find(line, 30).AttacksUsed == 1 &&
                line.Result.Entities.Count == frame.Entities.Count && JsonSerializer.Serialize(frame) == before,
                "Magma branches were absent, chosen as actual damage, or written into the original/line board.");
        });
        yield return ("Magma split wait boundary leaves every submitted following action unapplied", () =>
        {
            var line = evaluator.EvaluateLine(Frame(), [new(ActionKind.Attack, 30, 40), new(ActionKind.EndTurn, 2)], cards);
            Check(line.AppliedActions == 1 && !line.Result.TurnEnded && !line.IsLethal &&
                line.UnsupportedMechanics.Contains("AWAIT_MAGMA_SPLIT_SNAPSHOT") && line.Result.MagmaSplitPreviews.Count == 1,
                "The submitted sequence crossed a random split into another action.");
        });
        yield return ("Magma direct adapter reuses taunt exhausted and same-turn Rush target legality", () =>
        {
            var valid = evaluator.EvaluateMagmaSplit(Frame(), 30, 40, cards);
            Check(valid.AppliedActions == 1 && valid.Verification == LineVerification.Conditional && valid.AttackIsLegal != false && valid.Split?.Branches.Count > 1 &&
                !valid.CanEvaluateFollowupActions && !valid.HasAuthoritativeNextState, "Direct preview did not use a validated combat boundary.");
            var taunt = Entity(41, "TAUNT", 2, "MINION", 0, 3, ("TAUNT", 1));
            var taunted = evaluator.EvaluateMagmaSplit(Replace(Frame(), taunt), 30, 40, cards);
            var tired = evaluator.EvaluateMagmaSplit(Replace(Frame(), Entity(30, "FIR_953", 1, "MINION", 2, 8, ("EXHAUSTED", 1))), 30, 40, cards);
            foreach (var bad in new[] { taunted, tired })
                Check(bad.Verification == LineVerification.Invalid && bad.AppliedActions == 0 && bad.Split is null,
                    "Direct helper bypassed ordinary combat restrictions.");
            Check(evaluator.EvaluateMagmaSplit(Frame(), 30, 11, cards).Split is null, "A hero target became an after-minion-attack trigger.");
        });
        yield return ("Magma native suppression or dying in combat does not invent a split wait", () =>
        {
            foreach (var source in new[] { Entity(30, "FIR_953", 1, "MINION", 2, 8, ("SILENCED", 1)),
                Entity(30, "FIR_953", 1, "MINION", 2, 1) })
            {
                var frame = Replace(Frame(), source, Entity(40, "VANILLA", 2, "MINION", 1, 6));
                var line = evaluator.EvaluateLine(frame, [new(ActionKind.Attack, 30, 40), new(ActionKind.EndTurn, 2)], cards);
                Check(line.AppliedActions == 2 && line.Result.TurnEnded && !line.Result.MagmaSplitBoundaryReached &&
                    line.Result.MagmaSplitPreviews.Count == 0, "A suppressed or dead Hound triggered native random damage.");
            }
        });
        yield return ("Magma unmodeled combat death prevents projecting an imaginary sole-hero split", () =>
        {
            var frame = Replace(Frame(), Entity(40, "DEATH", 2, "MINION", 0, 1, ("DEATHRATTLE", 1)));
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.Attack, 30, 40), new(ActionKind.EndTurn, 2)], cards);
            Check(line.AppliedActions == 1 && line.Result.MagmaSplitBoundaryReached && !line.Result.TurnEnded &&
                line.Result.MagmaSplitPreviews.Single().PriorResolutionPending && Find(line, 11).RemainingHealth == 20 &&
                !line.IsLethal, "An unknown preceding deathrattle was ignored before applying sole-hero damage.");
        });
        yield return ("Magma direct preview does not export private entities or bypass spectator and unknown perspective guards", () =>
        {
            var hidden = Entity(99, "PRIVATE_MAGMA_HAND", 2, "MINION", 1, 1); var tags = hidden.Tags.ToDictionary(); tags["ZONE"] = "HAND";
            var frame = Replace(Frame(), hidden with { Tags = tags.ToFrozenDictionary() });
            var direct = evaluator.EvaluateMagmaSplit(frame, 30, 40, cards);
            Check(direct.Split?.Branches.Count > 1 && !JsonSerializer.Serialize(direct).Contains("PRIVATE_MAGMA_HAND", StringComparison.Ordinal),
                "Direct preview exported a hidden opponent entity.");
            foreach (var privateFrame in new[] { frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
                frame with { Quality = new(true, true, false, true, 0, []) } })
                Check(evaluator.EvaluateMagmaSplit(privateFrame, 30, 40, cards).Split is null, "Unconfirmed perspective exposed split evidence.");
        });
        yield return ("Magma single-hero fast path requires the audited full native definition", () =>
        {
            var frame = Replace(Frame(), Entity(40, "VANILLA", 2, "MINION", 0, 1));
            var altered = cards.ToDictionary(); altered["FIR_953"] = cards["FIR_953"] with { Text = "changed native script" };
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.Attack, 30, 40)], altered);
            Check(line.Verification == LineVerification.Unsupported && !line.IsLethal && line.AppliedActions == 0 &&
                line.UnsupportedMechanics.Contains("UNAUDITED_NATIVE_DEFINITION") && !line.Result.MagmaSplitBoundaryReached &&
                Find(line, 11).RemainingHealth == 20 && Find(line, 40).RemainingHealth == 1,
                "A same-ID altered card inherited audited combat or deterministic split damage.");
        });
        yield return ("Magma unobserved summoned source stats cannot prove its later split from printed values", () =>
        {
            var hand = Entity(30, "FIR_953", 1, "MINION", 2, 8); var tags = hand.Tags.ToDictionary();
            tags["ZONE"] = "HAND"; tags["COST"] = "8"; tags.Remove("ATK"); tags.Remove("HEALTH"); tags.Remove("DAMAGE");
            var frame = Replace(Frame(), hand with { Tags = tags.ToFrozenDictionary() }, Entity(40, "VANILLA", 2, "MINION", 0, 1));
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.PlayCard, 30), new(ActionKind.Attack, 30, 40)], cards);
            Check(line.Verification != LineVerification.Verified && !line.IsLethal && line.Result.MagmaSplitBoundaryReached &&
                Find(line, 11).RemainingHealth == 20, "Unknown source stats were replaced by printed values to certify later split damage.");
        });
        yield return ("Magma split boundary uses armor remaining after the base combat rather than its old observation", () =>
        {
            var frame = Replace(Frame(), Entity(40, "VANILLA", 2, "MINION", 0, 6, ("ARMOR", 3)));
            var line = evaluator.EvaluateLine(frame, [new(ActionKind.Attack, 30, 40)], cards);
            var target = line.Result.MagmaSplitPreviews.Single().Targets.Single(value => value.EntityId == 40);
            Check(Find(line, 40).Armor == 1 && target.RemainingHealth.Minimum == 5 && target.RemainingHealth.Maximum == 6 &&
                target.RemainingArmor.Minimum == 0 && target.RemainingArmor.Maximum == 1,
                "The split helper reused pre-combat armor and missed a later health-damage branch.");
        });
    }

    private static SimulatedEntity Find(LineEvaluation line, int id) => line.Result.Entities.Single(entity => entity.EntityId == id);
    private static GameSnapshot Frame()
    {
        var entities = new[] { Player(2, 1, 10), Player(3, 2, 11), Entity(10, "HERO_1", 1, "HERO", 0, 30),
            Entity(11, "HERO_2", 2, "HERO", 0, 20), Entity(30, "FIR_953", 1, "MINION", 2, 8), Entity(40, "VANILLA", 2, "MINION", 0, 6) };
        return new("magma-line", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Player(int id, int player, int hero) => new(id, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString(), ["HERO_ENTITY"] = hero.ToString(), ["RESOURCES"] = "10", ["RESOURCES_USED"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Entity(int id, string card, int owner, string type, int attack, int health, params (string Key, int Value)[] flags)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = type, ["ZONE"] = "PLAY",
            ["ATK"] = attack.ToString(), ["HEALTH"] = health.ToString(), ["DAMAGE"] = "0", ["ARMOR"] = "0", ["EXHAUSTED"] = "0", ["NUM_TURNS_IN_PLAY"] = "1" };
        foreach (var (key, value) in flags) tags[key] = value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, params EntitySnapshot[] replacements)
    { var entities = frame.Entities.ToDictionary(); foreach (var entity in replacements) entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
