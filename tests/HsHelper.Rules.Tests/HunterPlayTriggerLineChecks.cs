using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterPlayTriggerLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> baseCards)
    {
        var cards = LoadCards(baseCards);
        LineEvaluation Eval(GameSnapshot frame, params PlannedAction[] actions) => new LineEvaluator().EvaluateLine(frame,
            actions.Length == 0 ? [new(ActionKind.PlayCard, 30)] : actions, cards);
        yield return ("active quest advances once for a qualifying play and permits a known non-completing continuation", () =>
        {
            var frame = Frame(0); var before = JsonSerializer.Serialize(frame);
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 2 && result.Result.TurnEnded &&
                result.Result.QuestProgressPreviews.Single() is { CurrentProgress: 0, Increment: 1, ResultingProgress: 1, CanProjectProgress: true } &&
                Find(result, 60).Flags["QUEST_PROGRESS"] == 1 && !result.Result.QuestRewardBoundaryReached && before == JsonSerializer.Serialize(frame),
                "A non-completing native play was not counted exactly once or mutated the observation.");
        });
        yield return ("two covered Beast plays use the current simulated quest progress rather than the original counter", () =>
        {
            var frame = Add(Frame(0), Minion(31, "EDR_480", "HAND", 1, 9, 9));
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.PlayCard, 31), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 3 &&
                result.Result.QuestProgressPreviews.Select(outcome => outcome.ResultingProgress).SequenceEqual(new int?[] { 1, 2 }) &&
                Find(result, 60).Flags["QUEST_PROGRESS"] == 2, "The second play reused the original counter or counted a play twice.");
        });
        yield return ("quest completion stops at the real reward-choice boundary without creating a Zombeast", () =>
        {
            var result = Eval(Frame(2), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 1 && !result.Result.TurnEnded &&
                result.Result.QuestRewardBoundaryReached && result.Result.QuestProgressPreviews.Single() is
                    { ResultingProgress: 3, RequiresRewardChoice: true, CanProjectProgress: true } &&
                Find(result, 60).Flags["QUEST_PROGRESS"] == 3 && result.Result.Entities.All(entity => entity.Zone != "HAND"),
                "A completed task bypassed its choice or acquired a fabricated reward card.");
        });
        yield return ("an already-completed quest refuses actions before mana payment", () =>
        {
            var result = Eval(Frame(3));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 0 && result.Result.AvailableMana == 10 &&
                Find(result, 30).Zone == "HAND" && result.Result.QuestRewardBoundaryReached,
                "A pending observed quest reward allowed another action before its choice.");
        });
        yield return ("a native spell with no tribe leaves task progress unchanged", () =>
        {
            var frame = Add(Frame(1), Card(30, "GAME_005", "HAND", "SPELL", ("COST", "0")));
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.Result.QuestProgressPreviews.Single() is
                { PlayedCardQualifies: false, Increment: 0, ResultingProgress: 1 } && !result.Result.QuestRewardBoundaryReached,
                "A non-tribal spell became a Beast play or an unresolved random trigger.");
        });
        yield return ("missing quest counters never become a fresh zero or a projected completion", () =>
        {
            var frame = Frame(0); var tags = frame.Entities[60].Tags.Where(pair => pair.Key != "QUEST_PROGRESS").ToFrozenDictionary();
            var result = Eval(Add(frame, frame.Entities[60] with { Tags = tags }));
            Check(result.Verification == LineVerification.Conditional && result.Result.QuestRewardBoundaryReached &&
                result.Result.QuestProgressPreviews.Single() is { CurrentProgress: null, ResultingProgress: null, CanProjectProgress: false } &&
                !Find(result, 60).Flags.ContainsKey("QUEST_PROGRESS"), "An absent counter was reset or incremented from a guessed zero.");
        });
        yield return ("task installation spends current cost resets stale hand counters and supports later plays in the same line", () =>
        {
            var frame = Frame(0); frame = Add(frame, Card(60, "TLC_EVENT_400", "HAND", "SPELL", ("COST", "1"),
                ("QUEST_PROGRESS", "bad"), ("QUEST_PROGRESS_TOTAL", "9"), ("534", "2")));
            var result = Eval(frame, new(ActionKind.PlayCard, 60), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Conditional || result.Verification == LineVerification.Verified,
                "A successful new task installation was refused.");
            Check(result.AppliedActions == 3 && result.Result.AvailableMana == 8 && Find(result, 60).Zone == "SECRET" &&
                Find(result, 60).Flags["QUEST_PROGRESS"] == 1 && Find(result, 60).Flags["QUEST_PROGRESS_TOTAL"] == 3 &&
                result.Result.QuestProgressPreviews.Single().CurrentProgress == 0 && !result.Result.QuestRewardBoundaryReached,
                "Installed task progress reused stale hand tags or failed to bind the following Beast play.");
        });
        yield return ("task installation enforces duplicate capacity target and mana restrictions before payment", () =>
        {
            var fresh = Add(NoQuest(), Card(60, "TLC_EVENT_400", "HAND", "SPELL", ("COST", "1")));
            var duplicate = Add(fresh, Card(61, "TLC_EVENT_400", "SECRET", "SPELL", ("QUEST_PROGRESS", "0"), ("QUEST_PROGRESS_TOTAL", "3")));
            var full = fresh;
            for (var i = 0; i < 5; i++) full = Add(full, Card(70 + i, "UNKNOWN_SLOT_" + i, "SECRET", "SPELL"));
            var noMana = HunterGenerationLineChecks.Player(fresh, ("RESOURCES", "0"));
            foreach (var result in new[] { Eval(duplicate, new PlannedAction(ActionKind.PlayCard, 60)),
                Eval(full, new PlannedAction(ActionKind.PlayCard, 60)), Eval(noMana, new PlannedAction(ActionKind.PlayCard, 60)),
                Eval(fresh, new PlannedAction(ActionKind.PlayCard, 60, 11)) })
                Check(result.Verification == LineVerification.Invalid && result.AppliedActions == 0 && Find(result, 60).Zone == "HAND",
                    "An illegal task installation paid or changed zones.");
        });
        yield return ("shared secret-zone slots include sidequests when installing an Explosive Trap", () =>
        {
            var frame = Add(Frame(0), Card(30, "CORE_EX1_610", "HAND", "SPELL", ("COST", "2")));
            for (var i = 0; i < 4; i++) frame = Add(frame, Card(70 + i, "UNKNOWN_SLOT_" + i, "SECRET", "SPELL"));
            var result = Eval(frame);
            Check(result.Verification == LineVerification.Invalid && result.AppliedActions == 0 && result.Result.AvailableMana == 10,
                "The sidequest was omitted from the shared secret-zone capacity.");
        });
        yield return ("new native Dreambound Raptor enters and retains its unproven self-registration boundary", () =>
        {
            var frame = Add(NoQuest(), Minion(30, "EDR_849", "HAND", 1, 2, 1));
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.AppliedActions == 1 && result.Verification == LineVerification.Unsupported && Find(result, 30).Zone == "PLAY" &&
                result.Result.AvailableMana == 9 && result.Result.RaptorBoundaryReached &&
                result.Result.RaptorPreviews.Single().TriggerCount is { Minimum: 0, Maximum: 1 },
                "Raptor entry stayed unsupported or silently assumed its self-registration result.");
        });
        yield return ("active Raptor binds one native grant to the played minion and blocks later actions", () =>
        {
            var result = Eval(WithRaptor(NoQuest()), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            var preview = result.Result.RaptorPreviews.Single();
            Check(result.AppliedActions == 1 && result.Verification == LineVerification.Unsupported && result.Result.RaptorBoundaryReached &&
                preview.TriggerCount is { Minimum: 1, Maximum: 1 } && preview.Triggers.Single().SourceEntityId == 50 &&
                preview.TargetEntityId == 30 && preview.Triggers.Single().GuaranteedBonusEffect is null &&
                !Find(result, 30).Flags.ContainsKey("DIVINE_SHIELD") && !result.Result.TurnEnded,
                "Raptor count did not bind both entities or invented a random keyword.");
        });
        yield return ("multiple Raptor sources are counted separately without assigning an order", () =>
        {
            var frame = Add(WithRaptor(NoQuest()), Minion(51, "EDR_849", "PLAY", 1, 2, 1));
            var result = Eval(frame); var preview = result.Result.RaptorPreviews.Single();
            Check(preview.TriggerCount is { Minimum: 2, Maximum: 2 } && preview.Triggers.Count == 2 &&
                preview.Triggers.All(trigger => trigger.GuaranteedBonusEffect is null) && result.Result.RaptorBoundaryReached,
                "Multiple native sources lost a request or chose a grant order.");
        });
        yield return ("known suppressed Raptor sources add no random boundary", () =>
        {
            foreach (var flag in new[] { "SILENCED", "DORMANT" })
            {
                var frame = WithRaptor(NoQuest()); var tags = frame.Entities[50].Tags.ToDictionary(); tags[flag] = "1";
                var result = Eval(Add(frame, frame.Entities[50] with { Tags = tags.ToFrozenDictionary() }), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
                Check(result.Verification == LineVerification.Verified && result.AppliedActions == 2 && !result.Result.RaptorBoundaryReached &&
                    result.Result.RaptorPreviews.Single().TriggerCount is { Minimum: 0, Maximum: 0 }, "A suppressed Raptor still granted a random effect.");
            }
        });
        yield return ("missing observed Raptor damage is not promoted to a healthy zero-damage source", () =>
        {
            var frame = WithRaptor(NoQuest()); var tags = frame.Entities[50].Tags.Where(pair => pair.Key != "DAMAGE").ToFrozenDictionary();
            var result = Eval(Add(frame, frame.Entities[50] with { Tags = tags }));
            Check(result.Result.RaptorPreviews.Single().TriggerCount is { Minimum: 0, Maximum: 1 },
                "An originally missing damage value became a confirmed healthy source through the runtime overlay.");
        });
        yield return ("valid numeric zone aliases follow a covered minion into play while identity conflicts fail before payment", () =>
        {
            var frame = WithRaptor(NoQuest()); var tags = frame.Entities[30].Tags.ToDictionary(); tags["49"] = "3"; tags["50"] = "1"; tags["202"] = "4";
            var good = Eval(Add(frame, frame.Entities[30] with { Tags = tags.ToFrozenDictionary() }));
            Check(good.Result.RaptorPreviews.Single().TriggerCount is { Minimum: 1, Maximum: 1 }, "An old valid HAND alias contradicted the new PLAY zone.");
            foreach (var (key, value) in new[] { ("49", "1"), ("50", "2"), ("CARDTYPE", ""), ("202", "SPELL") })
            {
                var badTags = frame.Entities[30].Tags.ToDictionary(); badTags[key] = value;
                var bad = Eval(Add(frame, frame.Entities[30] with { Tags = badTags.ToFrozenDictionary() }));
                Check(bad.Verification == LineVerification.Unsupported && bad.AppliedActions == 0 && bad.Result.AvailableMana == 10 &&
                    Find(bad, 30).Zone == "HAND", "A contradictory original identity was repaired only after payment.");
            }
        });
        yield return ("pending draw generation or Raptor effects do not project later quest progress", () =>
        {
            var frames = new[] { Add(Frame(2), Minion(30, "CORE_UNG_912", "HAND", 1, 1, 1)),
                Add(Frame(2), Minion(30, "EDR_226", "HAND", 2, 2, 2)), WithRaptor(Frame(2)) };
            foreach (var frame in frames)
            {
                var result = Eval(frame); var progress = result.Result.QuestProgressPreviews.Single();
                Check(progress.IsConditionalPreview && !progress.CanProjectProgress && progress.ResultingProgress == 3 &&
                    Find(result, 60).Flags["QUEST_PROGRESS"] == 2 && result.Result.QuestRewardBoundaryReached,
                    "An after-play task reward was applied before unresolved native effects.");
            }
        });
        yield return ("unexpanded battlecry deathrattles keep after-play quest completion conditional", () =>
        {
            var frame = Add(Frame(2), Minion(30, "CORE_CS2_189", "HAND", 1, 1, 1));
            frame = Add(frame, Minion(51, "DEATH", "PLAY", 1, 0, 1));
            var result = Eval(frame, new PlannedAction(ActionKind.PlayCard, 30, 51));
            Check(result.UnsupportedMechanics.Contains("DEATHRATTLE_OR_REBORN") && result.Result.QuestProgressPreviews.Single() is
                { IsConditionalPreview: true, CanProjectProgress: false, ResultingProgress: 3 } && Find(result, 60).Flags["QUEST_PROGRESS"] == 2,
                "The task reward jumped ahead of an unmodeled deathrattle.");
        });
        yield return ("lethal health payment stops a sidequest installation before its new counter exists", () =>
        {
            var frame = Add(NoQuest(), Minion(30, "EDR_489", "HAND", 8, 1, 1));
            frame = Add(frame, Card(60, "TLC_EVENT_400", "HAND", "SPELL", ("COST", "1"), ("QUEST_PROGRESS", "2")));
            var hero = frame.Entities[11].Tags.ToDictionary(); hero["HEALTH"] = "1";
            frame = Add(frame, frame.Entities[11] with { Tags = hero.ToFrozenDictionary() });
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.PlayCard, 60));
            Check(result.Result.OpponentDefeatedByPayment && Find(result, 60).Zone == "HAND" && Find(result, 60).Flags["QUEST_PROGRESS"] == 2 &&
                result.Result.QuestProgressPreviews.Count == 0, "A task was installed or reset after game-ending payment.");
        });
    }

    private static GameSnapshot Frame(int progress)
    {
        var frame = Add(HunterGenerationLineChecks.Frame(), Minion(30, "EDR_227", "HAND", 1, 5, 2));
        return Add(frame, Card(60, "TLC_EVENT_400", "SECRET", "SPELL", ("SIDE_QUEST", "1"), ("QUEST_PROGRESS", progress.ToString()), ("QUEST_PROGRESS_TOTAL", "3")));
    }
    private static GameSnapshot NoQuest() => Frame(0) with { Entities = Frame(0).Entities.Where(pair => pair.Key != 60).ToFrozenDictionary() };
    private static GameSnapshot WithRaptor(GameSnapshot frame) => Add(frame, Minion(50, "EDR_849", "PLAY", 1, 2, 1));
    private static EntitySnapshot Minion(int id, string card, string zone, int cost, int attack, int health) => Card(id, card, zone, "MINION",
        ("COST", cost.ToString()), ("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("CARDRACE", "BEAST"));
    private static EntitySnapshot Card(int id, string card, string zone, string type, params (string Key, string Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = zone, ["CARDTYPE"] = type };
        foreach (var (key, value) in extra) tags[key] = value;
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity) => HunterGenerationLineChecks.Add(frame, entity);
    private static SimulatedEntity Find(LineEvaluation result, int id) => result.Result.Entities.Single(entity => entity.EntityId == id);
    private static IReadOnlyDictionary<string, CardRecord> LoadCards(IReadOnlyDictionary<string, CardRecord> original)
    {
        var cards = original.ToDictionary();
        foreach (var fixtureName in new[] { "quest-progress-255790.json", "play-trigger-line-255790.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName)));
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.TryGetProperty("text", out var text) ? text.GetString()! : "",
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        }
        return cards;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
