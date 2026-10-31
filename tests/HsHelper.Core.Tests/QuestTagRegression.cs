using HsHelper.Core;

internal static class QuestTagRegression
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("numeric sidequest counters normalize into observed semantic tags", () =>
        {
            var engine = Start(); var quest = engine.Snapshot().Entities[30];
            Check(quest.CardId == "TLC_EVENT_400" && quest.Zone == "SECRET" && quest.GetIntTag("QUEST_PROGRESS") == 1 &&
                quest.GetIntTag("QUEST_PROGRESS_TOTAL") == 3 && quest.GetIntTag("SIDE_QUEST") == 1 &&
                !quest.Tags.ContainsKey("534") && !quest.Tags.ContainsKey("535") && !quest.Tags.ContainsKey("1192"),
                "Numeric task evidence did not normalize or was replaced by defaults.");
        });
        yield return ("quest counter updates preserve prior immutable frames and explicit zero", () =>
        {
            var engine = Start(); var before = engine.Snapshot();
            Feed(engine, ["TAG_CHANGE Entity=30 tag=534 value=2", "PowerProcessor.EndCurrentTaskList() - complete"]);
            var after = engine.Snapshot();
            Check(after.StateVersion > before.StateVersion && after.Entities[30].GetIntTag("QUEST_PROGRESS") == 2 &&
                before.Entities[30].GetIntTag("QUEST_PROGRESS") == 1, "Task progress mutated its earlier snapshot.");
            Feed(engine, ["TAG_CHANGE Entity=30 tag=QUEST_PROGRESS value=0", "TAG_CHANGE Entity=30 tag=1192 value=0"]);
            var zero = engine.Snapshot().Entities[30];
            Check(zero.GetIntTag("QUEST_PROGRESS") == 0 && zero.GetIntTag("SIDE_QUEST") == 0, "Explicit zero task tags were lost.");
        });
        yield return ("malformed and unrelated task tags remain evidence rather than completion", () =>
        {
            var engine = Start();
            Feed(engine, ["TAG_CHANGE Entity=30 tag=534 value=bad", "TAG_CHANGE Entity=30 tag=535 value=4", "TAG_CHANGE Entity=30 tag=536 value=3"]);
            var quest = engine.Snapshot().Entities[30];
            Check(quest.Tags["QUEST_PROGRESS"] == "bad" && quest.GetIntTag("QUEST_PROGRESS") is null &&
                quest.GetIntTag("QUEST_PROGRESS_TOTAL") == 4 && quest.GetIntTag("536") == 3,
                "Invalid task counters were repaired or an unrelated numeric tag became progress.");
        });
    }
    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "quest-tags");
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING",
            "tag=TURN value=1", "tag=STEP value=MAIN_ACTION", "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1",
            "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0", "FULL_ENTITY - Creating ID=30 CardID=TLC_EVENT_400",
            "tag=CONTROLLER value=1", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL", "tag=534 value=1", "tag=535 value=3", "tag=1192 value=1",
            "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
