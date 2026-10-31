using HsHelper.Core;

public static class AssembledTagRegression
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("assembled half numeric tags normalize without interpreting unrelated links", Normalize);
        yield return ("only current own offered candidates expose observed assembled half markers", CurrentOffer);
        yield return ("choice history freezes assembled half markers before later changes", FrozenHistory);
        yield return ("assembled candidate markers remain private under opposing and spectator choices", Privacy);
    }

    private static void Normalize()
    {
        var engine = Start();
        Feed(engine, ["FULL_ENTITY - Creating ID=70 CardID=ASSEMBLED", "tag=CONTROLLER value=1", "tag=ZONE value=HAND",
            "tag=4572 value=114254", "tag=4573 value=115203",
            "FULL_ENTITY - Creating ID=71 CardID=UNRELATED_LINKS", "tag=CONTROLLER value=1", "tag=ZONE value=HAND",
            "tag=4694 value=114254", "tag=4957 value=115203"]);
        var frame = engine.Snapshot();
        Check(frame.Entities[70].GetIntTag(First) == 114254 && frame.Entities[70].GetIntTag(Second) == 115203 &&
            !frame.Entities[70].Tags.ContainsKey("4572") && !frame.Entities[70].Tags.ContainsKey("4573"),
            "Numeric half fields were not normalized.");
        Check(!frame.Entities[71].Tags.ContainsKey(First) && !frame.Entities[71].Tags.ContainsKey(Second) &&
            frame.Entities[71].GetIntTag("4694") == 114254 && frame.Entities[71].GetIntTag("4957") == 115203,
            "Unreviewed numeric fields became assembled halves or a target bridge.");
        Feed(engine, ["TAG_CHANGE Entity=70 tag=4572 value=0", "TAG_CHANGE Entity=70 tag=4573 value=invalid"]);
        var changed = engine.Snapshot().Entities[70];
        Check(changed.GetIntTag(First) == 0 && changed.Tags[Second] == "invalid", "Explicit zero or malformed half evidence was silently discarded.");
    }

    private static void CurrentOffer()
    {
        var engine = Start();
        Check(!engine.Snapshot().Entities[80].Tags.ContainsKey(First), "Private SETASIDE half data appeared before its offer.");
        Feed(engine, Offer(7));
        var frame = engine.Snapshot();
        Check(frame.Choice is { IsReady: true, Options.Count: 4 } && frame.Entities[80].GetIntTag(First) == 1 &&
            frame.Entities[80].GetIntTag(Second) == 0 && frame.Entities[82].GetIntTag(First) == 0 &&
            frame.Entities[82].GetIntTag(Second) == 1 && !frame.Entities[80].Tags.ContainsKey("4694") &&
            !frame.Entities[80].Tags.ContainsKey("4957"), "Current safe candidate projection lost half markers or exposed unrelated links.");
        Check(frame.Entities[84].CardId is null && !frame.Entities[84].Tags.ContainsKey(First) && !frame.Entities[84].Tags.ContainsKey(Second),
            "An unrelated SETASIDE card exposed its half data.");
    }

    private static void FrozenHistory()
    {
        var engine = Start(); Feed(engine, Offer(7));
        Feed(engine, ["GameState.DebugPrintEntitiesChosen() - id=7 Player=1 EntitiesCount=1",
            "GameState.DebugPrintEntitiesChosen() - Entities[0]=80", "ChoiceCardMgr.OnChoicesShown() - shown"]);
        var before = engine.Snapshot().ChoiceHistory!.Entries.Single();
        Feed(engine, ["TAG_CHANGE Entity=80 tag=4572 value=114254", "TAG_CHANGE Entity=80 tag=4573 value=115203"]);
        var after = engine.Snapshot().ChoiceHistory!.Entries.Single();
        Check(before.OptionEntities.Single(entity => entity.Id == 80).GetIntTag(First) == 1 &&
            after.OptionEntities.Single(entity => entity.Id == 80).GetIntTag(First) == 1 &&
            after.OptionEntities.Single(entity => entity.Id == 80).GetIntTag(Second) == 0,
            "Later half changes rewrote offered half history.");
    }

    private static void Privacy()
    {
        var opposing = Start(); Feed(opposing, Offer(7, 2));
        Check(opposing.Snapshot().Choice is null && !opposing.Snapshot().Entities[80].Tags.ContainsKey(First),
            "An opposing choice exposed private half data.");
        var watching = Start(); Feed(watching, Offer(7)); watching.ProcessLine("Begin Spectating");
        Check(watching.Snapshot().Choice is null && !watching.Snapshot().Entities[80].Tags.ContainsKey(First),
            "Spectating exposed private half data.");
    }

    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "assembled-tags");
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0",
            "FULL_ENTITY - Creating ID=30 CardID=BE_044", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY"]);
        for (var id = 80; id <= 84; id++)
            Feed(engine, [$"FULL_ENTITY - Creating ID={id} CardID=HALF_{id}", "tag=CONTROLLER value=1", "tag=ZONE value=SETASIDE",
                "tag=CARDTYPE value=MINION", $"tag=4572 value={(id < 82 ? 1 : 0)}", $"tag=4573 value={(id < 82 ? 0 : 1)}",
                "tag=4694 value=70", "tag=4957 value=71"]);
        engine.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
        return engine;
    }
    private static IEnumerable<string> Offer(int choice, int player = 1)
    {
        yield return $"GameState.DebugPrintEntityChoices() - id={choice} Player={player} TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1";
        yield return "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 player=1]";
        for (var index = 0; index < 4; index++)
            yield return $"GameState.DebugPrintEntityChoices() - Entities[{index}]=[entityName=Anonymous id={80 + index} zone=SETASIDE cardId=HALF_{80 + index} player={player}]";
        yield return "ChoiceCardMgr.OnChoicesShown() - shown";
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
