using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class MapFollowupChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Map initial offered odd-Attack Beast choice grants only its selected card", () =>
        {
            var frame = InitialFrame(); var result = Eval(frame, cards, 81);
            Check(result.Stage == "InitialChoice" && result.ResolvesEffects && result.FollowupConditionMet is null &&
                result.SelectedOptionEntityId == 81 && result.SelectedCardId == "EDR_227" && result.AddedToHand == true &&
                result.HandCountBefore == 3 && result.HandCountAfter == 4 && result.RemainingOptions.Count == 0 && !result.CanEvaluateFollowupActions,
                "Initial selection fabricated a second choice or failed to grant one direct card.");
        });
        yield return ("Map existing followup binds actual chosen same entity to original own turn and grants one remaining option", () =>
        {
            var result = Eval(Frame(), cards, 81);
            Check(result.Stage == "FollowupChoice" && result.ResolvesEffects && result.FollowupConditionMet == true && result.InitialChoiceId == 1 &&
                result.InitialSelectedEntityId == 80 && result.InitialSelectedCardId == "CORE_UNG_912" && result.InitialChosenAtEventSequence == 40 &&
                result.InitialMapPlayedAtEventSequence == 20 && result.InitialSelectedPlayedAtEventSequence == 60 && result.TurnNumber == 5 &&
                result.SelectedOptionEntityId == 81 && result.AddedToHand == true && result.HandCountAfter == 4 &&
                result.RemainingOptions.Select(option => option.EntityId).Order().SequenceEqual(new[] { 81, 82 }) &&
                !result.CanEvaluateFollowupActions, "Complete actual same-entity evidence failed or generated more than one card.");
        });
        yield return ("Map explicit offered identity works without copying unidentified entity residual traits", () =>
        {
            var frame = InitialFrame(); var unknownEntity = Tag(Tag(Tag(frame.Entities[82] with { CardId = null }, "CARDTYPE", "SPELL"), "CARDRACE", "DEMON"), "4572", "987654321");
            frame = Replace(frame, unknownEntity); var result = Eval(frame, cards, 82);
            Check(result.Stage == "InitialChoice" && result.ResolvesEffects && result.SelectedCardId == "EDR_480" && result.AddedToHand == true &&
                !JsonSerializer.Serialize(result).Contains("987654321", StringComparison.Ordinal), "Explicit offer identity was rejected or contaminated by unbound residual traits.");
            Unknown(Eval(Replace(frame, unknownEntity with { CardId = "EDR_227" }), cards, 82));
            Unknown(Eval(Replace(frame, Tag(unknownEntity, "CONTROLLER", "2")), cards, 82));
            Unknown(Eval(Replace(frame, Tag(unknownEntity, "ZONE", "HAND")), cards, 82));
        });
        yield return ("Map confirmed selected play without actual second offer waits instead of generating candidates", () =>
        {
            var frame = Frame() with { Choice = null, Phase = GamePhase.Main }; var result = Eval(frame, cards);
            Check(result.Stage == "AwaitingFollowupOffer" && result.FollowupConditionMet == true && !result.ResolvesEffects &&
                result.RemainingOptions.Count == 0 && result.AddedToHand is null && result.SelectedCardId is null && result.HandCountAfter is null,
                "Historical remaining options became a fabricated current offer or hand.");
        });
        yield return ("Map same CardId on another played entity never substitutes for the actual chosen card", () =>
        {
            var frame = WithPlays(Frame(), [Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Entity(90, "CORE_UNG_912", "HAND"))]);
            Unknown(Eval(frame, cards, 81));
            var pending = frame with { Choice = null, Phase = GamePhase.Main };
            Check(Eval(pending, cards).Stage == "AwaitingSelectedCardPlay" && Eval(pending, cards).FollowupConditionMet is null,
                "A same-name different entity completed the discovered card condition.");
            frame = Tag(frame, 90, "CREATOR", "30"); Unknown(Eval(frame, cards, 81));
        });
        yield return ("Map missing actual chosen entity to hand mapping stays unknown even with matching creator and card", () =>
        {
            var frame = Frame();
            var mapped = new ChoiceHistoryEntry(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 40,
                InitialOptions().Select(option => option.EntityId == 80 ? option with { EntityId = 79 } : option).ToArray(),
                First().OptionEntities.Select(entity => entity.Id == 80 ? entity with { Id = 79 } : entity).ToArray(), [79], true);
            frame = WithChoices(frame, [mapped]); frame = Tag(frame, 80, "CREATOR", "79"); Unknown(Eval(frame, cards, 81));
        });
        yield return ("Map current second offer must be exactly the original two remaining option entities", () =>
        {
            var frame = Frame();
            foreach (var options in new[] { new[] { new ChoiceOptionSnapshot(80, "CORE_UNG_912", 1, 0), new ChoiceOptionSnapshot(81, "EDR_227", 1, 1) },
                new[] { new ChoiceOptionSnapshot(91, "EDR_227", 1, 0), new ChoiceOptionSnapshot(92, "EDR_480", 1, 1) },
                new[] { new ChoiceOptionSnapshot(81, "EDR_480", 1, 0), new ChoiceOptionSnapshot(82, "EDR_227", 1, 1) } })
                Unknown(Eval(frame with { Choice = frame.Choice! with { Options = options } }, cards, options[0].EntityId));
        });
        yield return ("Map first option native odd Attack is checked without reusing modified current candidate Attack", () =>
        {
            var frame = Tag(Tag(Frame(), 81, "ATK", "0"), 82, "ATK", "2");
            Check(Eval(frame, cards, 81).ResolvesEffects, "Current post-offer attack overrides the native discover-pool property.");
            var even = InitialFrame(); var opts = even.Choice!.Options.Select(option => option.EntityId == 82 ? option with { CardId = "EDR_849" } : option).ToArray();
            even = Replace(even, Entity(82, "EDR_849", "SETASIDE")) with { Choice = even.Choice with { Options = opts } };
            Unknown(Eval(even, cards, 82));
        });
        yield return ("Map additional choice adds only one card and respects full unknown and conflicting hand capacity", () =>
        {
            var frame = Frame(); var full = Tag(frame, 1, "MAXHANDSIZE", "3"); var result = Eval(full, cards, 82);
            Check(result.AddedToHand == false && result.HandCountBefore == 3 && result.HandCountAfter == 3, "A full hand received an extra card or lost the original selection twice.");
            var unknown = RemoveTag(frame, 1, "MAXHANDSIZE"); result = Eval(unknown, cards, 81);
            Check(result.ResolvesEffects && result.AddedToHand is null && result.HandLimit is null && result.HandCountAfter is null, "An absent capacity became default ten.");
            foreach (var bad in new[] { Tag(frame, 1, "NUM_CARDS_IN_HAND", "broken"), Tag(frame, 1, "NUM_CARDS_IN_HAND", "4"), Tag(frame, 1, "NUM_CARDS_IN_HAND", "-1") })
            { result = Eval(bad, cards, 81); Check(result.AddedToHand is null && result.HandCountBefore is null && result.HandCountAfter is null, "Conflicting observed hand count became exact."); }
            result = Eval(Tag(frame, 1, "28", "2"), cards, 81); Check(result.HandLimit is null && result.AddedToHand is null, "Conflicting numeric hand limit alias became exact.");
            result = Eval(Tag(frame, 1, "CONTROLLER", "2"), cards, 81); Check(result.HandLimit is null && result.AddedToHand is null && result.HandCountBefore is null,
                "Contradictory player counter ownership supplied a hand capacity.");
        });
        yield return ("Map actual second selection is consumed once despite stale current choice or replay of the first card", () =>
        {
            var frame = WithChoices(Frame(), [First(), Second()]); var result = Eval(frame, cards, 81);
            Check(result.Stage == "FollowupAlreadyChosen" && !result.ResolvesEffects && result.AddedToHand is null, "A residual completed current choice granted again.");
            frame = frame with { Choice = frame.Choice! with { Id = 3, OfferedAtEventSequence = 95 } };
            result = Eval(frame, cards, 81); Check(result.Stage == "FollowupAlreadyChosen" && result.FollowupConditionMet == true && !result.ResolvesEffects,
                "A second residual offer reused an already-consumed Map instance.");
        });
        yield return ("Map original turn boundaries allow consecutive own turns and do not use turn minus two", () =>
        {
            var frame = Frame() with { Turn = 6 };
            var current = CurrentWindow(frame) with { };
            frame = WithTurns(frame, [new(5, 1, 1, 9, true, true, []), new(6, 1, 10, null, true, false, current.Plays)]);
            Check(Eval(frame, cards, 81).ResolvesEffects, "An actual consecutive extra own turn was rejected by arithmetic turn assumptions.");
        });
        yield return ("Map complete closed original turn without chosen play proves condition false", () =>
        {
            var frame = WithPlays(Frame(), [Play(20, Entity(30, "TLC_824", "HAND"))]); frame = NextTurn(frame);
            var result = Eval(frame, cards); Check(result.Stage == "ConditionNotMet" && result.FollowupConditionMet == false && !result.ResolvesEffects,
                "A complete original window with no selected entity play did not prove the expired condition false.");
            var withPlay = NextTurn(Frame()); result = Eval(withPlay, cards);
            Check(result.Stage == "FollowupWindowEnded" && result.FollowupConditionMet == true && result.RemainingOptions.Count == 0 && !result.ResolvesEffects,
                "A past successful condition created a future-turn followup offer.");
        });
        yield return ("Map wrong-turn play cannot satisfy original turn condition or authorize a later offered choice", () =>
        {
            var frame = WithPlays(Frame(), [Play(20, Entity(30, "TLC_824", "HAND"))]);
            var firstWindow = new TurnHistoryEntry(5, 1, 10, 70, true, true, CurrentWindow(frame).Plays);
            frame = frame with { Turn = 6 };
            frame = WithTurns(frame, [firstWindow, new(6, 1, 71, null, true, false, [Play(75, Entity(80, "CORE_UNG_912", "HAND"))])]);
            Unknown(Eval(frame, cards, 81));
        });
        yield return ("Map source and chosen actual plays require identities frozen before offers and choices", () =>
        {
            foreach (var plays in new[] { new[] { Play(35, Entity(30, "TLC_824", "HAND")), Play(60, Entity(80, "CORE_UNG_912", "HAND")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(90, Entity(80, "CORE_UNG_912", "HAND")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Entity(80, "EDR_227", "HAND")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Entity(80, "CORE_UNG_912", "HAND", 2)) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Tag(Entity(80, "CORE_UNG_912", "HAND"), "CARDTYPE", "SPELL")) },
                new[] { Play(20, RemoveTag(Entity(30, "TLC_824", "HAND"), "CARDTYPE")), Play(60, Entity(80, "CORE_UNG_912", "HAND")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(60, RemoveTag(Entity(80, "CORE_UNG_912", "HAND"), "CARDTYPE")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Entity(80, "CORE_UNG_912", "PLAY")) },
                new[] { Play(20, Entity(30, "TLC_824", "HAND")), Play(25, Entity(30, "EDR_849", "HAND")), Play(60, Entity(80, "CORE_UNG_912", "HAND")) } })
                Unknown(Eval(WithPlays(Frame(), plays), cards, 81));
        });
        yield return ("Map histories require exact snapshot binding completeness and real nonoverlapping own windows", () =>
        {
            var frame = Frame(); var history = frame.ChoiceHistory!; var turns = frame.TurnHistory!;
            foreach (var bad in new[] { frame with { ChoiceHistory = null }, frame with { TurnHistory = null },
                frame with { ChoiceHistory = new("FOREIGN", 10, 100, history.Entries, true, 0, []) },
                frame with { ChoiceHistory = new(frame.MatchId, 11, 100, history.Entries, true, 0, []) },
                frame with { ChoiceHistory = new(frame.MatchId, 10, 101, history.Entries, true, 0, []) },
                frame with { ChoiceHistory = new(frame.MatchId, 10, 100, history.Entries, false, 0, []) },
                frame with { ChoiceHistory = new(frame.MatchId, 10, 100, history.Entries, true, 30, []) },
                frame with { TurnHistory = new(frame.MatchId, 10, 100, turns.Turns, true, 15, []) },
                frame with { TurnHistory = new(frame.MatchId, 10, 100, turns.Turns, false, 0, []) },
                WithTurns(frame, [new(5, 1, 10, null, false, false, CurrentWindow(frame).Plays)]),
                WithTurns(frame, [new(4, 2, 1, 15, true, true, []), CurrentWindow(frame)]) }) Unknown(Eval(bad, cards, 81));
        });
        yield return ("Map actual chosen history must be unique complete and sourced by the offered Map identity", () =>
        {
            var frame = Frame(); var first = First();
            foreach (var entry in new[] { CopyFirst(sourceCard: null), CopyFirst(source: 31), CopyFirst(chosen: [81, 82]), CopyFirst(chosen: [999]),
                new ChoiceHistoryEntry(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 101, first.Options, first.OptionEntities, [80], true),
                new ChoiceHistoryEntry(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 40, first.Options, first.OptionEntities, [80], false),
                new ChoiceHistoryEntry(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 40, first.Options,
                    first.OptionEntities.Select(entity => entity.Id == 80 ? entity with { Tags = null! } : entity).ToArray(), [80], true) })
                Unknown(Eval(WithChoices(frame, [entry]), cards, 81));
            var duplicateInstance = new ChoiceHistoryEntry(9, 1, "GENERAL", 30, "TLC_824", 1, 1, 31, 41, first.Options, first.OptionEntities, [80], true);
            Unknown(Eval(WithChoices(frame, [first, duplicateInstance]), cards, 81));
        });
        yield return ("Map requires an actually used owned native source and candidate metadata cannot overwrite observed conflicts", () =>
        {
            var frame = Frame();
            foreach (var bad in new[] { Tag(frame, 30, "ZONE", "HAND"), Tag(frame, 30, "CONTROLLER", "2"), Tag(frame, 30, "CARDTYPE", "MINION"),
                Tag(frame, 30, "202", "4"), Tag(frame, 30, "49", "3"), Tag(frame, 81, "CARDTYPE", "SPELL"), Tag(frame, 82, "CARDRACE", "DEMON"),
                Tag(frame, 81, "200", "DRAGON"), Tag(frame, 81, "ZONE", "HAND"), Tag(frame, 82, "50", "2"),
                Tag(frame, 30, "ENTITY_ID", "31"), Tag(frame, 81, "53", "91") }) Unknown(Eval(bad, cards, 81));
            foreach (var key in new[] { "4572", "4573", "CRAFTABLE_FIRST_HALF_DBID", "CRAFTABLE_SECOND_HALF_DBID" })
            foreach (var value in new[] { "1", "117596", "bad", "-1" }) Unknown(Eval(Tag(frame, 30, key, value), cards, 81));
            Unknown(Eval(Tag(frame, 81, "4572", "1"), cards, 81));
        });
        yield return ("Map current choices reject unknown source identity duplicate or nonremaining options and selected ranges", () =>
        {
            var frame = Frame(); var choice = frame.Choice!;
            foreach (var bad in new[] { choice with { SourceCardId = null }, choice with { SourceCardId = "CORE_DS1_184" }, choice with { SourceEntityId = 31 },
                choice with { PlayerId = 2 }, choice with { IsReady = false }, choice with { CountMax = 2 }, choice with { OfferedAtEventSequence = 0 },
                choice with { Options = new[] { choice.Options[0], choice.Options[0] } } }) Unknown(Eval(frame with { Choice = bad }, cards, 81));
            Unknown(Eval(frame, cards, 999));
        });
        yield return ("Map fixed source metadata and native pool evidence must agree with RawJson", () =>
        {
            var frame = Frame();
            foreach (var card in new[] { cards["TLC_824"] with { DbfId = 117597 }, cards["TLC_824"] with { RawJson = "[]" },
                cards["TLC_824"] with { Text = "Discover three cards" }, cards["TLC_824"] with { Type = "MINION" } })
            { var changed = cards.ToDictionary(); changed["TLC_824"] = card; Unknown(Eval(frame, changed, 81)); }
            var missing = cards.ToDictionary(); missing.Remove("EDR_480"); Unknown(Eval(frame, missing, 81));
            var duplicate = cards.ToDictionary(); duplicate["TLC_824"] = cards["TLC_824"] with { RawJson = cards["TLC_824"].RawJson.Insert(1, "\"dbfId\":0,") };
            Unknown(Eval(frame, duplicate, 81));
        });
        yield return ("Map rejects incomplete spectator foreign and malformed perspectives without private options", () =>
        {
            var frame = Frame();
            foreach (var bad in new[] { frame with { ClientBuild = "255791" }, frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
                frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
                frame with { Quality = frame.Quality with { HasLocalPerspective = false } }, frame with { Quality = frame.Quality with { OpenBlockCount = 1 } },
                Replace(frame, frame.Entities[81] with { Tags = null! }) })
            { var result = Eval(bad, cards, 81); Unknown(result); Check(result.SelectedCardId is null && result.InitialSelectedCardId is null && result.RemainingOptions.Count == 0, "Rejected perspective leaked candidate identities."); }
        });
        yield return ("Map ignores hidden deck hand secret identities and leaves original snapshots catalog and lists immutable", () =>
        {
            var frame = Replace(Replace(Frame(), Entity(90, "PRIVATE_OPPONENT", "HAND", 2)), Entity(91, "PRIVATE_DECK", "DECK"));
            var before = JsonSerializer.Serialize(frame); var sourceCards = JsonSerializer.Serialize(cards); var result = Eval(frame, cards, 81);
            Check(!JsonSerializer.Serialize(result).Contains("PRIVATE_", StringComparison.Ordinal) && before == JsonSerializer.Serialize(frame) && sourceCards == JsonSerializer.Serialize(cards),
                "Map leaked unrelated hidden identity or changed authoritative inputs.");
            try { ((IList<MapFollowupOption>)result.RemainingOptions).Clear(); throw new Exception("Mutable Map options."); } catch (NotSupportedException) { }
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable Map unknowns."); } catch (NotSupportedException) { }
        });
    }

    private static MapFollowupOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string,CardRecord> cards, int? selected = null) => MapFollowupRules.Evaluate(frame, 30, cards, selected);
    private static ChoiceOptionSnapshot[] InitialOptions() => [new(80, "CORE_UNG_912", 1, 0), new(81, "EDR_227", 1, 1), new(82, "EDR_480", 1, 2)];
    private static ChoiceOptionSnapshot[] SecondOptions() => [new(81, "EDR_227", 1, 0), new(82, "EDR_480", 1, 1)];
    private static ChoiceHistoryEntry First() => new(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 40, InitialOptions(),
        InitialOptions().Select(option => Entity(option.EntityId, option.CardId!, "SETASIDE")).ToArray(), [80], true);
    private static ChoiceHistoryEntry CopyFirst(string? sourceCard = "TLC_824", int source = 30, IReadOnlyList<int>? chosen = null)
    { var first = First(); return new(1, 1, "GENERAL", source, sourceCard, 1, 1, 30, 40, first.Options, first.OptionEntities, chosen ?? [80], true); }
    private static ChoiceHistoryEntry Second() => new(2, 1, "GENERAL", 30, "TLC_824", 1, 1, 80, 90, SecondOptions(),
        SecondOptions().Select(option => Entity(option.EntityId, option.CardId!, "SETASIDE")).ToArray(), [81], true);
    private static TurnPlaySnapshot Play(long seq, EntitySnapshot entity) => new(seq, entity);
    private static TurnHistoryEntry CurrentWindow(GameSnapshot frame) => frame.TurnHistory!.Turns.Last();
    private static GameSnapshot Frame()
    {
        var entities = new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1", ["NUM_CARDS_IN_HAND"] = "3", ["MAXHANDSIZE"] = "10" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Entity(30, "TLC_824", "GRAVEYARD"), Entity(80, "CORE_UNG_912", "PLAY"), Entity(81, "EDR_227", "SETASIDE"), Entity(82, "EDR_480", "SETASIDE"),
            Entity(100, "CORE_UNG_912", "HAND"), Entity(101, "CORE_UNG_912", "HAND"), Entity(102, "CORE_UNG_912", "HAND") };
        var frame = new GameSnapshot("map-fixture", 10, 100, 5, 1, 1, GamePhase.Choice, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
            { ClientBuild = "255790", Choice = new(2, 1, "GENERAL", 30, 1, 1, SecondOptions(), true) { SourceCardId = "TLC_824", OfferedAtEventSequence = 80 } };
        frame = WithChoices(frame, [First()]); return WithTurns(frame, [new(5, 1, 10, null, true, false,
            [Play(20, Entity(30, "TLC_824", "HAND")), Play(60, Entity(80, "CORE_UNG_912", "HAND"))])]);
    }
    private static GameSnapshot InitialFrame()
    {
        var frame = Replace(Frame(), Entity(80, "CORE_UNG_912", "SETASIDE"));
        return frame with { ChoiceHistory = null, TurnHistory = null, Choice = new(1, 1, "GENERAL", 30, 1, 1, InitialOptions(), true)
            { SourceCardId = "TLC_824", OfferedAtEventSequence = 30 } };
    }
    private static GameSnapshot NextTurn(GameSnapshot frame)
    {
        var prior = CurrentWindow(frame); frame = frame with { Choice = null, Phase = GamePhase.Main, Turn = 6, ActivePlayerId = 2 };
        return WithTurns(frame, [new(5, 1, 10, 90, true, true, prior.Plays), new(6, 2, 91, null, true, false, [])]);
    }
    private static GameSnapshot WithPlays(GameSnapshot frame, IReadOnlyList<TurnPlaySnapshot> plays) => WithTurns(frame, [new(5, 1, 10, null, true, false, plays)]);
    private static GameSnapshot WithChoices(GameSnapshot frame, IReadOnlyList<ChoiceHistoryEntry> entries) => frame with { ChoiceHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, entries, true, 0, []) };
    private static GameSnapshot WithTurns(GameSnapshot frame, IReadOnlyList<TurnHistoryEntry> turns) => frame with { TurnHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, turns, true, 0, []) };
    private static EntitySnapshot Entity(int id, string card, string zone, int owner = 1) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone, ["CARDTYPE"] = card == "TLC_824" ? "SPELL" : "MINION", ["CARDRACE"] = "BEAST" }.ToFrozenDictionary());
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string value) => Replace(frame, Tag(frame.Entities.GetValueOrDefault(id) ?? Entity(id, "CORE_UNG_912", "PLAY"), key, value));
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot RemoveTag(GameSnapshot frame, int id, string key)
    { var entity = frame.Entities[id]; var tags = entity.Tags.ToDictionary(); tags.Remove(key); return Replace(frame, entity with { Tags = tags.ToFrozenDictionary() }); }
    private static EntitySnapshot RemoveTag(EntitySnapshot entity, string key)
    { var tags = entity.Tags.ToDictionary(); tags.Remove(key); return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string,CardRecord> Cards()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "map-followup-255790.json")));
        Check(document.RootElement.GetProperty("build").GetString() == "255790", "Map fixture build changed.");
        return document.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(),
            raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!, raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.GetProperty("cost").GetInt32(), raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Unknown(MapFollowupOutcome result) => Check(result.Stage == "Unconfirmed" && !result.ResolvesEffects && result.AddedToHand is null && result.HandCountAfter is null, "Unconfirmed identity/history acquired a direct card grant.");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
