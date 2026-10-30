using HsHelper.Core;
using System.Collections.Frozen;
using System.Text.Json;

public static class ChoiceHistoryRegression
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("choice history binds actual chosen rows to the immediately preceding offer", TwoStages);
        yield return ("chosen header without actual rows cannot establish a selected component", MissingChosenRows);
        yield return ("choice history freezes offered source identity and observed candidate attributes", FrozenOffer);
        yield return ("repeated choice headers and rows cannot rewrite completed history", Duplicates);
        yield return ("unknown and stale chosen headers cannot select a current candidate", UnknownAndStale);
        yield return ("wrong owner and unoffered chosen entities invalidate choice history", InvalidSelections);
        yield return ("chosen row positions and counts must be complete and bounded", InvalidCounts);
        yield return ("intervening offers break multistage adjacency without exposing private choices", InterveningOffer);
        yield return ("choice history is scoped to the perspective that actually observed it", PerspectivePrivacy);
        yield return ("spectator and unconfirmed choices never become private history retroactively", SpectatorPrivacy);
        yield return ("perspective changes between choice header and rows cannot capture private identities", MidOfferPerspective);
        yield return ("only ready confirmed current choice candidates expose safe setaside attributes", CandidateProjection);
        yield return ("conflicting candidate identities and controllers cannot expose setaside attributes", ConflictingProjection);
        yield return ("candidate descriptors cannot label numeric attributes of an unidentified entity", UnidentifiedCandidate);
        yield return ("source and chosen descriptors cannot overwrite conflicting offered identities", DescriptorConflicts);
        yield return ("conflicting repeated ready choices become nonactionable", ReadyConflicts);
        yield return ("choice history is bounded and resets at match and session boundaries", BoundedAndReset);
        yield return ("choice history models freeze input collections and survive readonly JSON settings", ImmutableAndJson);
        yield return ("empty actual choice is recorded only when its cardinality allows zero", EmptyChoice);
    }

    // Synthetic protocol replay; no player names or live-game records. Both header and row
    // forms match HearthSim/python-hslog tokens.py CHOICES_* and ENTITIES_CHOSEN_*.
    public static string[] TwoStageFixture() => [.. InitialLines(), .. Offer(8, 1, 80, 81),
        ChosenHeader(8), ChosenRow(80), .. Offer(9, 1, 82, 83)];

    public static string[] InitialLines() => [
        "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED",
        "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=8", "tag=STEP value=MAIN_ACTION",
        "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0",
        "FULL_ENTITY - Creating ID=30 CardID=TLC_EVENT_400", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
        "tag=QUEST_PROGRESS value=3", "tag=QUEST_PROGRESS_TOTAL value=3",
        .. Candidate(80, "FIRST_A", 1, 4, 3, 5), .. Candidate(81, "FIRST_B", 1, 2, 2, 2),
        .. Candidate(82, "SECOND_A", 1, 3, 2, 4), .. Candidate(83, "SECOND_B", 1, 1, 1, 1),
        .. Candidate(84, "UNRELATED_PRIVATE", 1, 9, 9, 9),
        "PowerProcessor.EndCurrentTaskList() - complete"];

    public static string[] Offer(int id, int player = 1, int first = 80, int second = 81) => [
        $"GameState.DebugPrintEntityChoices() - id={id} Player={player} TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
        "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 player=1]",
        $"GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id={first} zone=SETASIDE cardId={Card(first)} player={player}]",
        $"GameState.DebugPrintEntityChoices() - Entities[1]=[entityName=Anonymous id={second} zone=SETASIDE cardId={Card(second)} player={player}]",
        "ChoiceCardMgr.OnChoicesShown() - shown"];

    public static string ChosenHeader(int id, int player = 1, int count = 1) =>
        $"GameState.DebugPrintEntitiesChosen() - id={id} Player={player} EntitiesCount={count}";
    public static string ChosenRow(int entity, int index = 0) => $"GameState.DebugPrintEntitiesChosen() - Entities[{index}]={entity}";

    private static string Card(int id) => id switch { 80 => "FIRST_A", 81 => "FIRST_B", 82 => "SECOND_A", 83 => "SECOND_B", _ => "UNRELATED_PRIVATE" };
    private static string[] Candidate(int id, string card, int owner, int cost, int attack, int health) => [
        $"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=SETASIDE", "tag=CARDTYPE value=MINION",
        $"tag=COST value={cost}", $"tag=ATK value={attack}", $"tag=HEALTH value={health}", "tag=DAMAGE value=0", "tag=CARDRACE value=20",
        "tag=PLAYER_NAME value=SHOULD_NOT_LEAK", "tag=ACCOUNT_ID value=SHOULD_NOT_LEAK"];
    private static PowerLogStateEngine Replay(IEnumerable<string>? lines = null, int? local = 1)
    {
        var engine = new PowerLogStateEngine(local, "choice-history");
        Feed(engine, lines ?? InitialLines()); return engine;
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static ChoiceHistorySnapshot History(PowerLogStateEngine engine) => engine.Snapshot().ChoiceHistory ?? throw new Exception("History missing");
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Select(PowerLogStateEngine engine, int id, int entity = 80) { engine.ProcessLine(ChosenHeader(id)); engine.ProcessLine(ChosenRow(entity)); }

    private static void TwoStages()
    {
        var engine = Replay(TwoStageFixture()); var snapshot = engine.Snapshot(); var history = History(engine); var prior = history.Entries.Single();
        Assert(history.IsComplete && prior.IsComplete && prior.ChoiceId == 8 && prior.ChosenEntityIds.SequenceEqual([80]), "Actual selected first component missing");
        Assert(prior.SourceCardId == "TLC_EVENT_400" && prior.SourceEntityId == 30 && prior.OptionEntities.Single(e => e.Id == 80).Cost == 4, "Observed source/candidate evidence missing");
        Assert(snapshot.Choice is { Id: 9, IsReady: true } current && current.PreviousOfferedAtEventSequence == prior.OfferedAtEventSequence &&
            current.OfferedAtEventSequence > prior.ChosenAtEventSequence, "Offer adjacency or time binding lost");
        Assert(history.MatchId == snapshot.MatchId && history.StateVersion == snapshot.StateVersion && history.EventSequence == snapshot.EventSequence, "History binding differs from frame");
        Assert(snapshot.Entities[82].Cost == 3 && snapshot.Entities[82].Attack == 2 && snapshot.Entities[82].RemainingHealth == 4, "Current second component attributes are hidden");
    }
    private static void MissingChosenRows()
    {
        var engine = Replay(); Feed(engine, Offer(8)); engine.ProcessLine(ChosenHeader(8));
        Assert(engine.Snapshot().Choice is null && !History(engine).IsComplete && History(engine).Entries.Count == 0, "Header was guessed as an actual selection");
        Feed(engine, Offer(9, first: 82, second: 83));
        Assert(!History(engine).IsComplete && History(engine).Entries.Count == 0, "Later offer repaired missing chosen rows");
    }
    private static void FrozenOffer()
    {
        var engine = Replay(); Feed(engine, Offer(8)); var offered = engine.Snapshot();
        engine.ProcessLine("TAG_CHANGE Entity=80 tag=COST value=0");
        engine.ProcessLine("CHANGE_ENTITY - Updating Entity=30 CardID=TRANSFORMED_SOURCE");
        Select(engine, 8); var prior = History(engine).Entries.Single();
        Assert(prior.SourceCardId == "TLC_EVENT_400" && prior.OptionEntities.Single(e => e.Id == 80).Cost == 4, "Offer was backfilled from later entity changes");
        Assert(offered.Entities[80].Cost == 4 && !prior.OptionEntities.Any(e => e.Tags.ContainsKey("PLAYER_NAME") || e.Tags.ContainsKey("ACCOUNT_ID")), "Frozen snapshot changed or unsafe tags persisted");
    }
    private static void Duplicates()
    {
        var engine = Replay(); Feed(engine, Offer(8)); var first = engine.Snapshot().Choice!;
        Feed(engine, Offer(8)); Assert(engine.Snapshot().Choice!.OfferedAtEventSequence == first.OfferedAtEventSequence, "Duplicate header rewrote offer time");
        Select(engine, 8); var completed = History(engine).Entries.Single();
        Feed(engine, Offer(8)); Select(engine, 8);
        Assert(History(engine).Entries.Count == 1 && History(engine).Entries[0].ChosenAtEventSequence == completed.ChosenAtEventSequence, "Duplicate selection rewrote completed history");
    }
    private static void UnknownAndStale()
    {
        var engine = Replay(); Feed(engine, Offer(8)); Select(engine, 99);
        Assert(History(engine).Entries.Count == 0 && engine.Snapshot().Choice?.Id == 8 && !History(engine).IsComplete, "Future chosen ID selected current choice");
        Feed(engine, Offer(9, first: 82, second: 83)); Select(engine, 8);
        Assert(History(engine).Entries.Count == 0 && engine.Snapshot().Choice?.Id == 9, "Stale chosen header selected current choice");
    }
    private static void InvalidSelections()
    {
        foreach (var row in new[] { ChosenRow(84), "GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=Anonymous id=80 cardId=FIRST_A player=2]" })
        {
            var engine = Replay(); Feed(engine, Offer(8)); engine.ProcessLine(ChosenHeader(8)); engine.ProcessLine(row); engine.ProcessLine("boundary");
            Assert(History(engine).Entries.Count == 0 && !History(engine).IsComplete, "Invalid selection became actual choice evidence");
        }
        var wrongHeader = Replay(); Feed(wrongHeader, Offer(8)); wrongHeader.ProcessLine(ChosenHeader(8, 2)); wrongHeader.ProcessLine(ChosenRow(80));
        Assert(History(wrongHeader).Entries.Count == 0 && !History(wrongHeader).IsComplete, "Wrong chooser bound to local history");
    }
    private static void InvalidCounts()
    {
        foreach (var (count, index) in new[] { (1, 1), (2, 0), (65, 0) })
        {
            var engine = Replay(); Feed(engine, Offer(8)); engine.ProcessLine(ChosenHeader(8, count: count)); engine.ProcessLine(ChosenRow(80, index)); engine.ProcessLine("boundary");
            Assert(!History(engine).IsComplete && History(engine).Entries.Count == 0, "Invalid count/index established a complete selection");
        }
    }
    private static void InterveningOffer()
    {
        var engine = Replay(); Feed(engine, Offer(8)); Select(engine, 8); var original = History(engine).Entries.Single();
        Feed(engine, Offer(18, 2)); Feed(engine, Offer(9, first: 82, second: 83));
        Assert(engine.Snapshot().Choice!.PreviousOfferedAtEventSequence != original.OfferedAtEventSequence, "Intervening opponent offer was skipped for stage association");
        Assert(History(engine).Entries.Count == 1 && History(engine).Entries.All(e => e.PlayerId == 1), "Opponent private offer leaked into history");
    }
    private static void PerspectivePrivacy()
    {
        var engine = Replay(TwoStageFixture()); var observed = History(engine);
        engine.SetLocalPlayerId(2);
        Assert(engine.Snapshot().Choice is null && History(engine).Entries.Count == 0 && engine.Snapshot().Entities[82].CardId is null, "Own choice leaked after perspective switch");
        engine.SetLocalPlayerId(1); Assert(History(engine).Entries.Count == 1 && observed.Entries.Count == 1, "Returning to actual observation lost frozen history");
        var other = Replay(); Feed(other, Offer(8, 2)); other.ProcessLine(ChosenHeader(8, 2)); other.ProcessLine(ChosenRow(80)); other.SetLocalPlayerId(2);
        Assert(History(other).Entries.Count == 0 && !History(other).IsComplete && other.Snapshot().Choice is null, "Previously unobserved choice appeared after switching to opponent");
    }
    private static void SpectatorPrivacy()
    {
        foreach (var spectator in new[] { false, true })
        {
            var engine = Replay(local: spectator ? 1 : null); if (spectator) engine.ProcessLine("Begin Spectating");
            Feed(engine, Offer(8)); Select(engine, 8);
            Assert(History(engine).Entries.Count == 0 && engine.Snapshot().Entities[80].Cost is null && engine.Snapshot().Choice is null, "Unconfirmed/spectator private choice leaked");
            if (spectator) engine.ProcessLine("End Spectator"); engine.SetLocalPlayerId(1);
            Assert(History(engine).Entries.Count == 0 && !History(engine).IsComplete, "Private history was filled retrospectively after spectator/unconfirmed observation");
        }
        var unconfirmedPlayers = Replay(InitialLines().Where(line => !line.StartsWith("Player EntityID=", StringComparison.Ordinal)));
        Feed(unconfirmedPlayers, Offer(8)); Select(unconfirmedPlayers, 8);
        Feed(unconfirmedPlayers, ["Player EntityID=2 PlayerID=1", "Player EntityID=3 PlayerID=2"]);
        Assert(History(unconfirmedPlayers).Entries.Count == 0 && !History(unconfirmedPlayers).IsComplete, "Manual player id without confirmed player entities captured private history");
    }
    private static void CandidateProjection()
    {
        var engine = Replay(); Feed(engine, Offer(8).SkipLast(1));
        Assert(engine.Snapshot().Entities[80].CardId is null, "Incomplete choice exposed candidate tags");
        engine.ProcessLine("boundary"); var ready = engine.Snapshot();
        Assert(ready.Entities[80].Cost == 4 && ready.Entities[80].CardId == "FIRST_A" && ready.Entities[84].CardId is null &&
            !ready.Entities[80].Tags.ContainsKey("PLAYER_NAME"), "Candidate whitelist leaked unrelated private entity or unsafe tags");
        engine.ProcessLine("Begin Spectating"); Assert(engine.Snapshot().Choice is null && engine.Snapshot().Entities[80].CardId is null, "Manual perspective exposed spectator candidate");
        engine.ProcessLine("End Spectator"); Select(engine, 8);
        Assert(engine.Snapshot().Entities[80].CardId is null && engine.Snapshot().Entities[80].Cost is null, "Completed candidate polluted general entity cache");
    }
    private static void MidOfferPerspective()
    {
        foreach (var spectator in new[] { false, true })
        {
            var engine = Replay(); engine.ProcessLine(Offer(8)[0]);
            if (spectator) engine.ProcessLine("Begin Spectating"); else engine.SetLocalPlayerId(2);
            Feed(engine, Offer(8).Skip(1));
            if (spectator) engine.ProcessLine("End Spectator"); else engine.SetLocalPlayerId(1);
            var returned = engine.Snapshot();
            Assert(returned.Choice is { IsReady: false, Options.Count: 0, SourceCardId: null } && returned.Entities[80].CardId is null,
                "Private source or candidates observed in another perspective were filled after returning");
            Select(engine, 8); engine.ProcessLine("boundary");
            Assert(!History(engine).IsComplete && History(engine).Entries.Count == 0, "Scope changes during offer produced a complete history");
        }
        var chosen = Replay(); Feed(chosen, Offer(8)); chosen.ProcessLine(ChosenHeader(8)); chosen.SetLocalPlayerId(2);
        chosen.ProcessLine(ChosenRow(80)); chosen.SetLocalPlayerId(1); chosen.ProcessLine("boundary");
        Assert(!History(chosen).IsComplete && History(chosen).Entries.Count == 0, "Chosen rows from another perspective were stored privately");
    }
    private static void ConflictingProjection()
    {
        foreach (var line in new[] { "TAG_CHANGE Entity=80 tag=CONTROLLER value=2", "CHANGE_ENTITY - Updating Entity=80 CardID=OTHER_ID" })
        {
            var engine = Replay(); Feed(engine, Offer(8)); engine.ProcessLine(line);
            Assert(engine.Snapshot().Entities[80].CardId is null && engine.Snapshot().Entities[80].Cost is null, "Conflicting current candidate exposed stale private attributes");
        }
    }
    private static void UnidentifiedCandidate()
    {
        var lines = InitialLines().Select(line => line == "FULL_ENTITY - Creating ID=80 CardID=FIRST_A" ? "FULL_ENTITY - Creating ID=80 CardID=" : line);
        var engine = Replay(lines); Feed(engine, Offer(8)); var snapshot = engine.Snapshot();
        Assert(snapshot.Choice is { IsReady: true } && snapshot.Choice.Options.Single(option => option.EntityId == 80).CardId == "FIRST_A" &&
            snapshot.Entities[80].Cost is null && snapshot.Entities[80].Attack is null && snapshot.Entities[80].RemainingHealth is null,
            "Descriptor attached an identity to unidentified residual numeric tags or hid the actual offered option");
        Select(engine, 8); var history = History(engine); var prior = history.Entries.Single();
        Assert(history.IsComplete && prior.ChosenEntityIds.SequenceEqual([80]) && prior.Options.Single(option => option.EntityId == 80).CardId == "FIRST_A" &&
            !prior.OptionEntities.Any(entity => entity.Id == 80), "Unknown candidate attributes were frozen under the descriptor identity or treated as a conflict");
    }
    private static void BoundedAndReset()
    {
        var engine = Replay(); for (var id = 1; id <= 34; id++) { Feed(engine, Offer(id)); Select(engine, id); }
        var history = History(engine);
        Assert(history.Entries.Count == 32 && history.Entries[0].ChoiceId == 3 && history.TruncatedThroughSequence > 0 && !history.IsComplete, "History cap/truncation missing");
        engine.ProcessLine("CREATE_GAME"); Assert(History(engine).Entries.Count == 0 && History(engine).TruncatedThroughSequence == 0 && history.Entries.Count == 32, "New game retained or mutated history");
        engine.ResetSession(); Assert(History(engine).Entries.Count == 0 && !History(engine).IsComplete, "Session reset retained complete history");
    }
    private static void ReadyConflicts()
    {
        foreach (var conflict in new[] {
            "GameState.DebugPrintEntityChoices() - id=8 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=2",
            "GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=80 zone=SETASIDE cardId=OTHER_ID player=1]",
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=BE_044 player=1]" })
        {
            var engine = Replay(); Feed(engine, Offer(8));
            if (!conflict.Contains(" - id=", StringComparison.Ordinal)) engine.ProcessLine(Offer(8)[0]);
            engine.ProcessLine(conflict); engine.ProcessLine("boundary"); var snapshot = engine.Snapshot();
            Assert(snapshot.Choice is { IsReady: false } && !snapshot.Quality.IsStable && snapshot.Entities[80].CardId is null,
                "Conflicting duplicate retained a ready current choice or private candidate projection");
        }
    }
    private static void DescriptorConflicts()
    {
        foreach (var source in new[] {
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=BE_044 player=1]",
            "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=TLC_EVENT_400 player=2]" })
        {
            var engine = Replay(); var offer = Offer(8); offer[1] = source; Feed(engine, offer); Select(engine, 8);
            Assert(!History(engine).IsComplete && History(engine).Entries.Count == 0, "Conflicting source descriptor was attributed to current source card");
        }
        var duplicateSource = Replay(); Feed(duplicateSource, Offer(8));
        duplicateSource.ProcessLine("GameState.DebugPrintEntityChoices() - id=8 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1");
        duplicateSource.ProcessLine("GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=BE_044 player=1]");
        Select(duplicateSource, 8);
        Assert(!History(duplicateSource).IsComplete && History(duplicateSource).Entries.Count == 0, "Duplicate source descriptor rewrote first captured identity");
        var selected = Replay(); Feed(selected, Offer(8)); selected.ProcessLine(ChosenHeader(8));
        selected.ProcessLine("GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=Anonymous id=80 cardId=OTHER_ID player=1]");
        selected.ProcessLine("boundary");
        Assert(!History(selected).IsComplete && History(selected).Entries.Count == 0, "Conflicting chosen card identity reused old candidate stats");
        var unknownIdentity = Replay(); Feed(unknownIdentity, Offer(8)); unknownIdentity.ProcessLine(ChosenHeader(8));
        unknownIdentity.ProcessLine("GameState.DebugPrintEntitiesChosen() - Entities[0]=[entityName=Anonymous id=80 cardId= player=1]");
        Assert(History(unknownIdentity).IsComplete && History(unknownIdentity).Entries.Single().ChosenEntityIds.SequenceEqual([80]), "Empty chosen cardId erased the explicit actual entity selection");
    }
    private static void ImmutableAndJson()
    {
        var options = new List<ChoiceOptionSnapshot> { new(80, "A", 1, 0) }; var selected = new List<int> { 80 };
        var entities = new List<EntitySnapshot> { new(80, "A", EntityKind.Card, new Dictionary<string, string> { ["COST"] = "4" }.ToFrozenDictionary()) };
        var entry = new ChoiceHistoryEntry(8, 1, "GENERAL", 30, "TLC_EVENT_400", 1, 1, 10, 20, options, entities, selected, true);
        var entries = new List<ChoiceHistoryEntry> { entry }; var warnings = new List<string>();
        var history = new ChoiceHistorySnapshot("match", 4, 20, entries, true, 0, warnings);
        options.Clear(); entities.Clear(); selected.Clear(); entries.Clear(); warnings.Add("later");
        Assert(history.Entries.Count == 1 && entry.Options.Count == 1 && entry.OptionEntities[0].Cost == 4 && entry.ChosenEntityIds.Count == 1 && history.Warnings.Count == 0, "Constructor input collection remained mutable");
        var settings = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(history, settings));
        var decoded = document.RootElement; var decodedEntry = decoded.GetProperty("Entries")[0];
        Assert(decoded.GetProperty("MatchId").GetString() == "match" && decodedEntry.GetProperty("SourceCardId").GetString() == "TLC_EVENT_400" &&
            decodedEntry.GetProperty("ChosenEntityIds")[0].GetInt32() == 80 && decodedEntry.GetProperty("OptionEntities")[0].GetProperty("Tags").GetProperty("COST").GetString() == "4",
            "Readonly JSON settings dropped ledger evidence");
    }
    private static void EmptyChoice()
    {
        var engine = Replay(); engine.ProcessLine("GameState.DebugPrintEntityChoices() - id=8 Player=1 TaskList= ChoiceType=GENERAL CountMin=0 CountMax=0");
        engine.ProcessLine("boundary"); engine.ProcessLine(ChosenHeader(8, count: 0));
        Assert(History(engine).IsComplete && History(engine).Entries.Single().ChosenEntityIds.Count == 0, "Actual permitted empty choice was lost");
        var invalid = Replay(); Feed(invalid, Offer(8)); invalid.ProcessLine(ChosenHeader(8, count: 0));
        Assert(!History(invalid).IsComplete && History(invalid).Entries.Count == 0, "Empty selection satisfied a mandatory choice");
    }
}
