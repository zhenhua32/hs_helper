using HsHelper.Core;
using System.Collections.Frozen;

internal static class TurnHistoryRegression
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("turn windows preserve actual friendly extra turns rather than assuming alternating players", Windows);
        yield return ("turn play identities and traits freeze before later transforms or modifiers", Frozen);
        yield return ("turn history distinguishes actual hand PLAY blocks from trigger summons and replayed bodies", ActualPlay);
        yield return ("missing play blocks and unknown sources prevent a complete turn ledger", MissingPlays);
        yield return ("turn history requires opening and ending boundaries without rejecting normal cleanup", Boundaries);
        yield return ("turn gaps reversed counters and skipped windows cannot connect previous turns", Gaps);
        yield return ("turn history never fills an unknown played identity or its traits from future observations", UnknownIdentity);
        yield return ("turn history has no private opponent traits and cannot retroactively switch perspectives", Privacy);
        yield return ("turn history is bounded and cleared between games and sessions", Bounded);
        yield return ("turn snapshots and nested play lists remain immutable", Immutable);
    }
    private static PowerLogStateEngine Start()
    {
        var engine = new PowerLogStateEngine(1, "turn-evidence");
        Feed(engine, ["GameState.DebugPrintGame() - BuildNumber=255790", "CREATE_GAME", "GameEntity EntityID=1",
            "tag=STATE value=RUNNING", "tag=TURN value=0", "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1",
            "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0"]);
        return engine;
    }
    public static void Begin(PowerLogStateEngine engine, int number, int player)
    {
        Feed(engine, [$"TAG_CHANGE Entity=1 tag=TURN value={number}", $"TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value={(player == 1 ? 1 : 0)}",
            $"TAG_CHANGE Entity=3 tag=CURRENT_PLAYER value={(player == 2 ? 1 : 0)}", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY",
            "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete"]);
    }
    public static void Card(PowerLogStateEngine engine, int id, string card, int owner = 1, int race = 20)
    {
        Feed(engine, [$"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=HAND",
            "tag=202 value=4", $"tag=200 value={race}", "tag=1635 value=1", "tag=PLAYER_NAME value=PRIVATE_NAME"]);
    }
    public static void Play(PowerLogStateEngine engine, int id)
    {
        Feed(engine, [$"BLOCK_START BlockType=PLAY Entity={id}", $"TAG_CHANGE Entity={id} tag=ZONE value=PLAY", "BLOCK_END"]);
    }
    public static void End(PowerLogStateEngine engine) => Feed(engine, ["TAG_CHANGE Entity=1 tag=STEP value=MAIN_END",
        "TAG_CHANGE Entity=1 tag=STEP value=MAIN_CLEANUP", "PowerProcessor.EndCurrentTaskList() - complete"]);
    private static TurnHistorySnapshot History(PowerLogStateEngine engine) => engine.Snapshot().TurnHistory!;
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static void Windows()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, "EDR_227"); Play(engine, 40); End(engine);
        Begin(engine, 2, 1); End(engine); Begin(engine, 3, 2); End(engine); Begin(engine, 4, 1);
        var history = History(engine);
        Check(history.IsComplete && history.Turns.Select(turn => turn.PlayerId).SequenceEqual([1, 1, 2, 1]) &&
            history.Turns.Take(3).All(turn => turn.IsComplete && turn.EndedAtEventSequence.HasValue) &&
            !history.Turns[3].IsComplete && history.Turns[3].HasStartBoundary, "Extra turns or current/closed boundaries were lost.");
        Check(history.MatchId == engine.Snapshot().MatchId && history.StateVersion == engine.Snapshot().StateVersion &&
            history.EventSequence == engine.Snapshot().EventSequence, "Turn evidence lost its snapshot binding.");
    }
    private static void Frozen()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, "EDR_227"); Play(engine, 40);
        var old = History(engine); var source = old.Turns.Single().Plays.Single();
        Feed(engine, ["TAG_CHANGE Entity=40 tag=CARDRACE value=15", "TAG_CHANGE Entity=40 tag=CARDTYPE value=5",
            "CHANGE_ENTITY - Updating Entity=40 CardID=DIFFERENT"]);
        var now = History(engine).Turns.Single().Plays.Single();
        Check(source.AtPlay.CardId == "EDR_227" && source.AtPlay.Zone == "HAND" && now.AtPlay.CardId == "EDR_227" &&
            now.AtPlay.GetIntTag("CARDRACE") == 20 && now.AtPlay.GetIntTag("CARDTYPE") == 4 &&
            now.AtPlay.GetIntTag("SPELL_SCHOOL") == 1 && !now.AtPlay.Tags.ContainsKey("PLAYER_NAME"),
            "Later traits or private names filled the historical play.");
    }
    private static void ActualPlay()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, "EDR_227");
        Feed(engine, ["BLOCK_START BlockType=TRIGGER Entity=2", "TAG_CHANGE Entity=40 tag=ZONE value=PLAY", "BLOCK_END"]);
        Feed(engine, ["BLOCK_START BlockType=PLAY Entity=40", "BLOCK_END"]);
        End(engine);
        Check(History(engine).IsComplete && History(engine).Turns.Single().IsComplete && History(engine).Turns.Single().Plays.Count == 0,
            "A trigger summon or an already-played body became a new hand play.");
    }
    private static void MissingPlays()
    {
        var missing = Start(); Begin(missing, 1, 1); Card(missing, 40, "EDR_227");
        missing.ProcessLine("TAG_CHANGE Entity=40 tag=ZONE value=PLAY"); End(missing);
        Check(!History(missing).IsComplete && !History(missing).Turns.Single().IsComplete, "An unscoped hand transition claimed complete play evidence.");
        var unknown = Start(); Begin(unknown, 1, 1); unknown.ProcessLine("BLOCK_START BlockType=PLAY Entity=999"); unknown.ProcessLine("BLOCK_END");
        Check(!History(unknown).IsComplete, "An unresolved PLAY source left the source ledger complete.");
        var conflict = Start(); Begin(conflict, 1, 1); Card(conflict, 40, "EDR_227");
        conflict.ProcessLine("BLOCK_START BlockType=PLAY Entity=[id=40 cardId=DIFFERENT player=1]");
        Check(!History(conflict).IsComplete, "A conflicting source descriptor supplied a historical play.");
        var replay = Start(); Begin(replay, 1, 1); Card(replay, 40, "EDR_227"); Play(replay, 40);
        replay.ProcessLine("BLOCK_START BlockType=PLAY Entity=[id=40 zone=HAND cardId=EDR_227 player=1]");
        Check(!History(replay).IsComplete, "A newer hand-play descriptor reused an older source play after a missing return-to-hand.");
    }
    private static void Boundaries()
    {
        var engine = Start(); Begin(engine, 1, 1); End(engine);
        Check(History(engine).Turns.Single().IsComplete && History(engine).IsComplete, "Normal cleanup invalidated a completed turn.");
        var partial = Start(); partial.ProcessLine("TAG_CHANGE Entity=1 tag=TURN value=1");
        partial.ProcessLine("TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION"); End(partial);
        Check(!History(partial).IsComplete && !History(partial).Turns.Single().HasStartBoundary && !History(partial).Turns.Single().IsComplete,
            "A missing turn opening was manufactured.");
        var noEnd = Start(); Begin(noEnd, 1, 1); Begin(noEnd, 2, 2);
        Check(!History(noEnd).IsComplete && !History(noEnd).Turns[0].IsComplete, "Missing turn end established a complete window.");
    }
    private static void Gaps()
    {
        foreach (var next in new[] { 0, 1, 4 })
        {
            var engine = Start(); Begin(engine, 1, 1); End(engine); Begin(engine, 2, 2); End(engine); Begin(engine, next, 1);
            Check(!History(engine).IsComplete, "A reversed or missing turn number was linked.");
        }
        var skipped = Start(); Begin(skipped, 1, 1); End(skipped);
        skipped.ProcessLine("TAG_CHANGE Entity=1 tag=TURN value=2"); Begin(skipped, 3, 1);
        Check(!History(skipped).IsComplete, "A missing ownership window was skipped while linking older friendly turns.");
        var gap = Start(); Begin(gap, 1, 1); gap.MarkDataGap(); Check(!History(gap).IsComplete, "A known input gap was hidden.");
    }
    private static void UnknownIdentity()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, ""); Play(engine, 40);
        engine.ProcessLine("SHOW_ENTITY - Updating Entity=40 CardID=EDR_227");
        var atPlay = History(engine).Turns.Single().Plays.Single().AtPlay;
        Check(atPlay.CardId is null && !atPlay.Tags.ContainsKey("CARDRACE"), "A future reveal supplied an unknown source's race.");
    }
    private static void Privacy()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, "PRIVATE_OWN"); Play(engine, 40); End(engine);
        Begin(engine, 2, 2); Card(engine, 50, "PRIVATE_OPPONENT", 2); Play(engine, 50);
        var opposing = History(engine).Turns[1].Plays.Single().AtPlay;
        Check(opposing.CardId is null && !opposing.Tags.ContainsKey("CARDRACE"), "An opponent hand identity entered play-time evidence.");
        engine.SetLocalPlayerId(2);
        Check(History(engine).Turns[0].Plays.Single().AtPlay.CardId is null && History(engine).Turns[1].Plays.Single().AtPlay.CardId is null,
            "Switching perspective exposed old private traits or filled unobserved ones.");
        engine.ProcessLine("Begin Spectating");
        Check(History(engine).Turns.Count == 0 && !History(engine).IsComplete, "Spectating exposed private turn history.");
    }
    private static void Bounded()
    {
        var engine = Start();
        for (var i = 1; i <= 66; i++) { Begin(engine, i, i % 2 == 1 ? 1 : 2); End(engine); }
        Check(History(engine).Turns.Count == 64 && History(engine).TruncatedThroughSequence > 0 && !History(engine).IsComplete,
            "Turn evidence was unbounded or crossed a truncated beginning.");
        engine.ProcessLine("CREATE_GAME"); Check(History(engine).Turns.Count == 0, "New game inherited turn evidence.");
        engine.ResetSession(); Check(History(engine).Turns.Count == 0, "Session reset inherited turn evidence.");
    }
    private static void Immutable()
    {
        var engine = Start(); Begin(engine, 1, 1); Card(engine, 40, "EDR_227"); Play(engine, 40);
        var old = History(engine); Card(engine, 41, "EDR_227"); Play(engine, 41);
        Check(old.Turns.Single().Plays.Count == 1 && History(engine).Turns.Single().Plays.Count == 2, "Old history mutated with another play.");
        try { ((IList<TurnPlaySnapshot>)old.Turns.Single().Plays).Clear(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Nested play history remained mutable.");
    }
}
