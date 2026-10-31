using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterDrawLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        LineEvaluation Evaluate(GameSnapshot frame, params PlannedAction[] actions) => new LineEvaluator().EvaluateLine(frame,
            actions.Length == 0 ? [new(ActionKind.PlayCard, 30)] : actions, cards);
        yield return ("flower attendant uses the cumulative counter instead of hero-power strength", () =>
        {
            foreach (var count in new[] { 0, 1, 2, 4 })
            {
                var result = Evaluate(Frame(count)); var draw = result.Result.DrawPreviews.Single();
                Check(draw.ResolvesEffects && draw.RequestedDraws.IsExact && draw.RequestedDraws.Minimum == (count >= 2 ? 2 : 0) &&
                    result.Result.DrawBoundaryReached == (count >= 2) && result.Result.ImbueCountBefore == count && result.Result.ImbueCountAfter == count,
                    "Native draw did not use the explicit cumulative threshold.");
            }
        });
        yield return ("a covered native imbue before Flower Attendant advances its draw condition", () =>
        {
            var frame = Add(Frame(1), Card(31, "EDR_852", "HAND", 1)); frame = SetPlayer(frame, ("NUM_CARDS_IN_HAND", 2));
            var result = Evaluate(frame, new(ActionKind.PlayCard, 31), new(ActionKind.PlayCard, 30));
            Check(result.AppliedActions == 2 && result.Result.ImbueCountBefore == 1 && result.Result.ImbueCountAfter == 2 &&
                result.Result.HeroPowerChanges.Count == 1 && result.Result.DrawPreviews.Single().RequestedDraws.Minimum == 2,
                "The second card reused the old snapshot counter or advanced it twice.");
        });
        yield return ("a draw boundary refuses later actions without fabricating a hand entity", () =>
        {
            var result = Evaluate(Frame(2), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 1 &&
                result.UnsupportedMechanics.Contains("AWAIT_DRAW_SNAPSHOT") && !result.Result.TurnEnded &&
                result.Result.Entities.Count(entity => entity.ControllerId == 1 && entity.Zone == "HAND") == 0 &&
                result.Result.DrawPreviews.Single().HandCountAfter is { Minimum: 2, Maximum: 2 },
                "Direct count previews became generated hand entities or a usable next turn state.");
        });
        yield return ("a confirmed unmet draw condition allows the following covered end turn", () =>
        {
            var result = Evaluate(Frame(0), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 2 && result.Result.TurnEnded &&
                !result.Result.DrawBoundaryReached && result.Result.DrawPreviews.Single().RequestedDraws.Maximum == 0,
                "A known absent draw unnecessarily crossed an unresolved draw boundary.");
        });
        yield return ("a strong Wolf blessing never fills a missing cumulative counter in a line", () =>
        {
            var frame = Add(Frame(null), Card(12, "EDR_850p", "PLAY", 2, type: "HERO_POWER", ("TAG_SCRIPT_DATA_NUM_1", 15), ("TAG_SCRIPT_DATA_NUM_2", 15)));
            var result = Evaluate(frame);
            Check(result.Result.ImbueCountBefore is null && result.Result.DrawPreviews.Single().RequestedDraws is { Minimum: 0, Maximum: 2 },
                "Current hero-power strength was turned into a historical cumulative condition.");
        });
        yield return ("line draw overlays preserve conflicting count and fatigue aliases as unknown", () =>
        {
            var frame = SetPlayer(Frame(2, deck: 0), ("FATIGUE", 3), ("22", 2), ("MAXHANDSIZE", 10), ("28", 8));
            var draw = Evaluate(frame).Result.DrawPreviews.Single();
            Check(!draw.FatigueDamage.IsExact && (draw.CardsAddedToHand.Maximum == 0), "A conflicting fatigue alias became an exact draw overlay.");
            frame = SetPlayer(Frame(2), ("NUM_CARDS_IN_HAND", 99));
            draw = Evaluate(frame).Result.DrawPreviews.Single();
            Check(!draw.CardsAddedToHand.IsExact && draw.HandCountAfter is null,
                "Overlay entity counts silently overrode a contradictory observed hand count.");
        });
        yield return ("numeric immunity and conflicting immunity remain distinct in line draw previews", () =>
        {
            var frame = Add(Frame(2, deck: 0), Hero(10, 1, ("240", 1)));
            var draw = Evaluate(frame).Result.DrawPreviews.Single();
            Check(draw.FatigueDamage is { Minimum: 0, Maximum: 0 } && draw.FatigueAfter is { Minimum: 2, Maximum: 2 },
                "Numeric immunity was ignored or fatigue stopped advancing.");
            frame = Add(frame, Hero(10, 1, ("IMMUNE", 0), ("240", 1)));
            draw = Evaluate(frame).Result.DrawPreviews.Single();
            Check(!draw.FatigueDamage.IsExact && draw.FatigueDamage.Minimum == 0, "Conflicting immunity became hard false or true.");
        });
        yield return ("Houndmaster leaves its after-draw imbue pending even with an exact Beast draw count", () =>
        {
            var frame = Add(Frame(2), Card(30, "EDR_226", "HAND", 2));
            frame = frame with { DeckTracking = new(frame.MatchId, frame.StateVersion, "deck", "255790", 2,
                new Dictionary<string, int> { ["EDR_227"] = 2 }, 0, true, []) };
            var result = Evaluate(frame);
            Check(result.Result.DrawPreviews.Single().CardsRemovedFromDeck is { Minimum: 1, Maximum: 1 } &&
                result.Result.ImbueCountBefore == 2 && result.Result.ImbueCountAfter is null && result.Result.HeroPowerChanges.Count == 0 &&
                result.UnsupportedMechanics.Contains("PENDING_AFTER_DRAW_IMBUE"), "After-draw imbue was executed ahead of the unresolved draw.");
        });
    }

    private static GameSnapshot Frame(int? count, int deck = 2)
    {
        var tags = new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["RESOURCES"] = "10", ["RESOURCES_USED"] = "0", ["HERO_ENTITY"] = "10",
            ["HERO_POWER_ENTITY"] = "12", ["MAXHANDSIZE"] = "10", ["NUM_CARDS_IN_HAND"] = "1", ["NUM_CARDS_IN_DECK"] = deck.ToString(), ["FATIGUE"] = "0" };
        if (count.HasValue) tags["3527"] = count.Value.ToString();
        var entities = new List<EntitySnapshot> { new(2, null, EntityKind.Player, tags.ToFrozenDictionary()),
            new(3, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "11" }.ToFrozenDictionary()),
            Hero(10, 1), Hero(11, 2), Card(12, "EDR_850p", "PLAY", 2, "HERO_POWER", ("TAG_SCRIPT_DATA_NUM_1", 1), ("TAG_SCRIPT_DATA_NUM_2", 1)),
            Card(30, "FIR_921", "HAND", 3) };
        for (var i = 0; i < deck; i++) entities.Add(Card(40 + i, null, "DECK", 0));
        return new("native-draw-line", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        { ClientBuild = "255790" };
    }
    private static EntitySnapshot Hero(int id, int owner, params (string Key, int Value)[] more)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO", ["CLASS"] = owner == 1 ? "HUNTER" : "MAGE", ["HEALTH"] = "30", ["DAMAGE"] = "0", ["ARMOR"] = "0" };
        foreach (var (key, value) in more) tags[key] = value.ToString(); return new(id, "HERO_" + owner, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Card(int id, string? card, string zone, int cost, string type = "MINION", params (string Key, int Value)[] more)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = zone, ["CARDTYPE"] = type, ["COST"] = cost.ToString() };
        foreach (var (key, value) in more) tags[key] = value.ToString(); return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot SetPlayer(GameSnapshot frame, params (string Key, int Value)[] more)
    {
        var tags = frame.Entities[2].Tags.ToDictionary(); foreach (var (key, value) in more) tags[key] = value.ToString();
        return Add(frame, frame.Entities[2] with { Tags = tags.ToFrozenDictionary() });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
