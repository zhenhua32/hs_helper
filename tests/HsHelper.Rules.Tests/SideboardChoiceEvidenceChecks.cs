using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class SideboardChoiceEvidenceChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("JAIL actual chosen rows retain source groups counts and exact current offer independently", () =>
        {
            var result = Eval(Frame(), cards);
            Check(result is { ResolvesEvidence: true, IsHistoryComplete: true, IsCurrentSourceConfirmed: true } &&
                result.Sources.Select(source => source.SourceEntityId).SequenceEqual([30, 31]) &&
                result.Sources[0].Selections.Single() is { ChoiceId: 7, SelectedEntityId: 100, CardId: "EDR_489", OfferedAtEventSequence: 20, ChosenAtEventSequence: 30 } &&
                result.SelectedCounts.Sum(count => count.ObservedSelections) == 2 && result.SelectedCounts.All(count => !count.IsLowerBound) &&
                result.CurrentOffer is { ChoiceId: 9, SourceEntityId: 30, OptionCount: 3, IsExactOffer: true, HasCompleteCardIdentities: false, IsCompleteSideboardPool: false } &&
                !result.CurrentPoolIsExact && !result.CanEvaluateFollowupActions && !result.HasAuthoritativeNextState,
                "Actual selections, independent sources or pending offer facts became a guessed sideboard balance.");
        });
        yield return ("JAIL repeated choices count actual selected cards without resetting or merging copy pools", () =>
        {
            var frame = History(Frame(), [Entry(7, 30, "EDR_489", 20, 30), Entry(8, 31, "EDR_489", 40, 50), Entry(10, 30, "EDR_489", 60, 70)]);
            var result = Eval(frame, cards);
            Check(result.SelectedCounts.Single() is { CardId: "EDR_489", ObservedSelections: 3, IsLowerBound: false } &&
                result.Sources[0].SelectedCounts.Single().ObservedSelections == 2 && result.Sources[1].SelectedCounts.Single().ObservedSelections == 1 &&
                !result.CurrentPoolIsExact && result.Unknowns.Any(value => value.Contains("复制、偷取", StringComparison.Ordinal)),
                "Copy/replay records were reset, lost, or conflated with an exact shared pool.");
        });
        yield return ("JAIL incomplete truncated and warned ledgers retain only observed lower bounds", () =>
        {
            foreach (var frame in new[] { History(Frame(), Entries(), complete: false), History(Frame(), Entries(), truncated: 10),
                History(Frame(), Entries(), warnings: ["missing rows"]) })
            {
                var result = Eval(frame, cards);
                Check(result.ResolvesEvidence && !result.IsHistoryComplete && result.SelectedCounts.Sum(count => count.ObservedSelections) == 2 &&
                    result.SelectedCounts.All(count => count.IsLowerBound), "An incomplete choice ledger acquired exact lifetime counts.");
            }
        });
        yield return ("JAIL wrong-match state and sequence ledgers cannot be filled from the current offer", () =>
        {
            var frame = Frame();
            foreach (var stale in new[] { new ChoiceHistorySnapshot("other-match", frame.StateVersion, frame.EventSequence, Entries(), true, 0, []),
                new(frame.MatchId, frame.StateVersion + 1, frame.EventSequence, Entries(), true, 0, []),
                new(frame.MatchId, frame.StateVersion, frame.EventSequence + 1, Entries(), true, 0, []),
                new(frame.MatchId, frame.StateVersion, frame.EventSequence, Entries(), true, frame.EventSequence + 1, []) })
            {
                var result = Eval(frame with { ChoiceHistory = stale }, cards);
                Check(result.SelectedCounts.Count == 0 && result.Sources.Count == 0 && !result.IsHistoryComplete && result.CurrentOffer?.OptionCount == 3,
                    "Current offers were used as unobserved selections or a cross-frame ledger was accepted.");
            }
            Check(Eval(frame with { ChoiceHistory = null }, cards).SelectedCounts.Count == 0, "Missing history was fabricated from candidates.");
        });
        yield return ("JAIL future chosen identities never appear in old sideboard evidence", () =>
        {
            var frame = History(Frame(), [Entry(7, 30, "EDR_489", 20, 30), Entry(8, 31, "FUTURE_SIDEBOARD_PROBE", 190, 210)]);
            var result = Eval(frame, cards);
            Check(result.SelectedCounts.Single() is { CardId: "EDR_489", ObservedSelections: 1, IsLowerBound: true } &&
                !JsonSerializer.Serialize(result).Contains("FUTURE_SIDEBOARD_PROBE", StringComparison.Ordinal), "Future chosen-card identity entered an earlier observation.");
        });
        yield return ("JAIL duplicate history choice IDs are never counted twice or resolved by picking a row", () =>
        {
            var result = Eval(History(Frame(), [Entry(7, 30, "DUPLICATE_PROBE", 20, 30), Entry(7, 31, "OTHER_DUPLICATE_PROBE", 20, 30), Entry(8, 31, "DINO_421", 40, 50)]), cards);
            Check(!result.IsHistoryComplete && result.SelectedCounts.Single().CardId == "DINO_421" &&
                !JsonSerializer.Serialize(result).Contains("DUPLICATE_PROBE", StringComparison.Ordinal), "A conflicting repeated choice ID became two or one arbitrarily selected fact.");
        });
        yield return ("JAIL chosen headers missing rows wrong IDs and incomplete entries do not prove a selection", () =>
        {
            var valid = Entry(7, 30, "UNPROVEN_CHOSEN_PROBE", 20, 30);
            foreach (var bad in new[] { Rebuild(valid, chosen: []), Rebuild(valid, chosen: [999]), Rebuild(valid, complete: false),
                Rebuild(valid, sourceCard: null), Rebuild(valid, chosenAt: 20), Rebuild(valid, sourceId: null), Rebuild(valid, sourceId: 1),
                Rebuild(valid, options: [new(1, "UNPROVEN_CHOSEN_PROBE", 1, 0)], chosen: [1]) })
            {
                var result = Eval(History(Frame(), [bad]), cards);
                Check(!result.IsHistoryComplete && result.SelectedCounts.Count == 0 &&
                    !JsonSerializer.Serialize(result).Contains("UNPROVEN_CHOSEN_PROBE", StringComparison.Ordinal), "A header or inconsistent chosen descriptor became an actual selected card.");
            }
        });
        yield return ("JAIL history ignores foreign players foreign option owners and other source identities", () =>
        {
            var foreign = Entry(7, 30, "OPPONENT_SIDEBOARD_PROBE", 20, 30, player: 2);
            var optionForeign = Rebuild(Entry(8, 31, "OPPONENT_OPTION_PROBE", 40, 50), options: [new(100, "OPPONENT_OPTION_PROBE", 2, 0)]);
            var unrelated = Rebuild(Entry(10, 30, "UNRELATED_CHOICE_PROBE", 60, 70), sourceCard: "EDR_888");
            var result = Eval(History(Frame(), [foreign, optionForeign, unrelated]), cards);
            Check(result.SelectedCounts.Count == 0 && !JsonSerializer.Serialize(result).Contains("_PROBE", StringComparison.Ordinal),
                "Unconfirmed opponent or unrelated discovery identities were projected as own JAIL selections.");
        });
        yield return ("JAIL pending current offers never become selected history", () =>
        {
            var frame = History(Frame(), []); var result = Eval(frame, cards);
            Check(result.IsHistoryComplete && result.SelectedCounts.Count == 0 && result.CurrentOffer?.OptionCount == 3,
                "Pending candidates were interpreted as cards already selected.");
            var entities = frame.Entities.ToDictionary();
            entities[82] = new(82, "UNBOUND_CANDIDATE_PROBE", EntityKind.Card, new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "SETASIDE" }.ToFrozenDictionary());
            var unknown = Eval(frame with { Entities = entities.ToFrozenDictionary() }, cards);
            Check(unknown.CurrentOffer!.Options.Single(option => option.EntityId == 82).CardId is null &&
                !JsonSerializer.Serialize(unknown).Contains("UNBOUND_CANDIDATE_PROBE", StringComparison.Ordinal), "A missing offered identity was backfilled from another observation.");
        });
        yield return ("JAIL wrong stale unready and differently sourced current choices do not acquire offer evidence", () =>
        {
            var frame = Frame();
            foreach (var choice in new[] { frame.Choice! with { IsReady = false }, frame.Choice! with { PlayerId = 2 },
                frame.Choice! with { SourceEntityId = 31 }, frame.Choice! with { SourceCardId = null }, frame.Choice! with { SourceCardId = "EDR_888" },
                frame.Choice! with { Type = "MULLIGAN" }, frame.Choice! with { CountMin = 0 }, frame.Choice! with { CountMax = 2 },
                frame.Choice! with { OfferedAtEventSequence = 0 }, frame.Choice! with { OfferedAtEventSequence = 25 }, frame.Choice! with { OfferedAtEventSequence = 201 }, frame.Choice! with { Id = 7 } })
                Check(Eval(frame with { Choice = choice }, cards).CurrentOffer is null, "Stale or conflicting current choice became a native JAIL offer.");
            Check(Eval(frame with { Phase = GamePhase.Main }, cards).CurrentOffer is null, "A residual Main-phase choice exposed its offers.");
            var handSource = frame.Entities.ToDictionary();
            handSource[30] = handSource[30] with { Tags = handSource[30].Tags.Select(pair => pair.Key == "ZONE" ? new KeyValuePair<string, string>("ZONE", "HAND") : pair).ToFrozenDictionary() };
            Check(Eval(frame with { Entities = handSource.ToFrozenDictionary() }, cards).CurrentOffer is null,
                "An unplayed hand source became an already triggered sideboard offer.");
        });
        yield return ("JAIL duplicate or incomplete current history cannot reopen a consumed Ready offer", () =>
        {
            var frame = Frame();
            var current = new ChoiceHistoryEntry(9, 1, "GENERAL", 30, "JAIL_831", 1, 1, 100, 150, frame.Choice!.Options, [], [81], true);
            foreach (var entries in new[] { Entries().Concat(new[] { current, current }).ToArray(),
                Entries().Append(new ChoiceHistoryEntry(9, 1, "GENERAL", 30, "JAIL_831", 1, 1, 100, 150, frame.Choice.Options, [], [], false)).ToArray() })
            {
                var result = Eval(History(frame, entries, complete: false), cards);
                Check(result.CurrentChoiceHasHistory && result.CurrentOffer is null && !result.IsHistoryComplete &&
                    result.Sources.SelectMany(source => source.Selections).All(selection => selection.ChoiceId != 9),
                    "Dropping contradictory chosen rows from precise facts recreated a fresh current offer.");
            }
        });
        yield return ("JAIL historical source proof survives later transformation without claiming current native text", () =>
        {
            var frame = Frame(); var entities = frame.Entities.ToDictionary(); entities[30] = entities[30] with { CardId = "EDR_489" };
            var result = Eval(frame with { Entities = entities.ToFrozenDictionary() }, cards);
            Check(result.ResolvesEvidence && !result.IsCurrentSourceConfirmed && result.CurrentOffer is null && result.Sources[0].Selections.Single().CardId == "EDR_489",
                "Later source identity rewrote or erased a valid past selection or supplied current native source proof.");
        });
        yield return ("JAIL current source aliases custom text and unbound hidden sources fail closed", () =>
        {
            foreach (var (key, value) in new[] { ("CARDTYPE", ""), ("202", "SPELL"), ("50", "2"), ("49", "3"), ("4573", "1"), ("ZONE", "DECK") })
            {
                var frame = History(Frame(), []); var entities = frame.Entities.ToDictionary(); var tags = entities[30].Tags.ToDictionary(); tags[key] = value;
                entities[30] = entities[30] with { Tags = tags.ToFrozenDictionary() };
                var result = Eval(frame with { Entities = entities.ToFrozenDictionary() }, cards);
                Check(!result.ResolvesEvidence && result.CurrentOffer is null && result.SourceCardId is null,
                    "A hidden, custom or explicitly conflicting source acquired native offer proof.");
            }
        });
        yield return ("JAIL spectator unstable unknown build and malformed entity maps expose no private evidence or deck reference", () =>
        {
            var frame = Frame();
            foreach (var hidden in new[] { frame with { IsSpectating = true }, frame with { ClientBuild = null }, frame with { ClientBuild = "255791" },
                frame with { Quality = frame.Quality with { HasLocalPerspective = false } }, frame with { Quality = frame.Quality with { IsComplete = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { HasGameStart = false } },
                frame with { Quality = frame.Quality with { OpenBlockCount = 1 } }, frame with { Phase = GamePhase.Mulligan } })
            {
                var result = Eval(hidden, cards, Deck(cards));
                Check(!result.ResolvesEvidence && result.SourceCardId is null && result.Sources.Count == 0 && result.SelectedCounts.Count == 0 &&
                    result.CurrentOffer is null && result.InitialReference is null, "Rejected scope disclosed private historical choices or initial deck contents.");
            }
        });
        yield return ("JAIL starting sideboard is a three-card reference without subtracting actual selections or filling unknown DBFs", () =>
        {
            var result = Eval(Frame(), cards, Deck(cards)); var initial = result.InitialReference!;
            Check(initial is { OwnerDbfId: 125998, IsValid: true, TotalCards: 3, IsStartingReferenceOnly: true, IsCurrentPool: false } &&
                initial.Cards.Count == 3 && initial.Cards.Single(card => card.DbfId == 999999).CardId is null &&
                result.SelectedCounts.Sum(count => count.ObservedSelections) == 2 && !result.CurrentPoolIsExact,
                "An initial deckstring became remaining pool counts or unknown initial cards were inferred from offers.");
        });
        yield return ("JAIL malformed missing and duplicate initial sideboards never become an empty current pool", () =>
        {
            var valid = Deck(cards);
            foreach (var deck in new[] { valid with { Sideboards = [] }, valid with { Cards = [] }, valid with { Sideboards = [valid.Sideboards[0], valid.Sideboards[0]] },
                valid with { Sideboards = [new(125998, [new(1, 1), new(1, 2)])] }, valid with { Sideboards = [new(125998, [new(1, 2)])] },
                valid with { Sideboards = [new(125998, [new(1, int.MaxValue)])] } })
                Check(Eval(Frame(), cards, deck).InitialReference is { IsValid: false, TotalCards: null, Cards.Count: 0, IsCurrentPool: false },
                    "Malformed initial references were repaired or interpreted as an observed empty pool.");
        });
        yield return ("JAIL evidence freezes pending candidates deck references and all returned lists", () =>
        {
            var frame = Frame(); var options = frame.Choice!.Options.ToList(); frame = frame with { Choice = frame.Choice with { Options = options } };
            var initialCards = Deck(cards).Sideboards[0].Cards.ToList(); var sideboards = new List<DeckSideboard> { new(125998, initialCards) };
            var deck = Deck(cards) with { Sideboards = sideboards }; var before = JsonSerializer.Serialize(frame);
            var result = Eval(frame, cards, deck);
            Check(before == JsonSerializer.Serialize(frame), "The resolver changed its authoritative input.");
            options.Clear(); initialCards.Clear(); sideboards.Clear();
            Check(result.CurrentOffer!.OptionCount == 3 && result.InitialReference!.TotalCards == 3 && result.InitialReference.Cards.Count == 3 &&
                result.Sources[0].Selections.Count == 1, "Producer mutation changed already returned evidence.");
            foreach (var write in new Action[] { () => ((IList<SideboardSelectedCount>)result.SelectedCounts).Clear(),
                () => ((IList<SideboardChoiceSelection>)result.Sources[0].Selections).Clear(),
                () => ((IList<ChoiceOptionSnapshot>)result.CurrentOffer.Options).Clear(),
                () => ((IList<SideboardInitialCardReference>)result.InitialReference!.Cards).Clear() })
            { try { write(); throw new InvalidOperationException("Mutable evidence list."); } catch (NotSupportedException) { } }
        });
        yield return ("JAIL native identity audit rejects mismatched card data before private history resolution", () =>
        {
            foreach (var card in new[] { cards["JAIL_831"] with { DbfId = 1 }, cards["JAIL_831"] with { Type = "SPELL" },
                cards["JAIL_831"] with { Text = "other" }, cards["JAIL_831"] with { RawJson = "{}" } })
            {
                var altered = cards.ToDictionary(); altered["JAIL_831"] = card;
                Check(!Eval(Frame(), altered).ResolvesEvidence && Eval(Frame(), altered).SelectedCounts.Count == 0,
                    "Mismatched fixed data acquired private sideboard evidence.");
            }
        });
    }

    private static SideboardChoiceEvidenceOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, DeckDefinition? deck = null) =>
        SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, deck);
    private static GameSnapshot Frame()
    {
        var entities = new[] { Player(1), Player(2), Source(30), Source(31) };
        var frame = new GameSnapshot("sideboard-evidence", 10, 200, 5, 1, 1, GamePhase.Choice,
            entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        { ClientBuild = "255790", Choice = new(9, 1, "GENERAL", 30, 1, 1, [new(80, "EDR_489", 1, 0), new(81, "DINO_421", 1, 1), new(82, null, 1, 2)], true)
            { SourceCardId = "JAIL_831", OfferedAtEventSequence = 100, PreviousOfferedAtEventSequence = 40 } };
        return History(frame, Entries());
    }
    private static ChoiceHistoryEntry[] Entries() => [Entry(7, 30, "EDR_489", 20, 30), Entry(8, 31, "DINO_421", 40, 50)];
    private static ChoiceHistoryEntry Entry(int id, int source, string card, long offered, long chosen, int player = 1) =>
        new(id, player, "GENERAL", source, "JAIL_831", 1, 1, offered, chosen, [new(100, card, player, 0)], [], [100], true);
    private static ChoiceHistoryEntry Rebuild(ChoiceHistoryEntry entry, int[]? chosen = null, bool complete = true, string? sourceCard = "JAIL_831",
        long? chosenAt = null, int? sourceId = 30, ChoiceOptionSnapshot[]? options = null) =>
        new(entry.ChoiceId, entry.PlayerId, entry.Type, sourceId, sourceCard, entry.CountMin, entry.CountMax, entry.OfferedAtEventSequence,
            chosenAt ?? entry.ChosenAtEventSequence, options ?? entry.Options, [], chosen ?? entry.ChosenEntityIds, complete);
    private static GameSnapshot History(GameSnapshot frame, IReadOnlyList<ChoiceHistoryEntry> entries, bool complete = true, long truncated = 0, IReadOnlyList<string>? warnings = null) =>
        frame with { ChoiceHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, entries, complete, truncated, warnings ?? []) };
    private static EntitySnapshot Player(int player) => new(player, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString() }.ToFrozenDictionary());
    private static EntitySnapshot Source(int id) => new(id, "JAIL_831", EntityKind.Card, new Dictionary<string, string>
        { ["CONTROLLER"] = "1", ["ZONE"] = "PLAY", ["CARDTYPE"] = "MINION" }.ToFrozenDictionary());
    private static DeckDefinition Deck(IReadOnlyDictionary<string, CardRecord> cards) => new(2, [31], [new(125998, 1)],
        [new(125998, [new(cards["EDR_489"].DbfId!.Value, 1), new(cards["DINO_421"].DbfId!.Value, 1), new(999999, 1)])]);
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "beast-hunter-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Unpinned sideboard fixture.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(raw => raw.GetProperty("id").GetString()!, raw =>
            new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
                raw.TryGetProperty("text", out var text) ? text.GetString()! : "", raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText()), StringComparer.Ordinal);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
