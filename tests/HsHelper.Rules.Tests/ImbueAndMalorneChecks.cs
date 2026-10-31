using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class ImbueAndMalorneChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("explicit cumulative Imbue zero and positive counts resolve both audited thresholds", () =>
        {
            foreach (var count in new[] { 0, 1, 2, 3, 4, 9, int.MaxValue })
            {
                var evidence = ImbueEvidence.Evaluate(Frame(count));
                Check(evidence.Count == count && evidence.AtLeast(2) == (count >= 2) && evidence.AtLeast(4) == (count >= 4) &&
                    evidence.SourceEntityIds.SequenceEqual([1]), "Observed cumulative count/threshold was changed or explicit zero was lost.");
            }
        });
        yield return ("missing cumulative Imbue never becomes zero or a current Wolf level", () =>
        {
            var frame = Replace(Frame(null), Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10),
                ("TAG_SCRIPT_DATA_NUM_1", 9), ("TAG_SCRIPT_DATA_NUM_2", 9)));
            Check(ImbueEvidence.Evaluate(frame).Count is null && ImbueEvidence.Evaluate(frame).AtLeast(2) is null,
                "Current hero power strength was misread as game-wide Imbues.");
            frame = Replace(frame, Player(null, extra: [("IMBUE", 4), ("CARES_ABOUT_IMBUE_CARDS", 4), ("IMBUE_COUNT", 4)]));
            Check(ImbueEvidence.Evaluate(frame).Count is null, "An unreviewed tag name was treated as the cumulative counter.");
        });
        yield return ("cumulative Imbue rejects malformed negative overflow and conflicting observations", () =>
        {
            foreach (var value in new[] { "-1", "overflow", "2147483648" })
            {
                var tags = Player(null).Tags.ToDictionary(); tags[ImbueEvidence.CounterTag] = value;
                Check(ImbueEvidence.Evaluate(Replace(Frame(null), Player(null) with { Tags = tags.ToFrozenDictionary() })).Count is null,
                    "Malformed cumulative counter became a number.");
            }
            var conflict = Replace(Frame(4), Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10), ("3527", 3)));
            Check(ImbueEvidence.Evaluate(conflict).Count is null, "Conflicting current counters were combined or maxed.");
        });
        yield return ("cumulative Imbue accepts explicitly linked current hero and power with consistent ownership", () =>
        {
            var frame = Replace(Frame(null), Entity(11, "HERO_05", "PLAY", 1, ("CARDTYPE", 3), ("3527", 4)));
            Check(ImbueEvidence.Evaluate(frame).Count == 4, "An explicitly linked current hero counter was discarded.");
            frame = Replace(frame, Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10), ("3527", 4)));
            frame = Replace(frame, Player(4));
            var evidence = ImbueEvidence.Evaluate(frame);
            Check(evidence.Count == 4 && evidence.SourceEntityIds.Order().SequenceEqual([1, 11, 12]), "Consistent current counter sources were not frozen.");
        });
        yield return ("cumulative Imbue never borrows another player old power hidden entity or arbitrary carrier", () =>
        {
            foreach (var carrier in new[]
            {
                Entity(12, "EDR_850p", "PLAY", 2, ("CARDTYPE", 10), ("3527", 4)),
                Entity(12, "EDR_850p", "GRAVEYARD", 1, ("CARDTYPE", 10), ("3527", 4)),
                Entity(12, "EDR_850p", "HAND", 1, ("CARDTYPE", 10), ("3527", 4)),
                Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 4), ("3527", 4)),
                Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10), ("3527", 4)) with { CardId = null },
                Entity(99, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10), ("3527", 4)),
                Entity(30, "EDR_888", "PLAY", 1, ("CARDTYPE", 4), ("3527", 4))
            }) Check(ImbueEvidence.Evaluate(Replace(Frame(null), carrier)).Count is null, "An unconfirmed/stale counter carrier supplied a cumulative count.");
            var other = new EntitySnapshot(2, null, EntityKind.Player,
                new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["CONTROLLER"] = "2", ["3527"] = "4" }.ToFrozenDictionary());
            Check(ImbueEvidence.Evaluate(Replace(Frame(null), other)).Count is null, "Opponent cumulative history became our history.");
        });
        yield return ("cumulative Imbue requires the audited build and complete stable own perspective", () =>
        {
            var frame = Frame(4);
            foreach (var changed in new[] { frame with { ClientBuild = "other" }, frame with { LocalPlayerId = null },
                frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { IsComplete = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { HasGameStart = false } },
                frame with { Quality = frame.Quality with { OpenBlockCount = 1 } },
                Replace(frame, Player(4) with { Tags = null! }) })
                Check(ImbueEvidence.Evaluate(changed).Count is null, "Unbound perspective/version supplied a historical count.");
        });
        yield return ("cumulative Imbue carries exact snapshot bindings and immutable source lists", () =>
        {
            var frame = Frame(4); var before = JsonSerializer.Serialize(frame); var evidence = ImbueEvidence.Evaluate(frame);
            Check(evidence.MatchId == frame.MatchId && evidence.StateVersion == frame.StateVersion && evidence.EventSequence == frame.EventSequence &&
                evidence.CardBuild == "255790" && evidence.PlayerId == 1 && before == JsonSerializer.Serialize(frame), "Counter binding/input changed.");
            try { ((IList<int>)evidence.SourceEntityIds).Clear(); throw new Exception("Mutable counter sources."); }
            catch (NotSupportedException) { }
            try { evidence.AtLeast(0); throw new Exception("Invalid threshold accepted."); }
            catch (ArgumentOutOfRangeException) { }
        });
        yield return ("Malorne below four cumulative Imbues grants no cost operation", () =>
        {
            foreach (var count in new[] { 0, 1, 2, 3 })
            {
                var result = MalorneChoiceRules.Evaluate(Frame(count), 60, cards);
                Check(result.ResolvesEffects && result.CardId == "EDR_480" && result.ImbueConditionMet == false &&
                    result.GrantedSetCost is null && result.ResultingCost is null && result.AddedToHand == true,
                    "An unsatisfied Malorne condition granted a discount/set cost or lost the selected card.");
            }
        });
        yield return ("Malorne at four cumulative Imbues grants set cost one without subtracting candidate cost", () =>
        {
            foreach (var observedCost in new[] { 0, 1, 7 })
            {
                var frame = Replace(Frame(4), Entity(60, "EDR_480", "SETASIDE", 1, ("COST", observedCost), ("CARDTYPE", 4)));
                var result = MalorneChoiceRules.Evaluate(frame, 60, cards);
                Check(result.ResolvesEffects && result.ImbueConditionMet == true && result.GrantedSetCost == 1 &&
                    result.ResultingCost is null && !result.CanEvaluateFollowupActions && result.Imbues.Count == 4,
                    "Malorne's set-to-one operation became minus four or a final executable cost.");
            }
        });
        yield return ("Malorne missing cumulative evidence preserves the offered card but not the cost condition", () =>
        {
            var result = MalorneChoiceRules.Evaluate(Frame(null), 60, cards);
            Check(result.ResolvesEffects && result.CardId == "EDR_480" && result.AddedToHand == true &&
                result.ImbueConditionMet is null && result.GrantedSetCost is null && result.ResultingCost is null,
                "Missing count became zero, four or a withheld direct selected-card consequence.");
        });
        yield return ("Malorne hand capacity has explicit full missing and inconsistent outcomes", () =>
        {
            Check(MalorneChoiceRules.Evaluate(Replace(Frame(4), Player(4, hand: 0, limit: 0)), 60, cards).AddedToHand == false,
                "Full hand gained a Malorne card.");
            Check(MalorneChoiceRules.Evaluate(Replace(Frame(4), Player(4, limit: null)), 60, cards).AddedToHand is null,
                "Missing hand limit defaulted to ten.");
            Check(MalorneChoiceRules.Evaluate(Replace(Frame(4), Player(4, hand: 2)), 60, cards).AddedToHand is null,
                "Inconsistent hand count was reused.");
        });
        yield return ("Malorne rejects foreign stale unpinned and mismatched source choices", () =>
        {
            var frame = Frame(4);
            foreach (var changed in new[] { frame with { ClientBuild = "other" }, frame with { IsSpectating = true },
                frame with { Choice = frame.Choice! with { SourceCardId = null } }, frame with { Choice = frame.Choice! with { SourceCardId = "BE_044" } },
                Replace(frame, Entity(30, "EDR_888", "HAND", 1)), Replace(frame, Entity(30, "EDR_888", "PLAY", 2)),
                Replace(frame, Entity(30, "BE_044", "PLAY", 1)), Replace(frame, Entity(30, "EDR_888", "PLAY", 1, ("CARDTYPE", 5))) })
                Check(!MalorneChoiceRules.Evaluate(changed, 60, cards).ResolvesEffects, "A source/choice mismatch still resolved Malorne.");
            Check(!MalorneChoiceRules.Evaluate(frame, 999, cards).ResolvesEffects, "A non-offered selection acquired a card.");
        });
        yield return ("Malorne validates exact reviewed Wild God identities rather than names sets or cost", () =>
        {
            var frame = Frame(4);
            foreach (var candidate in new[] { Entity(60, "EDR_489", "SETASIDE", 1), Entity(60, "EDR_480", "SETASIDE", 2),
                Entity(60, "EDR_480", "HAND", 1), Entity(60, "EDR_480", "SETASIDE", 1, ("CARDTYPE", 5)) })
                Check(!MalorneChoiceRules.Evaluate(Replace(frame, candidate), 60, cards).ResolvesEffects, "A conflicting current candidate was accepted.");
            var notGod = frame with { Choice = frame.Choice! with { Options = [new(60, "EDR_888", 1, 1)] } };
            Check(!MalorneChoiceRules.Evaluate(notGod, 60, cards).ResolvesEffects, "A similar Wild God name/legendary source became an audited pool member.");
            foreach (var id in new[] { "EDR_888", "EDR_480" })
            {
                var changed = cards.ToDictionary(); changed[id] = changed[id] with { DbfId = 999 };
                Check(!MalorneChoiceRules.Evaluate(frame, 60, changed).ResolvesEffects, "A mismatched fixed-build record resolved Malorne.");
            }
        });
        yield return ("Malorne projections freeze results and never reuse unidentified numeric candidate tags", () =>
        {
            var frame = Replace(Frame(4), Entity(60, "EDR_480", "SETASIDE", 1, ("COST", 0)) with { CardId = null });
            var before = JsonSerializer.Serialize(frame); var result = MalorneChoiceRules.Evaluate(frame, 60, cards);
            Check(result.ResolvesEffects && result.GrantedSetCost == 1 && result.ResultingCost is null && before == JsonSerializer.Serialize(frame),
                "An unidentified entity's old cost replaced the direct grant or changed input.");
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable Malorne unknowns."); }
            catch (NotSupportedException) { }
        });
    }

    private static GameSnapshot Frame(int? count)
    {
        var frame = new GameSnapshot("imbue-fixture", 10, 100, 5, 1, 1, GamePhase.Choice,
            new[] { Player(count), Entity(11, "HERO_05", "PLAY", 1, ("CARDTYPE", 3)),
                Entity(12, "EDR_850p", "PLAY", 1, ("CARDTYPE", 10)), Entity(30, "EDR_888", "PLAY", 1, ("CARDTYPE", 4)),
                Entity(60, "EDR_480", "SETASIDE", 1, ("CARDTYPE", 4)) }.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, []))
        {
            ClientBuild = "255790",
            Choice = new(7, 1, "GENERAL", 30, 1, 1, [new(60, "EDR_480", 1, 1)], true)
            { SourceCardId = "EDR_888", OfferedAtEventSequence = 90 }
        };
        return frame;
    }
    private static EntitySnapshot Player(int? count, int hand = 0, int? limit = 10, params (string Key, int Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["CONTROLLER"] = "1", ["HERO_ENTITY"] = "11", ["HERO_POWER_ENTITY"] = "12", ["NUM_CARDS_IN_HAND"] = hand.ToString() };
        if (limit.HasValue) tags["MAXHANDSIZE"] = limit.Value.ToString();
        if (count.HasValue) tags["3527"] = count.Value.ToString();
        foreach (var (key, value) in extra) tags[key] = value.ToString();
        return new(1, null, EntityKind.Player, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Entity(int id, string card, string zone, int owner, params (string Key, int Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString() };
        foreach (var (key, value) in extra) tags[key] = value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "imbue-malorne-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Unpinned Imbue fixture.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!,
            raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.GetProperty("collectible").GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
