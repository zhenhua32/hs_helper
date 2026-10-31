using System.Collections.Frozen;
using HsHelper.Core;

public static class ProjectedChoiceEntityChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("actual ready own choice entities share the current and frozen anonymous tag projection", CurrentProjection);
        yield return ("current offered identity eligibility follows the offer rather than a hidden zone allowlist", OfferedZones);
        yield return ("current option projection requires complete stable unique own choice scope", Scope);
        yield return ("current option projection rejects descriptor entity identity and owner conflicts", Identity);
        yield return ("current option projection requires an actual existing source entity", Source);
        yield return ("current option projection rejects extra private tags and equal but unbound entity copies", ExtraTagsAndObjects);
        yield return ("completed superseded and ordinary hidden entities cannot reuse old offer visibility", OldOffers);
        yield return ("real parser unidentified or conflicting candidates remain private without changing projection semantics", ParserConflicts);
    }

    private static void CurrentProjection()
    {
        var engine = Start();
        Feed(engine, ["TAG_CHANGE Entity=80 tag=RUSH value=1", "TAG_CHANGE Entity=80 tag=TAG_SCRIPT_DATA_NUM_1 value=2",
            "TAG_CHANGE Entity=80 tag=4572 value=1", "TAG_CHANGE Entity=80 tag=4573 value=0", "TAG_CHANGE Entity=80 tag=PRIVATE_REFERENCE value=PRIVATE_CANDIDATE_PROBE"]);
        Check(!Accept(engine.Snapshot(), 80), "An unoffered private candidate became publicly eligible.");
        Feed(engine, ChoiceHistoryRegression.Offer(8)); var frame = engine.Snapshot();
        Check(Accept(frame, 80) && Accept(frame, 81) && frame.Entities[80].Zone == "SETASIDE" && frame.Entities[80].Cost == 4 &&
            frame.Entities[80].GetIntTag("TAG_SCRIPT_DATA_NUM_1") == 2 && frame.Entities[80].GetIntTag("CRAFTABLE_FIRST_HALF_DBID") == 1 &&
            !frame.Entities[80].Tags.ContainsKey("PLAYER_NAME") && !frame.Entities[80].Tags.ContainsKey("PRIVATE_REFERENCE"),
            "The actual anonymous own current option was rejected or its projection changed.");
        Select(engine, 8); var prior = engine.Snapshot().ChoiceHistory!.Entries.Single().OptionEntities.Single(entity => entity.Id == 80);
        Check(prior.Cost == frame.Entities[80].Cost && prior.GetIntTag("CRAFTABLE_FIRST_HALF_DBID") == 1 &&
            prior.GetIntTag("TAG_SCRIPT_DATA_NUM_1") == 2 && !prior.Tags.ContainsKey("PRIVATE_REFERENCE"),
            "Current and frozen candidate projections no longer use the same observed tag policy.");
    }

    private static void OfferedZones()
    {
        foreach (var zone in new string?[] { "DECK", "SETASIDE", "REMOVEDFROMGAME", "INVALID", null })
        {
            var lines = ChoiceHistoryRegression.InitialLines().Where(line => zone is not null || line != "tag=ZONE value=SETASIDE")
                .Select(line => line == "tag=ZONE value=SETASIDE" ? "tag=ZONE value=" + zone : line);
            var engine = Start(lines); Feed(engine, ChoiceHistoryRegression.Offer(8)); var frame = engine.Snapshot();
            Check(frame.Entities[80].Zone == zone && Accept(frame, 80) && !Accept(frame, 84),
                "A zone-name exception replaced exact offered membership or a valid Core anonymous offer was rejected.");
        }
    }

    private static void Scope()
    {
        var frame = Ready();
        var extraPlayer = new EntitySnapshot(99, null, EntityKind.Player,
            new Dictionary<string, string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary());
        foreach (var bad in new[] { frame with { Choice = null }, frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
            frame with { LocalPlayerId = 2 }, frame with { Phase = GamePhase.Main }, frame with { Phase = GamePhase.Complete },
            frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
            frame with { Quality = frame.Quality with { HasLocalPerspective = false } }, frame with { Quality = frame.Quality with { OpenBlockCount = 1 } },
            frame with { Choice = frame.Choice! with { IsReady = false } }, frame with { Choice = frame.Choice! with { PlayerId = 2 } },
            frame with { Choice = frame.Choice! with { CountMax = 9 } },
            frame with { Choice = frame.Choice! with { Options = [frame.Choice.Options[0], frame.Choice.Options[0]] } },
            frame with { Entities = frame.Entities.Values.Append(extraPlayer).ToFrozenDictionary(entity => entity.Id) } })
            Check(!Accept(bad, 80), "An unconfirmed current-choice scope enabled a private projected entity.");
    }

    private static void Identity()
    {
        var frame = Ready(); var candidate = frame.Entities[80];
        foreach (var card in new string?[] { null, "", " ", "DIFFERENT_CARD" })
        {
            Check(!Accept(WithEntity(frame, candidate with { CardId = card }), 80), "An unknown or different actual identity matched a provided candidate.");
            Check(!Accept(frame with { Choice = frame.Choice! with { Options = frame.Choice.Options.Select(option =>
                option.EntityId == 80 ? option with { CardId = card } : option).ToArray() } }, 80), "An unconfirmed offered identity borrowed current entity data.");
        }
        foreach (var owner in new int?[] { null, 2 })
        {
            Check(!Accept(WithEntity(frame, WithTags(candidate, owner is null ? null : owner.Value.ToString())), 80),
                "An unconfirmed or opposing actual entity borrowed the chooser's ownership.");
            Check(!Accept(frame with { Choice = frame.Choice! with { Options = frame.Choice.Options.Select(option =>
                option.EntityId == 80 ? option with { ControllerId = owner } : option).ToArray() } }, 80),
                "A descriptor without the explicit projected owner became a Core own projection.");
        }
        foreach (var kind in new[] { EntityKind.Unknown, EntityKind.Game, EntityKind.Player })
            Check(!Accept(WithEntity(frame, candidate with { Kind = kind }), 80), "A non-card entity was accepted as a projected option.");
        var entities = frame.Entities.ToDictionary(); entities[80] = candidate with { Id = 79 };
        Check(!Accept(frame with { Entities = entities.ToFrozenDictionary() }, 80), "A dictionary-key/entity-ID conflict matched a candidate.");
    }

    private static void Source()
    {
        var frame = Ready();
        foreach (var source in new int?[] { null, 0, 799 })
            Check(!Accept(frame with { Choice = frame.Choice! with { SourceEntityId = source } }, 80), "A missing or invalid source established a current projection.");
        Check(!Accept(frame with { Entities = frame.Entities.Where(pair => pair.Key != 30).ToFrozenDictionary() }, 80),
            "An absent source entity established a current projection.");
        var entities = frame.Entities.ToDictionary(); entities[30] = entities[30] with { Id = 31 };
        Check(!Accept(frame with { Entities = entities.ToFrozenDictionary() }, 80), "A source dictionary-key/ID conflict established a current projection.");
    }

    private static void ExtraTagsAndObjects()
    {
        var frame = Ready(); var entity = frame.Entities[80];
        Check(!ChoiceVisibility.IsProjectedCurrentOptionEntity(frame, entity with { }) &&
            !ChoiceVisibility.IsProjectedCurrentOptionEntity(frame, null!), "A detached value-equal entity or null became the frame's actual option object.");
        foreach (var tag in new[] { "NAME", "ACCOUNT_ID", "PRIVATE_REFERENCE", "CREATOR", "47", "4572", "raw_private_tag" })
        {
            var altered = entity with { Tags = entity.Tags.Append(new(tag, "PRIVATE_CANDIDATE_PROBE")).ToFrozenDictionary() };
            Check(!ChoiceVisibility.IsProjectedCurrentOptionEntity(WithEntity(frame, altered), altered),
                "A private or unnormalized extra tag bypassed the anonymous current-option projection policy.");
        }
        var removed = frame with { Entities = frame.Entities.Where(pair => pair.Key != 80).ToFrozenDictionary() };
        Check(!ChoiceVisibility.IsProjectedCurrentOptionEntity(removed, entity), "An object absent from the actual snapshot became a current option.");
    }

    private static void OldOffers()
    {
        var engine = Start(); Feed(engine, ChoiceHistoryRegression.Offer(8)); var previous = engine.Snapshot();
        Select(engine, 8); var completed = engine.Snapshot();
        Check(!Accept(completed, 80) && !ChoiceVisibility.IsProjectedCurrentOptionEntity(completed, previous.Entities[80]),
            "A completed choice retained its projected visibility through a current entity or old object.");
        Feed(engine, ChoiceHistoryRegression.Offer(9, first: 82, second: 83)); var next = engine.Snapshot();
        Check(Accept(next, 82) && !Accept(next, 80) && !Accept(next, 84), "A new offer enabled old or unrelated private SETASIDE slots.");
        var superseded = Start(); Feed(superseded, ChoiceHistoryRegression.Offer(8)); Feed(superseded, ChoiceHistoryRegression.Offer(9, first: 82, second: 83));
        Check(!Accept(superseded.Snapshot(), 80) && Accept(superseded.Snapshot(), 82), "An unchosen superseded offer remained current.");
    }

    private static void ParserConflicts()
    {
        var unknown = Start(ChoiceHistoryRegression.InitialLines().Select(line => line == "FULL_ENTITY - Creating ID=80 CardID=FIRST_A"
            ? "FULL_ENTITY - Creating ID=80 CardID=" : line));
        Feed(unknown, ChoiceHistoryRegression.Offer(8)); var unidentified = unknown.Snapshot();
        Check(unidentified.Choice?.Options.Single(option => option.EntityId == 80).CardId == "FIRST_A" &&
            unidentified.Entities[80].CardId is null && !Accept(unidentified, 80), "An offered name labeled unrelated residual candidate numbers.");
        foreach (var conflict in new[] { "TAG_CHANGE Entity=80 tag=CONTROLLER value=2", "CHANGE_ENTITY - Updating Entity=80 CardID=DIFFERENT_CARD" })
        {
            var engine = Start(); Feed(engine, ChoiceHistoryRegression.Offer(8)); engine.ProcessLine(conflict); var frame = engine.Snapshot();
            Check(frame.Entities[80].CardId is null && !Accept(frame, 80), "A real parser conflict exposed or accepted stale candidate attributes.");
        }
        var watching = Start(); Feed(watching, ChoiceHistoryRegression.Offer(8)); watching.ProcessLine("Begin Spectating");
        Check(!Accept(watching.Snapshot(), 80), "A spectator retained private current options.");
        var opposing = Start(); Feed(opposing, ChoiceHistoryRegression.Offer(8, player: 2));
        Check(!Accept(opposing.Snapshot(), 80), "An opposing current choice enabled private candidates.");
    }

    private static EntitySnapshot WithTags(EntitySnapshot entity, string? controller)
    {
        var tags = entity.Tags.ToDictionary(); if (controller is null) tags.Remove("CONTROLLER"); else tags["CONTROLLER"] = controller;
        return entity with { Tags = tags.ToFrozenDictionary() };
    }
    private static GameSnapshot WithEntity(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static bool Accept(GameSnapshot snapshot, int id) => ChoiceVisibility.IsProjectedCurrentOptionEntity(snapshot, snapshot.Entities[id]);
    private static GameSnapshot Ready() { var engine = Start(); Feed(engine, ChoiceHistoryRegression.Offer(8)); return engine.Snapshot(); }
    private static PowerLogStateEngine Start(IEnumerable<string>? lines = null)
    { var engine = new PowerLogStateEngine(1, "projected-choice"); Feed(engine, lines ?? ChoiceHistoryRegression.InitialLines()); return engine; }
    private static void Select(PowerLogStateEngine engine, int choice) => Feed(engine,
        [ChoiceHistoryRegression.ChosenHeader(choice), ChoiceHistoryRegression.ChosenRow(80), "boundary"]);
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
