using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class ChoiceResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        yield return ("tracking resolves only the selected card and preserves the other deck candidates", () => Tracking(cards));
        yield return ("tracking still removes one selected card when the observed hand is full", () => FullHand(cards));
        yield return ("sideboard discovery reports granted discount without subtracting an already observed candidate cost", () => Sideboard(cards));
        yield return ("sideboard effects require a current offer with proven timing rather than historical facts", () => SideboardOfferBinding(cards));
        yield return ("sideboard effects reject bound current type contradictions without borrowing unidentified traits", () => SideboardCandidateTypes(cards));
        yield return ("odd map resolves its offered card but does not invent a second choice", () => Map(cards));
        yield return ("choice effects distinguish legal range from unknown source version and mechanisms", () => UnsupportedSources(cards));
        yield return ("choice effects reject source zone ownership and selected entity conflicts", () => SourceAndCandidate(cards));
        yield return ("choice effects never reuse future or mismatched deck compositions", () => DeckBindings(cards));
        yield return ("tracking refuses contradictory empty or exact deck evidence", () => DeckConflicts(cards));
        yield return ("choice effects discard malformed deck quantities and reject a missing option list", () => MalformedEvidence(cards));
        yield return ("choice effects preserve unknown hand limits and conflicting counts", () => UnknownHand(cards));
        yield return ("choice effect projections freeze selected identities composition and lists", () => Immutable(cards));
        yield return ("choice effect evaluation honors cancellation before reading consequences", () => Canceled(cards));
    }

    private static ChoiceResolutionEvaluation Evaluate(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards,
        IReadOnlyList<int>? selected = null, int? expected = 7) => new LineEvaluator().EvaluateChoice(frame, selected ?? [40], cards, expected);

    private static void Tracking(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var before = JsonSerializer.Serialize(frame);
        var result = Evaluate(frame, cards);
        Check(result.Verification == LineVerification.Conditional && result.IsLegal == true && !result.SelectionOnly &&
            result.ResolvesEffects && !result.CanEvaluateFollowupActions, "Range/effect/followup claims were not separated.");
        Check(result.SourceEntityId == 30 && result.SourceCardId == "CORE_DS1_184" && result.SourceName == "追踪术",
            "Choice effect lost its observed source and fixed-build Chinese name.");
        var card = result.CardOutcomes.Single();
        Check(card.OptionEntityId == 40 && card.CardId == "EDR_227" && card.Name == "幽爪熊" && card.Origin == "Deck" &&
            card.AddedToHand == true && card.Destination == "Hand" && card.ResultingCost is null && card.GrantedCostReduction is null,
            "Tracking invented a cost, hand entity or wrong selected identity.");
        var deck = result.DirectDeckChange!;
        Check(deck.RemovedCardId == "EDR_227" && deck.RemovedCopies == 1 && deck.TotalCardsBefore == 4 && deck.TotalCardsAfter == 3 &&
            deck.CompositionIsExact && deck.KnownRemainingAfter["EDR_227"] == 1 && deck.KnownRemainingAfter["EDR_480"] == 1 &&
            deck.KnownRemainingAfter["KEEP"] == 1, "Tracking discarded the other options or lost exact direct composition.");
        Check(result.HandCountBefore == 0 && result.HandCountAfter == 1 && result.HandLimit == 10 && before == JsonSerializer.Serialize(frame),
            "Tracking changed its input or failed to project observed hand capacity.");
    }

    private static void FullHand(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = WithHand(Frame(), 10);
        var result = Evaluate(frame, cards);
        var card = result.CardOutcomes.Single();
        Check(card.AddedToHand == false && card.Destination == "NotAddedHandFull" && result.HandCountAfter == 10,
            "A full hand gained an invented eleventh card.");
        Check(result.DirectDeckChange is { TotalCardsAfter: 3 } && result.DirectDeckChange.KnownRemainingAfter["EDR_480"] == 1 &&
            result.Assumptions.Any(text => text.Contains("不移除其他候选", StringComparison.Ordinal)),
            "Full-hand tracking failed to remove the selected card or burned other options.");
    }

    private static void Sideboard(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame("JAIL_831", "PLAY");
        frame = frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 90,
            Options = [new(40, "EDR_227", 1, 0), new(41, "EDR_480", 1, 1)] } };
        var entities = frame.Entities.ToDictionary();
        entities[40] = Entity(40, "EDR_227", "SETASIDE", 1, ("COST", 0));
        var result = Evaluate(frame with { Entities = entities.ToFrozenDictionary() }, cards);
        var card = result.CardOutcomes.Single();
        Check(result.SourceName == "下水道之王" && card.Origin == "Sideboard" && card.GrantedCostReduction == 3 &&
            card.ResultingCost is null && card.AddedToHand == true && result.DirectDeckChange is null,
            "Sideboard discovery used printed/candidate cost as final, double-discounted, or removed a deck card.");
        Check(result.SideboardEvidence is { ResolvesEvidence: true, IsHistoryComplete: false, CurrentPoolIsExact: false,
                CurrentOffer.OptionCount: 2, InitialReference: null } &&
            result.Unknowns.Any(text => text.Contains("完整剩余池未知", StringComparison.Ordinal)),
            "Actual current offers and missing historical evidence were confused with a simulated complete sideboard pool.");
    }

    private static void SideboardOfferBinding(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame("JAIL_831", "PLAY");
        frame = frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 90 } };
        var entry = new ChoiceHistoryEntry(3, 1, "GENERAL", 30, "JAIL_831", 1, 1, 30, 50,
            [new(99, "EDR_227", 1, 0)], [], [99], true);
        foreach (var bad in new[]
        {
            frame with { Choice = frame.Choice with { OfferedAtEventSequence = 0 } },
            frame with { Choice = frame.Choice with { OfferedAtEventSequence = 101 } },
            frame with { Choice = frame.Choice with { OfferedAtEventSequence = 20 },
                ChoiceHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, [entry], true, 0, []) }
        })
        {
            var result = Evaluate(bad, cards);
            Check(result.SideboardEvidence is { IsCurrentSourceConfirmed: true, CurrentOffer: null } &&
                !result.ResolvesEffects && result.CardOutcomes.Count == 0 && result.HandCountAfter is null &&
                result.Verification == LineVerification.Unsupported, "An unbound or stale current offer still granted a sideboard card and reduction.");
        }
        var valid = Evaluate(frame with { ChoiceHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, [entry], true, 0, []) }, cards);
        Check(valid.SideboardEvidence?.CurrentOffer?.ChoiceId == frame.Choice.Id && valid.ResolvesEffects,
            "A correctly later fresh offer could not resolve independently of past selections.");
    }

    private static void SideboardCandidateTypes(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame("JAIL_831", "PLAY"); frame = frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 90 } };
        foreach (var key in new[] { "CARDTYPE", "202" })
        foreach (var value in new[] { "SPELL", "5", "", "bad" })
        {
            var candidate = Entity(40, "EDR_227", "SETASIDE", 1); var tags = candidate.Tags.ToDictionary(); tags[key] = value;
            var entities = frame.Entities.ToDictionary(); entities[40] = candidate with { Tags = tags.ToFrozenDictionary() };
            var result = Evaluate(frame with { Entities = entities.ToFrozenDictionary() }, cards);
            Check(result.SideboardEvidence?.CurrentOffer is not null && !result.ResolvesEffects && result.CardOutcomes.Count == 0 &&
                result.HandCountAfter is null, "A bound observed non-minion/invalid type was repaired from printed metadata before granting the native card.");
        }
        foreach (var known in new[] { true, false })
        {
            var candidate = Entity(40, known ? "EDR_227" : null, "SETASIDE", 1);
            var tags = candidate.Tags.ToDictionary(); tags["CARDTYPE"] = known ? "MINION" : "SPELL"; tags["202"] = known ? "4" : "5";
            var entities = frame.Entities.ToDictionary(); entities[40] = candidate with { Tags = tags.ToFrozenDictionary() };
            Check(Evaluate(frame with { Entities = entities.ToFrozenDictionary() }, cards).ResolvesEffects,
                "Valid equivalent aliases or explicit offered identity without bound residual traits were rejected.");
        }
        var noTraits = frame.Entities.ToDictionary(); noTraits[40] = Entity(40, "EDR_227", "SETASIDE", 1);
        Check(Evaluate(frame with { Entities = noTraits.ToFrozenDictionary() }, cards).ResolvesEffects && !noTraits[40].Tags.ContainsKey("CARDTYPE"),
            "Missing current type was invented or written into the source entity.");
    }

    private static void Map(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame("TLC_824");
        frame = frame with { Choice = frame.Choice! with { Options = [new(40, "EDR_227", 1, 0), new(41, "EDR_480", 1, 1), new(42, "CORE_UNG_912", 1, 2)], OfferedAtEventSequence = 90 } };
        frame = Replace(frame, Entity(42, null, "SETASIDE", 1));
        var result = Evaluate(frame, cards);
        Check(result.CardOutcomes.Single() is { Origin: "DiscoverPool", AddedToHand: true, ResultingCost: null } &&
            result.DirectDeckChange is null && result.Map is { Stage: "InitialChoice", InitialSelectedPlayedAtEventSequence: null } &&
            !result.CanEvaluateFollowupActions, "Map created an unobserved second selection or a next playable hand state.");
    }

    private static void UnsupportedSources(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        foreach (var unsupported in new[]
        {
            frame with { ClientBuild = "900001" },
            frame with { Choice = frame.Choice! with { SourceEntityId = null } },
            frame with { Choice = frame.Choice! with { SourceCardId = null } },
            frame with { Choice = frame.Choice! with { SourceCardId = "JAIL_831" } },
            frame with { Choice = frame.Choice! with { Type = "DREDGE" } },
            Frame("VANILLA", "PLAY"),
            frame with { Phase = GamePhase.Mulligan }
        })
        {
            var result = Evaluate(unsupported, cards);
            Check(result.Verification == LineVerification.Unsupported && result.IsLegal == true &&
                result.SelectionOnly && !result.ResolvesEffects && result.CardOutcomes.Count == 0,
                "An unknown direct effect lost valid selection range or claimed it resolved effects.");
        }
        var invalid = Evaluate(frame, cards, [999]);
        Check(invalid.Verification == LineVerification.Invalid && invalid.IsLegal == false && !invalid.ResolvesEffects,
            "An out-of-range option was resolved.");
        var notReady = Evaluate(frame with { Choice = frame.Choice! with { IsReady = false } }, cards);
        Check(notReady.IsLegal is null && !notReady.ResolvesEffects, "Incomplete selection evidence was called illegal or resolved.");
    }

    private static void SourceAndCandidate(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        foreach (var source in new[] { Entity(30, "CORE_DS1_184", "HAND", 1), Entity(30, "CORE_DS1_184", "DECK", 1),
            Entity(30, "CORE_DS1_184", "GRAVEYARD", 2) })
        {
            var result = Evaluate(Replace(frame, source), cards);
            Check(!result.ResolvesEffects && result.SourceCardId is null && result.SourceName is null,
                "An unplayed or opposing source exposed its identity or supplied an effect.");
        }
        foreach (var candidate in new[] { Entity(40, "PRIVATE_CONFLICT", "SETASIDE", 1), Entity(40, null, "SETASIDE", 2),
            Entity(40, "EDR_227", "HAND", 1) })
            Check(!Evaluate(Replace(frame, candidate), cards).ResolvesEffects, "Conflicting identity/owner or an existing hand card was duplicated.");
        var unknown = Evaluate(frame with { Choice = frame.Choice! with { Options = [new(40, null, 1, 1), new(41, "EDR_480", 1, 2)] } }, cards);
        Check(!unknown.ResolvesEffects, "An unknown option acquired an invented card identity.");
    }

    private static void DeckBindings(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        foreach (var tracking in new[] { frame.DeckTracking! with { MatchId = "future-match" }, frame.DeckTracking! with { StateVersion = 999 },
            frame.DeckTracking! with { CardBuild = "900001" } })
        {
            var result = Evaluate(frame with { DeckTracking = tracking with { KnownRemaining = new Dictionary<string, int> { ["FUTURE_PRIVATE"] = 40 } } }, cards);
            Check(result.DirectDeckChange is { CompositionIsExact: false, TotalCardsBefore: 4, TotalCardsAfter: 3 } &&
                result.DirectDeckChange.KnownRemainingAfter.Count == 0 && !JsonSerializer.Serialize(result).Contains("FUTURE_PRIVATE", StringComparison.Ordinal),
                "A cross-state ledger filled current deck counts or leaked its card identities.");
        }
    }

    private static void DeckConflicts(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        foreach (var conflict in new[]
        {
            frame with { DeckTracking = frame.DeckTracking! with { TotalCards = 0, KnownRemaining = FrozenDictionary<string, int>.Empty } },
            frame with { DeckTracking = frame.DeckTracking! with { KnownRemaining = new Dictionary<string, int> { ["EDR_227"] = 4 } } }
        })
        {
            var result = Evaluate(conflict, cards);
            Check(result.Verification == LineVerification.Unsupported && !result.ResolvesEffects && result.DirectDeckChange is null && result.HandCountAfter is null,
                "Contradictory exact composition or an empty deck produced a fake post-choice composition.");
        }
    }

    private static void UnknownHand(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        var unknownLimit = Replace(frame, Player(1, 1, hand: 0, limit: null));
        var result = Evaluate(unknownLimit, cards);
        Check(result.HandLimit is null && result.HandCountAfter is null && result.CardOutcomes.Single().AddedToHand is null,
            "A missing observed hand limit defaulted to ten or guaranteed card gain.");
        var conflict = Evaluate(Replace(frame, Player(1, 1, hand: 9)), cards);
        Check(conflict.HandCountBefore is null && conflict.HandCountAfter is null && conflict.CardOutcomes.Single().AddedToHand is null,
            "Conflicting observed and entity hand counts became an exact result.");
    }

    private static void MalformedEvidence(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame();
        foreach (var tracking in new[]
        {
            frame.DeckTracking! with { UnknownCount = -1 },
            frame.DeckTracking! with { TotalCards = -1 },
            frame.DeckTracking! with { KnownRemaining = new Dictionary<string, int> { ["EDR_227"] = -2 } },
            frame.DeckTracking! with { KnownRemaining = new Dictionary<string, int> { ["EDR_227"] = 99 } }
        })
        {
            var result = Evaluate(frame with { DeckTracking = tracking }, cards);
            Check(result.DirectDeckChange is { CompositionIsExact: false, TotalCardsBefore: 4, TotalCardsAfter: 3 } &&
                result.DirectDeckChange.KnownRemainingAfter.Count == 0,
                "Malformed quantities were reused as a current post-choice composition.");
        }
        var missing = Evaluate(frame with { Choice = frame.Choice! with { Options = null! } }, cards);
        Check(missing.IsLegal is null && !missing.ResolvesEffects, "A missing option list crashed or became a resolved choice.");
        var malformed = frame.Entities[40] with { Tags = null! };
        Check(!Evaluate(Replace(frame, malformed), cards).ResolvesEffects,
            "A malformed entity reached consequence evaluation through the common choice guard.");
        var nullOption = frame with { Choice = frame.Choice! with { Options = [null!] } };
        Check(!Evaluate(nullOption, cards).ResolvesEffects &&
            new LineEvaluator().ValidateChoice(nullOption, [40], 7).Verification == LineVerification.Unsupported,
            "A malformed candidate bypassed validation or crashed before the special rule guard.");
    }

    private static void Immutable(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var selected = new List<int> { 40 };
        var options = frame.Choice!.Options.ToList(); var counts = frame.DeckTracking!.KnownRemaining.ToDictionary();
        frame = frame with { Choice = frame.Choice with { Options = options }, DeckTracking = frame.DeckTracking with { KnownRemaining = counts } };
        var result = Evaluate(frame, cards, selected); selected.Clear(); options.Clear(); counts["EDR_227"] = 999;
        Check(result.SelectedEntityIds.SequenceEqual([40]) && result.CardOutcomes.Count == 1 && result.DirectDeckChange!.KnownRemainingAfter["EDR_227"] == 1,
            "Later caller changes mutated an earlier choice projection.");
        try { ((IList<ChoiceCardOutcome>)result.CardOutcomes).Clear(); throw new Exception("Consumer changed outcome list."); }
        catch (NotSupportedException) { }
    }

    private static void Canceled(IReadOnlyDictionary<string, CardRecord> cards)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { new LineEvaluator().EvaluateChoice(Frame(), [40], cards, cancellationToken: cancellation.Token); throw new Exception("Canceled evaluation ran."); }
        catch (OperationCanceledException) { }
    }

    private static GameSnapshot Frame(string source = "CORE_DS1_184", string zone = "GRAVEYARD")
    {
        var frame = new GameSnapshot("discover-fixture", 10, 100, 5, 1, 1, GamePhase.Choice,
            new[] { Player(1, 1), Player(2, 2), Entity(30, source, zone, 1), Entity(40, null, "SETASIDE", 1), Entity(41, null, "SETASIDE", 1) }
                .ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        {
            ClientBuild = LineEvaluator.AuditedBuild,
            Choice = new(7, 1, "GENERAL", 30, 1, 1, [new(40, "EDR_227", 1, 1), new(41, "EDR_480", 1, 2)], true) { SourceCardId = source }
        };
        return frame with { DeckTracking = new(frame.MatchId, frame.StateVersion, "deck-a", LineEvaluator.AuditedBuild, 4,
            new Dictionary<string, int> { ["EDR_227"] = 2, ["EDR_480"] = 1, ["KEEP"] = 1 }, 0, true, []) };
    }
    private static EntitySnapshot Player(int id, int player, int hand = 0, int? limit = 10)
    {
        var tags = new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString(), ["NUM_CARDS_IN_HAND"] = hand.ToString(), ["NUM_CARDS_IN_DECK"] = "4" };
        if (limit.HasValue) tags["MAXHANDSIZE"] = limit.Value.ToString();
        return new(id, null, EntityKind.Player, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Entity(int id, string? card, string zone, int owner, params (string Key, int Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone };
        foreach (var (key, value) in extra) tags[key] = value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot WithHand(GameSnapshot frame, int count)
    {
        frame = Replace(frame, Player(1, 1, count));
        foreach (var index in Enumerable.Range(0, count)) frame = Replace(frame, Entity(100 + index, "HAND_" + index, "HAND", 1));
        return frame;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
