using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class RewindChoiceChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = LoadCards();
        yield return ("raptor chooses an offered Beast without inventing hand timing or Kindred", () => Discover(cards));
        yield return ("Dark Gifts grant audited deltas to the actual immediately preceding chosen Beast", () => Grants(cards));
        yield return ("Sweet Dreams moves the bound Beast to deck top without creating a hand card", () => SweetDreams(cards));
        yield return ("Dark Gifts require exact complete current-version adjacent choice evidence", () => HistoryBindings(cards));
        yield return ("Dark Gift target comes from chosen rows rather than unchosen options", () => ActualSelection(cards));
        yield return ("Dark Gifts reject absent hidden transformed opposing and already played targets", () => TargetIdentity(cards));
        yield return ("Rewind rejects observed type and race conflicts across current targets and frozen candidates", () => ObservedTypes(cards));
        yield return ("timeline buttons distinguish keeping the result from an unknown rewind restoration", () => Timeline(cards));
        yield return ("Rewind stages reject mixed candidates and invalid source perspective or build", () => InvalidStage(cards));
        yield return ("Rewind projections preserve immutable grants and do not mutate input state", () => FrozenResults(cards));
        yield return ("common choice evaluation exposes Rewind consequences without fabricating a next hand", () => Integration(cards));
        yield return ("Kindred matches the frozen previous own turn and grants one independent cost reduction", () => KindredPositive(cards));
        yield return ("Kindred is false only for a complete previous own turn with all played types known", () => KindredNegative(cards));
        yield return ("Kindred is inactive on the first own turn only when observed continuously from turn one", () => KindredFirstOwnTurn(cards));
        yield return ("Kindred uses actual extra-turn windows instead of turn minus two", () => KindredExtraTurns(cards));
        yield return ("Kindred rejects stale future truncated malformed and source-unbound turn evidence", () => KindredBindings(cards));
        yield return ("Kindred never fills past races from current entities or static activation-looking tags", () => KindredFrozenTypes(cards));
        yield return ("Kindred does not recreate timeline effects or turn an unknown gift target into a resolved grant", () => KindredStages(cards));
    }

    private static void Discover(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = DiscoverFrame(); var result = Eval(frame, cards)!;
        Check(result is { Stage: "DiscoverBeast", ResolvesEffects: true, DirectCardId: "EDR_227", DirectCardName: "幽爪熊" } &&
            result.AddsNewHandCard is null && result.TargetEntityId is null && result.GrantedDarkGiftId is null &&
            result.KindredActive is null && result.GrantedCostReduction is null && result.ResultingCost is null &&
            !result.CanEvaluateFollowupActions, "An offered Beast acquired a fabricated gift, final cost or hand entity.");
        var alias = Replace(frame, Entity(30, "CORE_EDR_004", "PLAY"));
        alias = alias with { Choice = alias.Choice! with { SourceCardId = "CORE_EDR_004" } };
        Check(Eval(alias, cards)?.DirectCardName == "幽爪熊", "Audited fixed-build alias was not recognized.");
    }

    private static void Grants(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var id in new[] { "EDR_100t", "EDR_100t1", "EDR_100t2", "EDR_100t3", "EDR_100t4", "EDR_100t5",
            "EDR_100t6", "EDR_100t7", "EDR_100t9", "EDR_100t10", "EDR_100t13" })
        {
            var result = Eval(GiftFrame(id), cards)!;
            Check(result is { Stage: "DarkGift", ResolvesEffects: true, HistoryBound: true, TargetEntityId: 40,
                TargetCardId: "EDR_227", TargetCardName: "幽爪熊", DirectCardName: "幽爪熊", AddsNewHandCard: false } &&
                result.GrantedDarkGiftId == id && result.GrantedDarkGiftName == cards[id].Name &&
                result.GrantedEffectText == cards[id].Text && result.ResultingCost is null && result.ResultingAttack is null &&
                result.ResultingHealth is null && result.KindredActive is null && !result.CanEvaluateFollowupActions,
                "An audited gift was not granted to the current actually chosen Beast, or final values were guessed.");
        }
        var waking = Eval(GiftFrame("EDR_100t"), cards)!;
        Check(waking.AttackDelta == 3 && waking.HealthDelta is null && waking.GrantedKeywords.SequenceEqual(["LIFESTEAL"]), "Waking Terror grant changed.");
        var rested = Eval(GiftFrame("EDR_100t1"), cards)!;
        Check(rested.AttackDelta == 2 && rested.HealthDelta == 2 && rested.GrantedKeywords.SequenceEqual(["ELUSIVE"]), "Well Rested grant changed.");
        var claws = Eval(GiftFrame("EDR_100t2"), cards)!;
        Check(claws.AttackDelta == -2 && claws.GrantedCostReduction == 2 && claws.ResultingCost is null, "Short Claws reused current or printed cost.");
        var bundled = Eval(GiftFrame("EDR_100t3"), cards)!;
        Check(bundled.HealthDelta == 4 && bundled.GrantedKeywords.SequenceEqual(["TAUNT"]), "Bundled Up grant changed.");
        var scales = Eval(GiftFrame("EDR_100t10"), cards)!;
        Check(scales.GrantedKeywords.Count == 0 && scales.GrantedEffectText!.Contains("3次", StringComparison.Ordinal), "Three-hit shield became an ordinary combat flag.");
    }

    private static void SweetDreams(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame("EDR_100t8");
        var result = Eval(frame, cards)!;
        Check(result is { ResolvesEffects: true, TargetEntityId: 40, AttackDelta: 4, HealthDelta: 5,
            DestinationAfterGift: "DeckTop", AddsNewHandCard: false } && result.ResultingAttack is null,
            "Sweet Dreams added a hand card, lost the deck-top destination or guessed final stats.");
        var pending = Eval(Replace(frame, Entity(40, "EDR_227", "SETASIDE")), cards)!;
        Check(pending.DestinationAfterGift == "DeckTop" && pending.AddsNewHandCard == false, "A pending Sweet Dreams target was promised as a new hand card.");
        var normalPending = Eval(Replace(GiftFrame(), Entity(40, "EDR_227", "SETASIDE")), cards)!;
        Check(normalPending.ResolvesEffects && normalPending.AddsNewHandCard is null, "Pending gift timing became guaranteed hand gain.");
    }

    private static void HistoryBindings(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame(); var history = frame.ChoiceHistory!; var entry = history.Entries.Single();
        var rejected = new List<GameSnapshot>
        {
            frame with { ChoiceHistory = null },
            WithHistory(frame, [], complete: false),
            WithHistory(frame, [entry], match: "other-match"),
            WithHistory(frame, [entry], version: 11),
            WithHistory(frame, [entry], sequence: 101),
            WithHistory(frame, [entry], truncated: 1),
            WithHistory(frame, [entry], warnings: ["partial"]),
            frame with { Choice = frame.Choice! with { PreviousOfferedAtEventSequence = 0 } },
            frame with { Choice = frame.Choice! with { PreviousOfferedAtEventSequence = 11 } },
            frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 0 } },
            frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 101 } },
            WithHistory(frame, [Entry(chosenAt: 31)]),
            WithHistory(frame, [Entry(chosenAt: 10)]),
            WithHistory(frame, [Entry(offeredAt: 21, chosenAt: 20)]),
            WithHistory(frame, [Entry(source: 31)]),
            WithHistory(frame, [Entry(sourceCard: "CORE_EDR_004")]),
            WithHistory(frame, [Entry(player: 2)]),
            WithHistory(frame, [Entry(complete: false)]),
            WithHistory(frame, [entry, entry]),
            WithHistory(frame, [entry, Entry(choiceId: 9, offeredAt: 22, chosenAt: 25, source: 99)])
        };
        foreach (var invalid in rejected)
        {
            var result = Eval(invalid, cards)!;
            Check(!result.ResolvesEffects && !result.HistoryBound && result.TargetEntityId is null &&
                result.AttackDelta is null && result.GrantedEffectText is null && result.DirectCardId is null,
                "Missing/stale/interleaved/unbound history granted an effect or exposed a historical target.");
        }
    }

    private static void ActualSelection(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame();
        foreach (var chosen in new IReadOnlyList<int>[] { [], [41], [40, 41], [999] })
        {
            var result = Eval(WithHistory(frame, [Entry(chosen: chosen)]), cards)!;
            Check(!result.ResolvesEffects && result.TargetEntityId is null && result.DirectCardId is null,
                "An unchosen offered Beast or missing/duplicate result became the target.");
        }
        var options = new ChoiceOptionSnapshot[] { new(40, "EDR_227", 1, 1), new(41, "EDR_480", 2, 2) };
        Check(!Eval(WithHistory(frame, [Entry(options: options)]), cards)!.ResolvesEffects, "An opposing historical candidate was accepted.");
        Check(!Eval(WithHistory(frame, [Entry(entities: [Entity(40, "EDR_480", "SETASIDE")])]), cards)!.ResolvesEffects,
            "A frozen identity conflict was repaired using a later target.");
        Check(!Eval(WithHistory(frame, [Entry(entities: [Entity(40, "EDR_227", "HAND")])]), cards)!.ResolvesEffects,
            "An already present historical hand entity was misidentified as a newly offered candidate.");
        Check(!Eval(WithHistory(frame, [Entry(entities: [Entity(40, "EDR_227", "SETASIDE") with { Tags = null! }])]), cards)!.ResolvesEffects,
            "A malformed frozen entity with missing tags crashed or granted an effect.");
    }

    private static void TargetIdentity(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame();
        foreach (var target in new[] { Entity(40, null, "SETASIDE"), Entity(40, "EDR_480", "HAND"),
            Entity(40, "EDR_227", "HAND", owner: 2), Entity(40, "EDR_227", "PLAY"), Entity(40, "EDR_227", "DECK") })
            Check(!Eval(Replace(frame, target), cards)!.ResolvesEffects, "A hidden, transformed, opposing or moved target was granted a gift.");
        var entities = frame.Entities.ToDictionary(); entities.Remove(40); entities[99] = Entity(99, "EDR_227", "HAND");
        Check(!Eval(frame with { Entities = entities.ToFrozenDictionary() }, cards)!.ResolvesEffects, "A same-name new entity was substituted for the historical target.");
    }

    private static void Timeline(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var keep = Eval(TimelineFrame(), cards)!;
        Check(keep is { Stage: "KeepTimeline", ResolvesEffects: true, KeepsTimeline: true, RequestsRewind: false, AddsNewHandCard: false } &&
            keep.DirectCardId is null && keep.GrantedDarkGiftId is null, "Keep Timeline became a hand card or replay.");
        var rewind = Eval(TimelineFrame(), cards, 61)!;
        Check(rewind is { Stage: "RewindTimeline", ResolvesEffects: true, KeepsTimeline: false, RequestsRewind: true } &&
            rewind.AddsNewHandCard is null && rewind.RestoredHandCount is null && rewind.RestoredMana is null &&
            rewind.RestoresSourceToHand is null && rewind.ResultingCost is null && !rewind.CanEvaluateFollowupActions,
            "Rewind reconstructed pre-play state from current resources or printed cost.");
    }

    private static void ObservedTypes(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var conflicting = new (string Tag, string Value)[]
        {
            ("CARDTYPE", "SPELL"), ("CARDTYPE", "5"), ("CARDTYPE", "HERO"), ("CARDTYPE", "3"),
            ("CARDTYPE", "UNKNOWN"), ("CARDRACE", "DEMON"), ("CARDRACE", "15"),
            ("CARDRACE", "HERO"), ("CARDRACE", "0"), ("CARDRACE", "UNKNOWN")
        };
        foreach (var (tag, value) in conflicting)
        {
            if (tag == "CARDTYPE")
                Check(Eval(Replace(GiftFrame(), Tagged(Entity(30, "CORE_EDR_004_2026", "PLAY"), tag, value)), cards) is null,
                    "An explicit non-minion source type was overwritten by the fixed source card definition.");
            var current = Replace(GiftFrame(), Tagged(Entity(40, "EDR_227", "HAND"), tag, value));
            NoGrant(Eval(current, cards)!, "Current target type/race conflict was overwritten by printed Beast metadata.");
            var common = new LineEvaluator().EvaluateChoice(current, [60], cards, 8);
            Check(common.IsLegal == true && common.Verification == LineVerification.Unsupported && !common.ResolvesEffects,
                "A target conflict became an illegal selection or a resolved gift.");
            foreach (var id in new[] { 40, 41 })
            {
                var frozen = new[] { Entity(40, "EDR_227", "SETASIDE"), Entity(41, "EDR_480", "SETASIDE") }
                    .Select(entity => entity.Id == id ? Tagged(entity, tag, value) : entity).ToArray();
                NoGrant(Eval(WithHistory(GiftFrame(), [Entry(entities: frozen)]), cards)!,
                    "A chosen or unchosen frozen candidate's type/race conflict was ignored.");
                var discover = Replace(DiscoverFrame(), Tagged(Entity(id, id == 40 ? "EDR_227" : "EDR_480", "SETASIDE"), tag, value));
                Check(Eval(discover, cards) is { ResolvesEffects: false, DirectCardId: null },
                    "A current Beast candidate's type/race conflict was ignored.");
            }
        }
        foreach (var (type, race) in new[] { ("MINION", "BEAST"), ("4", "20"), ("MINION", "ALL"), ("4", "26") })
        {
            EntitySnapshot Valid(EntitySnapshot entity) => Tagged(Tagged(Tagged(entity, "CARDTYPE", type), "CARDRACE", race), "UNMAPPED_TAG", "unknown");
            Check(Eval(Replace(GiftFrame(), Valid(Entity(40, "EDR_227", "HAND"))), cards) is { ResolvesEffects: true, AttackDelta: 3 },
                "An audited Beast/ALL tag or an unrelated unknown tag rejected a valid target.");
            Check(Eval(Replace(GiftFrame(), Tagged(Entity(30, "CORE_EDR_004_2026", "PLAY"), "CARDTYPE", type)), cards)!.ResolvesEffects,
                "An observed minion source type was rejected.");
            Check(Eval(WithHistory(GiftFrame(), [Entry(entities: [Valid(Entity(40, "EDR_227", "SETASIDE")),
                Valid(Entity(41, "EDR_480", "SETASIDE"))])]), cards)!.ResolvesEffects,
                "Compatible frozen minion/race tags rejected the bound choice.");
            Check(Eval(Replace(DiscoverFrame(), Valid(Entity(40, "EDR_227", "SETASIDE"))), cards) is { Stage: "DiscoverBeast", ResolvesEffects: true },
                "Compatible current Beast tags rejected discovery.");
        }
        Check(Eval(GiftFrame(), cards)!.ResolvesEffects && Eval(DiscoverFrame(), cards)!.ResolvesEffects,
            "Missing dynamic type/race tags were treated as explicit contradictions.");

        static void NoGrant(RewindChoiceResolution result, string message) => Check(!result.ResolvesEffects && !result.HistoryBound &&
            result.TargetEntityId is null && result.TargetCardId is null && result.DirectCardId is null && result.AttackDelta is null &&
            result.HealthDelta is null && result.GrantedCostReduction is null && result.GrantedKeywords.Count == 0 && result.GrantedEffectText is null, message);
    }

    private static void InvalidStage(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame();
        foreach (var invalid in new[] { frame with { ClientBuild = "999999" }, frame with { IsSpectating = true },
            frame with { Choice = frame.Choice! with { SourceCardId = null } },
            frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
            Replace(frame, Entity(30, "CORE_EDR_004_2026", "HAND")), Replace(frame, Entity(30, "CORE_EDR_004_2026", "PLAY", owner: 2)) })
            Check(Eval(invalid, cards) is null, "An unsafe source, build or perspective reached a Rewind effect.");
        foreach (var offered in new[] { DiscoverFrame(), TimelineFrame() })
            Check(Eval(offered with { Choice = offered.Choice! with { SourceCardId = null } }, cards) is null,
                "A later Raptor identity filled a missing offered source identity in a direct helper call.");
        var mixed = frame with { Choice = frame.Choice! with { Options = [new(60, "EDR_100t", 1, 1), new(61, "EDR_227", 1, 2)] } };
        Check(Eval(mixed, cards) is { Stage: "Unknown", ResolvesEffects: false }, "Mixed candidates were forced into a known stage.");
        var malformed = cards.ToDictionary(); malformed["EDR_100t"] = cards["EDR_100t"] with { DbfId = 999 };
        Check(Eval(frame, malformed) is { ResolvesEffects: false }, "An unaudited gift record was granted.");
        var unknownRace = cards.ToDictionary(); unknownRace["EDR_227"] = cards["EDR_227"] with { RawJson = "{}" };
        Check(Eval(DiscoverFrame(), unknownRace) is { ResolvesEffects: false }, "A candidate name or text substituted for an audited Beast type.");
        Check(Eval(Replace(frame, Entity(40, "EDR_227", "HAND") with { Tags = null! }), cards) is null,
            "Missing current tags reached an effect or crashed.");
    }

    private static void FrozenResults(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = GiftFrame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame, cards)!;
        Check(before == JsonSerializer.Serialize(frame), "Gift evaluation mutated the authoritative snapshot.");
        try { ((IList<string>)result.GrantedKeywords).Clear(); throw new Exception("Consumer changed granted keywords."); }
        catch (NotSupportedException) { }
        try { ((IList<string>)result.RuleCoverage).Clear(); throw new Exception("Consumer changed rule coverage."); }
        catch (NotSupportedException) { }
    }

    private static void Integration(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var result = new LineEvaluator().EvaluateChoice(GiftFrame(), [60], cards, 8);
        Check(result.Verification == LineVerification.Conditional && result.IsLegal == true && result.ResolvesEffects &&
            !result.SelectionOnly && result.Rewind is { TargetCardName: "幽爪熊", AttackDelta: 3 } &&
            result.CardOutcomes.Count == 0 && result.HandCountAfter is null && result.DirectDeckChange is null &&
            !result.CanEvaluateFollowupActions, "Common choice evaluation treated a grant as an extra hand card or a usable next snapshot.");
    }

    private static void KindredPositive(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame, cards)!;
        Check(result is { KindredActive: true, KindredCostReduction: 1, KindredSourcePlaySequence: 70,
            KindredPreviousOwnTurnStart: 10, KindredMatchingPlaySequence: 15, ResultingCost: null } &&
            result.GrantedCostReduction is null && result.AddsNewHandCard is null && !result.CanEvaluateFollowupActions &&
            before == JsonSerializer.Serialize(frame), "Kindred lost its frozen prior own-turn witness or guessed current cost/hand gain.");
        var gift = Eval(KindredFrame(gift: true), cards)!;
        Check(gift is { ResolvesEffects: true, KindredActive: true, KindredCostReduction: 1,
            GrantedCostReduction: 2, ResultingCost: null }, "Kindred and Short Claws were summed into an already observed candidate/final cost.");
        var all = Eval(WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "26")]),
            Turn(4, 2, 30, 59), CurrentTurn()]), cards)!;
        Check(all.KindredActive == true, "An observed ALL minion did not share Beast.");
        var partial = Eval(WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "20")], complete: false),
            Turn(4, 2, 30, 59), CurrentTurn()]), cards)!;
        Check(partial.KindredActive == true, "A positive witness was discarded merely because the previous play list was not exhaustive.");
    }

    private static void KindredNegative(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame();
        foreach (var previous in new[] { Turn(3, 1, 10, 29),
            Turn(3, 1, 10, 29, [Play(15, 90, "CORE_DS1_184", "5", null)]) })
        {
            var result = Eval(WithTurns(frame, [previous, Turn(4, 2, 30, 59), CurrentTurn()]), cards)!;
            Check(result is { KindredActive: false, KindredCostReduction: 0, KindredMatchingPlaySequence: null,
                KindredSourcePlaySequence: 70, KindredPreviousOwnTurnStart: 10 }, "A complete empty/spell-only previous own turn did not prove no Beast match.");
        }
        var incomplete = Eval(WithTurns(frame, [Turn(3, 1, 10, 29, complete: false), Turn(4, 2, 30, 59), CurrentTurn()]), cards)!;
        Check(incomplete.KindredActive is null && incomplete.KindredCostReduction is null, "An incomplete empty list was called inactive.");
        var missingType = Eval(WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", null, null)]),
            Turn(4, 2, 30, 59), CurrentTurn()]), cards)!;
        Check(missingType.KindredActive is null, "An unknown played type was treated as evidence of absence.");
    }

    private static void KindredExtraTurns(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame();
        var ownExtra = WithTurns(frame, [Turn(3, 1, 10, 29),
            Turn(4, 1, 30, 59, [Play(35, 90, "EDR_227", "4", "20")]), CurrentTurn()]);
        var result = Eval(ownExtra, cards)!;
        Check(result is { KindredActive: true, KindredPreviousOwnTurnStart: 30, KindredMatchingPlaySequence: 35 },
            "An immediately preceding extra own turn was replaced by turn minus two.");
        var opponentExtra = WithTurns(frame, [Turn(2, 1, 2, 9, [Play(5, 90, "EDR_227", "4", "20")]),
            Turn(3, 2, 10, 29), Turn(4, 2, 30, 59), CurrentTurn()]);
        Check(Eval(opponentExtra, cards) is { KindredActive: true, KindredPreviousOwnTurnStart: 2, KindredMatchingPlaySequence: 5 },
            "Two opponent turns hid the actual previous own turn.");
        var olderBeast = WithTurns(frame, [Turn(2, 1, 2, 9, [Play(5, 90, "EDR_227", "4", "20")]),
            Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn()]);
        Check(Eval(olderBeast, cards) is { KindredActive: false, KindredPreviousOwnTurnStart: 10 },
            "An older Beast was used after a more recent own turn without one.");
    }

    private static void KindredFirstOwnTurn(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame();
        var source = new[] { Play(70, 30, "CORE_EDR_004_2026", "4", "20") };
        foreach (var turns in new IReadOnlyList<TurnHistoryEntry>[]
        {
            [Turn(1, 1, 60, null, source, complete: false)],
            [Turn(1, 2, 10, 29), Turn(2, 1, 60, null, source, complete: false)],
            [Turn(1, 2, 10, 29), Turn(2, 2, 30, 59), Turn(3, 1, 60, null, source, complete: false)]
        })
        {
            var result = Eval(WithTurns(frame with { Turn = turns[^1].TurnNumber }, turns), cards)!;
            Check(result is { KindredActive: false, KindredCostReduction: 0, KindredSourcePlaySequence: 70,
                KindredPreviousOwnTurnStart: null, KindredMatchingPlaySequence: null } &&
                result.RuleCoverage.Contains("raptor-kindred-first-own-turn-from-game-start"),
                "A first own turn observed from actual turn one remained unknown or invented a prior own turn.");
        }
        foreach (var state in new[]
        {
            WithTurns(frame with { Turn = 4 }, [Turn(3, 2, 10, 29), Turn(4, 1, 60, null, source, complete: false)]),
            WithTurns(frame, [CurrentTurn()]),
            WithTurns(frame with { Turn = 1 }, [Turn(1, 1, 60, null, source, complete: false)], truncated: 1),
            WithTurns(frame with { Turn = 1 }, [Turn(1, 1, 60, null, source, complete: false)], complete: false)
        })
            Check(Eval(state, cards) is { KindredActive: null, KindredCostReduction: null },
                "A missing game-start window, truncation or partial ledger was called the first own turn.");
    }

    private static void KindredBindings(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame(); var turns = frame.TurnHistory!.Turns;
        var invalid = new[]
        {
            frame with { TurnHistory = null },
            WithTurns(frame, turns, match: "other"), WithTurns(frame, turns, version: 11), WithTurns(frame, turns, sequence: 101),
            WithTurns(frame, turns, complete: false), WithTurns(frame, turns, truncated: 1), WithTurns(frame, turns, warnings: ["PRIVATE_FUTURE_ID"]),
            frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 0 } },
            frame with { ActivePlayerId = 2 }, frame with { Turn = 6 },
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(hasStart: false)]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [])]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [Play(91, 30, "CORE_EDR_004_2026", "4", "20")])]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [Play(70, 30, "EDR_227", "4", "20")])]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [Play(70, 30, "CORE_EDR_004_2026", "4", "20"), Play(80, 30, "EDR_227", "4", "20")])]),
            WithTurns(frame, [Turn(2, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 31), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, null), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 29, [Play(10, 90, "EDR_227", "4", "20")]), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 29, [Play(29, 90, "EDR_227", "4", "20")]), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "20"), Play(15, 91, "EDR_480", "4", "20")]), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "20", owner: 2)]), Turn(4, 2, 30, 59), CurrentTurn()]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [Play(70, 30, "CORE_EDR_004_2026", "5", "20")])]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [Play(70, 30, "CORE_EDR_004_2026", "4", null)])]),
            WithTurns(frame, [Turn(3, 1, 10, 29), Turn(4, 2, 30, 59), CurrentTurn(plays: [new(70, Tagged(Tagged(Entity(30, "CORE_EDR_004_2026", "PLAY"), "CARDTYPE", "4"), "CARDRACE", "20"))])])
        };
        foreach (var state in invalid)
        {
            var result = Eval(state, cards)!;
            Check(result.KindredActive is null && result.KindredCostReduction is null && result.KindredMatchingPlaySequence is null &&
                result.ResultingCost is null && !JsonSerializer.Serialize(result).Contains("PRIVATE_FUTURE_ID", StringComparison.Ordinal),
                "Invalid/future/unbound history activated Kindred or disclosed untrusted history values.");
        }
    }

    private static void KindredFrozenTypes(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = KindredFrame();
        var frozen = WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "CORE_TTN_866", "4", "15")]), Turn(4, 2, 30, 59), CurrentTurn()]);
        Check(Eval(frozen, cards) is { KindredActive: true }, "An audited dual Demon/Beast's frozen native Demon primary lost its Beast type.");
        var changed = WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "15")]), Turn(4, 2, 30, 59), CurrentTurn()]);
        Check(Eval(Replace(changed, Tagged(Entity(90, "EDR_227", "PLAY"), "CARDRACE", "20")), cards)!.KindredActive is null,
            "A later Beast race or the frozen card's printed Beast race replaced its different observed race at play.");
        var missing = WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", null)]), Turn(4, 2, 30, 59), CurrentTurn()]);
        Check(Eval(Replace(missing, Tagged(Entity(90, "EDR_227", "PLAY"), "CARDRACE", "20")), cards)!.KindredActive is null,
            "Current entity tags filled missing historical race evidence.");
        Check(Eval(Replace(frame, Entity(90, "CORE_DS1_184", "GRAVEYARD")), cards)!.KindredActive == true,
            "A later transformation erased a frozen Beast play witness.");
        var noHistory = DiscoverFrame();
        noHistory = Replace(noHistory, Tagged(Tagged(Tagged(noHistory.Entities[30], "KINDRED", "1"), "4028", "1"), "4050", "1"));
        Check(Eval(noHistory, cards)!.KindredActive is null, "Static keyword/numeric tags were treated as runtime activation.");
    }

    private static void KindredStages(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var unknownTarget = Replace(KindredFrame(gift: true), Entity(40, null, "SETASIDE"));
        Check(Eval(unknownTarget, cards) is { ResolvesEffects: false, KindredActive: true, KindredCostReduction: 1, TargetEntityId: null },
            "Known Kindred invented a missing current gift target or resolved its grant.");
        var timeline = TimelineFrame() with { Turn = 5, Choice = TimelineFrame().Choice! with { OfferedAtEventSequence = 90 } };
        timeline = WithTurns(timeline, KindredFrame().TurnHistory!.Turns);
        Check(Eval(timeline, cards) is { KindredActive: null, KindredCostReduction: null, AddsNewHandCard: false },
            "Keeping a timeline replayed Kindred's already observed reward.");
    }

    private static GameSnapshot KindredFrame(bool gift = false)
    {
        var frame = gift ? GiftFrame("EDR_100t2") : DiscoverFrame();
        frame = frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 90, PreviousOfferedAtEventSequence = gift ? 80 : 0 } };
        if (gift) frame = WithHistory(frame, [Entry(offeredAt: 80, chosenAt: 81)]);
        return WithTurns(frame, [Turn(3, 1, 10, 29, [Play(15, 90, "EDR_227", "4", "20")]), Turn(4, 2, 30, 59), CurrentTurn()]);
    }
    private static TurnHistoryEntry CurrentTurn(IReadOnlyList<TurnPlaySnapshot>? plays = null, bool hasStart = true) =>
        Turn(5, 1, 60, null, plays ?? [Play(70, 30, "CORE_EDR_004_2026", "4", "20")], complete: false, hasStart: hasStart);
    private static TurnHistoryEntry Turn(int number, int player, long start, long? end,
        IReadOnlyList<TurnPlaySnapshot>? plays = null, bool complete = true, bool hasStart = true) => new(number, player, start, end, hasStart, complete, plays ?? []);
    private static TurnPlaySnapshot Play(long sequence, int id, string? card, string? kind, string? race, int owner = 1)
    {
        var entity = Entity(id, card, "HAND", owner);
        if (kind is not null) entity = Tagged(entity, "CARDTYPE", kind);
        if (race is not null) entity = Tagged(entity, "CARDRACE", race);
        return new(sequence, entity);
    }
    private static GameSnapshot WithTurns(GameSnapshot frame, IReadOnlyList<TurnHistoryEntry> turns, bool complete = true,
        string? match = null, long? version = null, long? sequence = null, long truncated = 0, IReadOnlyList<string>? warnings = null) => frame with
    { TurnHistory = new(match ?? frame.MatchId, version ?? frame.StateVersion, sequence ?? frame.EventSequence, turns, complete, truncated, warnings ?? []) };

    private static RewindChoiceResolution? Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, int? selected = null) =>
        RewindChoiceRules.Evaluate(frame, frame.Entities[30], frame.Choice!.Options.Single(option => option.EntityId == (selected ?? frame.Choice.Options[0].EntityId)), cards);

    private static GameSnapshot DiscoverFrame() => BaseFrame() with
    {
        Choice = new(7, 1, "GENERAL", 30, 1, 1, [new(40, "EDR_227", 1, 1), new(41, "EDR_480", 1, 2)], true)
            { SourceCardId = "CORE_EDR_004_2026", OfferedAtEventSequence = 10 }
    };
    private static GameSnapshot GiftFrame(string id = "EDR_100t")
    {
        var frame = Replace(BaseFrame(), Entity(40, "EDR_227", "HAND")) with
        {
            Choice = new(8, 1, "GENERAL", 30, 1, 1, [new(60, id, 1, 1), new(61, id == "EDR_100t1" ? "EDR_100t3" : "EDR_100t1", 1, 2)], true)
                { SourceCardId = "CORE_EDR_004_2026", OfferedAtEventSequence = 30, PreviousOfferedAtEventSequence = 10 }
        };
        return WithHistory(frame, [Entry()]);
    }
    private static GameSnapshot TimelineFrame() => BaseFrame() with
    {
        Choice = new(8, 1, "GENERAL", 30, 1, 1, [new(60, "TIME_000ta", 1, 1), new(61, "TIME_000tb", 1, 2)], true)
            { SourceCardId = "CORE_EDR_004_2026", OfferedAtEventSequence = 30 }
    };
    private static GameSnapshot BaseFrame() => new("rewind-fixture", 10, 100, 5, 1, 1, GamePhase.Choice,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1", ["NUM_CARDS_IN_HAND"] = "1", ["MAXHANDSIZE"] = "10" }.ToFrozenDictionary()),
            Entity(30, "CORE_EDR_004_2026", "PLAY"), Entity(40, null, "SETASIDE"), Entity(41, null, "SETASIDE"),
            Entity(60, null, "SETASIDE"), Entity(61, null, "SETASIDE") }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
        { ClientBuild = "255790" };
    private static ChoiceHistoryEntry Entry(int choiceId = 7, int source = 30, string sourceCard = "CORE_EDR_004_2026",
        int player = 1, long offeredAt = 10, long chosenAt = 20, IReadOnlyList<int>? chosen = null,
        IReadOnlyList<ChoiceOptionSnapshot>? options = null, IReadOnlyList<EntitySnapshot>? entities = null, bool complete = true) =>
        new(choiceId, player, "GENERAL", source, sourceCard, 1, 1, offeredAt, chosenAt,
            options ?? [new(40, "EDR_227", 1, 1), new(41, "EDR_480", 1, 2)],
            entities ?? [Entity(40, "EDR_227", "SETASIDE"), Entity(41, "EDR_480", "SETASIDE")], chosen ?? [40], complete);
    private static GameSnapshot WithHistory(GameSnapshot frame, IReadOnlyList<ChoiceHistoryEntry> entries,
        bool complete = true, string? match = null, long? version = null, long? sequence = null, long truncated = 0, IReadOnlyList<string>? warnings = null) =>
        frame with { ChoiceHistory = new(match ?? frame.MatchId, version ?? frame.StateVersion, sequence ?? frame.EventSequence, entries, complete, truncated, warnings ?? []) };
    private static EntitySnapshot Entity(int id, string? card, string zone, int owner = 1) =>
        new(id, card, EntityKind.Card, new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString() }.ToFrozenDictionary());
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static EntitySnapshot Tagged(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }

    private static IReadOnlyDictionary<string, CardRecord> LoadCards()
    {
        var cards = new Dictionary<string, CardRecord>(StringComparer.Ordinal);
        foreach (var name in new[] { "rewind-choice-255790.json", "beast-hunter-255790.json", "mythical-terror-255790.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Rewind fixture build changed.");
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
                    raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        }
        return cards.ToFrozenDictionary(StringComparer.Ordinal);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
