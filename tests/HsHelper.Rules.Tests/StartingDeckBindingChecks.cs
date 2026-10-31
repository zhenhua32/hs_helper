using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class StartingDeckBindingChecks
{
    private const string Build = "255790";
    private const string DeckId = "original-deck-version";

    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        cards = WithFixedTerror(cards);
        yield return ("starting deck binding owns every definition list without changing source data", () => DeepFreeze(cards));
        yield return ("starting deck references require the exact frame build and anonymous deck version", () => Scope(cards));
        yield return ("matching deck tracking cannot contradict the original deck or build", () => Tracking(cards));
        yield return ("malformed starting definitions are rejected without echoing identifiers or cards", () => Malformed(cards));
        yield return ("choice effects carry session-bound starting references separately from offers and actual choices", () => Choice(cards));
        yield return ("a rejected binding never falls back to a raw deck or leaks its contents", () => RawConflict(cards));
        yield return ("raw starting references retain their unverified-source compatibility semantics", () => RawCompatibility(cards));
        yield return ("missing fixed definitions remain unknown even when the current offer names the card", () => MissingDefinition(cards));
        yield return ("session references remain private and do not promote transformed sources into native effects", () => PrivacyAndHistory(cards));
        yield return ("starting reference validation preserves choice cancellation and old positional calls", () => ExistingContract(cards));
    }

    private static void DeepFreeze(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var heroes = new List<int> { 31 }; var main = new List<DeckCard> { new(125998, 1) };
        var side = Deck(cards).Sideboards[0].Cards.ToList(); var boards = new List<DeckSideboard> { new(125998, side) };
        var source = Bind(Frame(), new(2, heroes, main, boards));
        var before = JsonSerializer.Serialize(source);
        var frozen = StartingDeckBinding.FreezeForSnapshot(Frame(), Build, source)!;
        Check(frozen is not null && before == JsonSerializer.Serialize(source) && frozen.Definition != source.Definition,
            "The binding was rejected, retained its definition, or modified its caller.");
        heroes.Clear(); main.Clear(); side.Clear(); boards.Clear();
        Check(frozen!.Definition.Heroes.SequenceEqual([31]) && frozen.Definition.Cards.Single().DbfId == 125998 &&
            frozen.Definition.Sideboards.Single().Cards.Count == 3, "Producer mutation changed the frozen original deck.");
        ReadOnly(() => ((IList<int>)frozen.Definition.Heroes).Clear());
        ReadOnly(() => ((IList<DeckCard>)frozen.Definition.Cards).Clear());
        ReadOnly(() => ((IList<DeckSideboard>)frozen.Definition.Sideboards).Clear());
        ReadOnly(() => ((IList<DeckCard>)frozen.Definition.Sideboards[0].Cards).Clear());
    }

    private static void Scope(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var good = Bind(frame, Deck(cards));
        Check(StartingDeckBinding.FreezeForSnapshot(frame, Build, good) is not null &&
            StartingDeckBinding.FreezeForSnapshot(frame with { ClientBuild = null }, Build, good) is not null,
            "A host-fixed build could not bind an otherwise exact frame lacking an observed build.");
        foreach (var bad in new[] { good with { MatchId = "foreign-match" }, good with { StateVersion = frame.StateVersion + 1 },
            good with { EventSequence = frame.EventSequence + 1 }, good with { CardBuild = "255791" } }) Rejected(frame, bad, cards);
        foreach (var id in new[] { "", " ", "_leading", "https://private.invalid/deck", "PRIVATE\nDECK", new string('x', 201), "私有套牌" })
            Rejected(frame, good with { DeckVersionId = id }, cards);
        foreach (var build in new string?[] { null, "", "unknown", "latest", "255790\n", "255791" })
            Check(StartingDeckBinding.FreezeForSnapshot(frame, build, good) is null, "An unknown or conflicting fixed build became a binding.");
        Check(StartingDeckBinding.FreezeForSnapshot(frame with { ClientBuild = "255791" }, Build, good) is null &&
            StartingDeckBinding.FreezeForSnapshot(frame with { ClientBuild = "unknown" }, Build, good) is null,
            "An explicit observed build contradiction was repaired by a host reference.");
        Check(StartingDeckBinding.FreezeForSnapshot(frame, Build, good with { DeckVersionId = new string('a', 200) }) is not null,
            "The inclusive anonymous identifier boundary changed.");
    }

    private static void Tracking(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var binding = Bind(frame, Deck(cards));
        var tracking = new DeckTrackingSnapshot(frame.MatchId, frame.StateVersion, DeckId, Build, null, new Dictionary<string, int>(), 0, false, []);
        Check(StartingDeckBinding.FreezeForSnapshot(frame with { DeckTracking = tracking }, Build, binding) is not null,
            "Consistent current tracking disabled an original deck reference.");
        foreach (var bad in new[] { tracking with { DeckVersion = "different-deck" }, tracking with { CardBuild = "255791" },
            tracking with { CardBuild = "unknown" }, tracking with { DeckVersion = "" } })
            Rejected(frame with { DeckTracking = bad }, binding, cards);
        foreach (var stale in new[] { tracking with { MatchId = "other", DeckVersion = "other" },
            tracking with { StateVersion = frame.StateVersion - 1, CardBuild = "255791" } })
            Check(StartingDeckBinding.FreezeForSnapshot(frame with { DeckTracking = stale }, Build, binding) is not null,
                "Unbound stale tracking was mistaken for the current deck or used to rewrite the host reference.");
    }

    private static void Malformed(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var deck = Deck(cards);
        foreach (var bad in new[] { deck with { Heroes = null! }, deck with { Cards = null! }, deck with { Sideboards = null! },
            deck with { Heroes = [] }, deck with { Heroes = [0] }, deck with { Format = 9 },
            deck with { Cards = [null!] }, deck with { Cards = [new(125998, 0)] }, deck with { Cards = [new(125998, 1), new(125998, 1)] },
            deck with { Sideboards = [null!] }, deck with { Sideboards = [new(125998, null!)] },
            deck with { Sideboards = [new(125998, [null!])] }, deck with { Sideboards = [new(125998, [new(0, 1)])] },
            deck with { Sideboards = [deck.Sideboards[0], deck.Sideboards[0]] } })
            Rejected(frame, Bind(frame, bad), cards);
        Rejected(frame, Bind(frame, null!), cards);
        Rejected(frame, Bind(frame, deck with { Sideboards = new[] { 125998, 125999 }.Select(owner =>
            new DeckSideboard(owner, Enumerable.Range(1, 501).Select(id => new DeckCard(id, 1)).ToArray())).ToArray() }), cards);
    }

    private static void Choice(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var before = JsonSerializer.Serialize(frame); var binding = Bind(frame, Deck(cards));
        var result = new LineEvaluator().EvaluateChoice(frame, [81], cards, 9, default, binding);
        var reference = result.SideboardEvidence!.InitialReference!;
        Check(reference is { IsValid: true, IsSessionBound: true, DeckVersionId: DeckId, CardBuild: Build, TotalCards: 3,
                IsStartingReferenceOnly: true, IsCurrentPool: false } && reference.Cards.Count == 3 &&
            reference.Unknowns.Any(value => value.Contains("本局记录", StringComparison.Ordinal)) &&
            reference.Unknowns.All(value => !value.Contains("未核验其对局", StringComparison.Ordinal)) &&
            result.SideboardEvidence.CurrentOffer?.OptionCount == 3 && result.SideboardEvidence.SelectedCounts.Sum(count => count.ObservedSelections) == 2 &&
            !result.SideboardEvidence.CurrentPoolIsExact && !result.CanEvaluateFollowupActions &&
            result.CardOutcomes.Single() is { CardId: "DINO_421", GrantedCostReduction: 3, ResultingCost: null } && before == JsonSerializer.Serialize(frame),
            "The bound starting reference changed native choice consequences, offered/selected facts, current pool or its input.");
        ReadOnly(() => ((IList<SideboardInitialCardReference>)reference.Cards).Clear());
        var damaged = binding with { Definition = binding.Definition with { Sideboards = [new(125998, [new(cards["EDR_489"].DbfId!.Value, 2)])] } };
        Check(SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, startingDeck: damaged).InitialReference is
            { IsValid: false, IsSessionBound: true, TotalCards: null, Cards.Count: 0, DeckVersionId: DeckId },
            "A structurally saved but incomplete JAIL sideboard was repaired into a valid reference or lost its real source metadata.");
    }

    private static void RawConflict(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var binding = Bind(frame, Deck(cards));
        var conflict = binding.Definition with { Sideboards = [new(125998, [new(87654321, 3)])] };
        var denied = SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, conflict, binding);
        Check(denied.InitialReference is null && denied.ResolvesEvidence && !JsonSerializer.Serialize(denied).Contains("87654321", StringComparison.Ordinal),
            "A conflicting raw reference overrode a bound original or disclosed its unused cards.");
        var reordered = binding.Definition with { Sideboards = [new(125998, binding.Definition.Sideboards[0].Cards.Reverse().ToArray())] };
        Check(SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, reordered, binding).InitialReference?.IsSessionBound == true,
            "Equivalent entry order was treated as different deck content.");
        var bad = binding with { MatchId = "other", DeckVersionId = "PRIVATE_BINDING_PROBE", Definition = conflict };
        var fallback = SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, binding.Definition, bad);
        var json = JsonSerializer.Serialize(fallback);
        Check(fallback.InitialReference is null && !json.Contains("PRIVATE_BINDING_PROBE", StringComparison.Ordinal) && !json.Contains("87654321", StringComparison.Ordinal),
            "A rejected binding fell back to a raw deck or echoed its identifier/cards.");
    }

    private static void RawCompatibility(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var reference = SideboardChoiceEvidenceRules.Evaluate(Frame(), 30, cards, Deck(cards)).InitialReference!;
        Check(reference is { IsValid: true, IsSessionBound: false, DeckVersionId: null, CardBuild: null, TotalCards: 3 } &&
            reference.Unknowns.Any(value => value.Contains("未核验其对局/套牌版本绑定", StringComparison.Ordinal)),
            "An old raw deck caller acquired invented session provenance.");
    }

    private static void MissingDefinition(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var missing = cards.Where(pair => pair.Key != "CORE_TTN_866").ToDictionary();
        var result = SideboardChoiceEvidenceRules.Evaluate(frame, 30, missing, startingDeck: Bind(frame, Deck(cards)));
        var entry = result.InitialReference!.Cards.Single(card => card.DbfId == cards["CORE_TTN_866"].DbfId);
        Check(result.InitialReference.IsSessionBound && entry is { CardId: null, InitialCopies: 1 } &&
            result.InitialReference.Unknowns.Any(value => value.Contains("DBF", StringComparison.Ordinal)) &&
            result.CurrentOffer!.Options.Any(option => option.CardId == "CORE_TTN_866"),
            "An offered card identity filled missing fixed-build DBF evidence in the original deck reference.");
    }

    private static void PrivacyAndHistory(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var binding = Bind(frame, Deck(cards));
        foreach (var denied in new[] { frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
            frame with { Choice = frame.Choice! with { PlayerId = 2 }, LocalPlayerId = 2 },
            frame with { Quality = frame.Quality with { IsStable = false } } })
            Check(SideboardChoiceEvidenceRules.Evaluate(denied, 30, cards, startingDeck: binding) is
                { InitialReference: null, CurrentOffer: null, SourceCardId: null }, "An unavailable private scope exposed an original deck reference.");
        var entities = frame.Entities.ToDictionary();
        entities[30] = entities[30] with { Tags = entities[30].Tags.Append(new("CRAFTABLE_FIRST_HALF_DBID", "1")).ToFrozenDictionary() };
        var custom = frame with { Entities = entities.ToFrozenDictionary() };
        var result = new LineEvaluator().EvaluateChoice(custom, [81], cards, 9, startingDeck: binding);
        Check(result.Verification == LineVerification.Unsupported && result.CardOutcomes.Count == 0 && !result.ResolvesEffects &&
            result.SideboardEvidence?.InitialReference?.IsSessionBound == true && result.SideboardEvidence.IsCurrentSourceConfirmed == false &&
            result.SideboardEvidence.SelectedCounts.Sum(count => count.ObservedSelections) == 2,
            "A starting reference promoted a custom current source into native effects or lost separately frozen historical evidence.");
    }

    private static void ExistingContract(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(); var rule = new LineEvaluator();
        var old = rule.EvaluateChoice(frame, [81], cards, 9, CancellationToken.None);
        Check(old.SideboardEvidence?.InitialReference is null, "An old positional caller acquired an unprovided starting reference.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { rule.EvaluateChoice(frame, [81], cards, 9, cancellation.Token, Bind(frame, Deck(cards))); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Starting deck evaluation bypassed cancellation.");
    }

    private static void Rejected(GameSnapshot frame, StartingDeckBinding binding, IReadOnlyDictionary<string, CardRecord> cards)
    {
        Check(StartingDeckBinding.FreezeForSnapshot(frame, Build, binding) is null, "An invalid original binding was accepted.");
        Check(SideboardChoiceEvidenceRules.Evaluate(frame, 30, cards, startingDeck: binding).InitialReference is null,
            "The sideboard helper bypassed original frame/binding validation.");
    }
    private static StartingDeckBinding Bind(GameSnapshot frame, DeckDefinition deck) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, Build, DeckId, deck);
    private static DeckDefinition Deck(IReadOnlyDictionary<string, CardRecord> cards) => new(2, [31], [new(125998, 1)],
        [new(125998, new[] { "EDR_489", "DINO_421", "CORE_TTN_866" }.Select(id => new DeckCard(cards[id].DbfId!.Value, 1)).ToArray())]);
    private static GameSnapshot Frame()
    {
        var entities = new[] { Player(1), Player(2), new EntitySnapshot(30, "JAIL_831", EntityKind.Card,
            new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "PLAY", ["CARDTYPE"] = "MINION" }.ToFrozenDictionary()) };
        var frame = new GameSnapshot("bound-sideboard", 10, 200, 5, 1, 1, GamePhase.Choice,
            entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = Build };
        return frame with
        {
            Choice = new(9, 1, "GENERAL", 30, 1, 1, [new(80, "EDR_489", 1, 0), new(81, "DINO_421", 1, 1), new(82, "CORE_TTN_866", 1, 2)], true)
            { SourceCardId = "JAIL_831", OfferedAtEventSequence = 100 },
            ChoiceHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence,
                [Entry(7, "EDR_489", 20, 30), Entry(8, "DINO_421", 40, 50)], true, 0, [])
        };
    }
    private static ChoiceHistoryEntry Entry(int choice, string card, long offered, long chosen) =>
        new(choice, 1, "GENERAL", 30, "JAIL_831", 1, 1, offered, chosen, [new(100, card, 1, 0)], [], [100], true);
    private static EntitySnapshot Player(int player) => new(player, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString(), ["NUM_CARDS_IN_HAND"] = "0", ["MAXHANDSIZE"] = "10" }.ToFrozenDictionary());
    private static IReadOnlyDictionary<string, CardRecord> WithFixedTerror(IReadOnlyDictionary<string, CardRecord> cards)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terror-end-turn-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == Build, "Unpinned starting-reference fixture.");
        var raw = fixture.RootElement.GetProperty("cards").EnumerateArray().Single(card => card.GetProperty("id").GetString() == "CORE_TTN_866");
        var result = cards.ToDictionary();
        result["CORE_TTN_866"] = new("CORE_TTN_866", raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!, raw.GetProperty("cost").GetInt32(),
            raw.GetProperty("collectible").GetBoolean(), [], [], raw.GetRawText());
        return result;
    }
    private static void ReadOnly(Action action)
    { try { action(); } catch (NotSupportedException) { return; } throw new InvalidOperationException("A definition or reference list was mutable."); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
