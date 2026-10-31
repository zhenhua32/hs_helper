using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class GenerationResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("native Macaw generation previews one Beast without inventing a card identity or cost", () =>
        {
            var result = Eval(Frame(), cards);
            Check(result.ResolvesEffects && result.IsDirectEffectOnly && !result.RequiresChoice && !result.CanEvaluateFollowupActions &&
                result.Kind == "RandomBeastToHand" && result.SourceEntityId == 30 && result.SourceCardId == "CORE_UNG_912",
                "Native generation lost its scope or became a next actionable hand.");
            Exact(result.RequestedCards, 1); Exact(result.CardsAddedToHand, 1); Exact(result.CardsLostToHandLimit, 0); Exact(result.HandCountAfter!, 5);
            Check(result.Unknowns.Any(value => value.Contains("最终费用", StringComparison.Ordinal)), "Generated current cost was treated as already observed.");
        });
        yield return ("a full observed hand frees the played Macaw slot before the generated card arrives", () =>
        {
            var result = Eval(Frame(hand: 10), cards);
            Exact(result.CardsAddedToHand, 1); Exact(result.CardsLostToHandLimit, 0); Exact(result.HandCountAfter!, 10);
        });
        yield return ("generation overlays are already after departure and preserve full-hand loss", () =>
        {
            var frame = Frame(hand: 10); var result = Eval(frame, cards, Overlay(frame, 10, 10));
            Exact(result.RequestedCards, 1); Exact(result.CardsAddedToHand, 0); Exact(result.CardsLostToHandLimit, 1); Exact(result.HandCountAfter!, 10);
            var departed = Replace(frame, frame.Entities[30] with { Tags = frame.Entities[30].Tags.SetItemForTest("ZONE", "PLAY") });
            var after = Eval(departed, cards, Overlay(departed, 9, 10)); Exact(after.CardsAddedToHand, 1); Exact(after.HandCountAfter!, 10);
            Check(!Eval(departed, cards).ResolvesEffects, "An old board Macaw re-triggered without an after-play overlay.");
        });
        yield return ("a zero observed hand cap loses the native generated card without a fabricated destination", () =>
        {
            var result = Eval(Frame(hand: 1, maximum: 0), cards);
            Exact(result.CardsAddedToHand, 0); Exact(result.CardsLostToHandLimit, 1); Exact(result.HandCountAfter!, 0);
            Check(result.Assumptions.Any(value => value.Contains("最终区域", StringComparison.Ordinal)), "The lost card acquired an invented zone/entity.");
        });
        yield return ("unknown generation hand limit uses correlated zero-to-one bounds without defaulting to ten", () =>
        {
            var result = Eval(Frame(hand: 10, maximum: null), cards);
            Bounds(result.CardsAddedToHand, 0, 1); Bounds(result.CardsLostToHandLimit, 0, 1); Bounds(result.HandCountAfter!, 9, 10);
        });
        yield return ("conflicting or malformed observed generation hand counts stay unknown", () =>
        {
            foreach (var value in new[] { "0", "-1", "broken" })
            {
                var frame = Tag(Frame(), 1, "NUM_CARDS_IN_HAND", value); var result = Eval(frame, cards);
                Check(result.ResolvesEffects && result.HandCountAfter is null, "Conflicting hand count became exact.");
                Exact(result.RequestedCards, 1); Bounds(result.CardsAddedToHand, 0, 1); Bounds(result.CardsLostToHandLimit, 0, 1);
            }
        });
        yield return ("generation hand-cap aliases must agree and malformed values do not become a fixed cap", () =>
        {
            var frame = Tag(Frame(hand: 10), 1, "28", "10"); Exact(Eval(frame, cards).CardsAddedToHand, 1);
            foreach (var value in new[] { "9", "-1", "bad" })
            {
                var result = Eval(Tag(frame, 1, "28", value), cards);
                Bounds(result.CardsAddedToHand, 0, 1); Bounds(result.CardsLostToHandLimit, 0, 1);
            }
            frame = RemoveTag(Tag(Frame(), 1, "28", "10"), 1, "MAXHANDSIZE");
            Exact(Eval(frame, cards).CardsAddedToHand, 1);
        });
        yield return ("silenced or explicitly suppressed Macaw battlecries request zero native cards", () =>
        {
            foreach (var frame in new[] { Tag(Frame(), 30, "SILENCED", "1"), Tag(Frame(), 30, "188", "1"),
                Tag(Frame(), 30, "BATTLECRY", "0"), Tag(Frame(), 30, "218", "0") })
            {
                var result = Eval(frame, cards); Check(result.ResolvesEffects, "A confirmed zero effect became unknown.");
                Exact(result.RequestedCards, 0); Exact(result.CardsAddedToHand, 0); Exact(result.CardsLostToHandLimit, 0); Exact(result.HandCountAfter!, 4);
            }
        });
        yield return ("ambiguous silence or battlecry tags retain zero-or-one native generation branches", () =>
        {
            foreach (var frame in new[] { Tag(Frame(), 30, "SILENCED", "-1"), Tag(Frame(), 30, "BATTLECRY", "bad"),
                Tag(Tag(Frame(), 30, "SILENCED", "0"), 30, "188", "1"), Tag(Tag(Frame(), 30, "BATTLECRY", "1"), 30, "218", "0") })
            {
                var result = Eval(frame, cards); Bounds(result.RequestedCards, 0, 1); Bounds(result.CardsAddedToHand, 0, 1); Bounds(result.HandCountAfter!, 4, 5);
                var full = Eval(frame, cards, Overlay(frame, 10, 10)); Exact(full.CardsAddedToHand, 0); Bounds(full.CardsLostToHandLimit, 0, 1); Exact(full.HandCountAfter!, 10);
            }
        });
        yield return ("generation overlays require the exact snapshot match version and event sequence", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame, 10, 10);
            foreach (var value in new[] { overlay with { MatchId = "PRIVATE_OTHER_MATCH" }, overlay with { StateVersion = 11 }, overlay with { EventSequence = 101 } })
            {
                var result = Eval(frame, cards, value); Check(!result.ResolvesEffects && result.HandCountAfter is null &&
                    !JsonSerializer.Serialize(result).Contains("PRIVATE_OTHER_MATCH", StringComparison.Ordinal), "Unbound counters were used or echoed.");
            }
        });
        yield return ("negative and missing generation overlay counters remain unknown without old-snapshot fallback", () =>
        {
            var frame = Frame();
            foreach (var count in new int?[] { null, -1 })
            {
                var result = Eval(frame, cards, Overlay(frame, count, 10)); Check(result.HandCountAfter is null, "Unknown overlay hand count reused the old observed count.");
                Bounds(result.CardsAddedToHand, 0, 1);
                result = Eval(frame, cards, Overlay(frame, 3, count)); Bounds(result.HandCountAfter!, 3, 4); Bounds(result.CardsLostToHandLimit, 0, 1);
            }
        });
        yield return ("generation arithmetic safely represents an unknown-cap overlay above Int32 hand count", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, Overlay(frame, int.MaxValue, null));
            Bounds(result.HandCountAfter!, int.MaxValue, (long)int.MaxValue + 1);
            result = Eval(frame, cards, Overlay(frame, int.MaxValue, int.MaxValue)); Exact(result.CardsAddedToHand, 0); Exact(result.CardsLostToHandLimit, 1);
        });
        yield return ("random generation rejects wrong perspectives phases incomplete snapshots and malformed entities", () =>
        {
            var frame = Frame(); var invalid = new[]
            {
                frame with { ClientBuild = "255791" }, frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
                frame with { ActivePlayerId = 2 }, frame with { Phase = GamePhase.Choice },
                frame with { Quality = frame.Quality with { HasGameStart = false } }, frame with { Quality = frame.Quality with { IsComplete = false } },
                frame with { Quality = frame.Quality with { IsStable = false } }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
                frame with { Quality = frame.Quality with { OpenBlockCount = 1 } },
                Replace(frame, new(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary())),
                Replace(frame, frame.Entities[30] with { Tags = null! }),
                frame with { Entities = frame.Entities.ToDictionary().Append(new KeyValuePair<int,EntitySnapshot>(99, frame.Entities[30])).ToFrozenDictionary() }
            };
            foreach (var value in invalid) Check(!Eval(value, cards).ResolvesEffects, "Incomplete/foreign/malformed evidence resolved generation.");
        });
        yield return ("generation source must be the exact owned audited Macaw minion rather than same-name or old-zone entities", () =>
        {
            var frame = Frame();
            foreach (var source in new[] { Entity(30, "BE_044", "HAND", 1), Entity(30, "UNG_912", "HAND", 1),
                Entity(30, "CORE_UNG_912", "HAND", 2), Entity(30, "CORE_UNG_912", "GRAVEYARD", 1),
                Entity(30, "CORE_UNG_912", "DECK", 1), frame.Entities[30] with { Kind = EntityKind.Player } })
                Check(!Eval(Replace(frame, source), cards).ResolvesEffects, "A different/unplayed/opposing source supplied Macaw's generation.");
            foreach (var tag in new[] { "CARDTYPE", "202" })
                Check(!Eval(Tag(frame, 30, tag, "5"), cards).ResolvesEffects, "An observed spell type was overwritten by the printed minion type.");
            Check(Eval(Tag(Tag(frame, 30, "CARDTYPE", "MINION"), 30, "202", "4"), cards).ResolvesEffects,
                "Equivalent minion type aliases rejected an audited source.");
        });
        yield return ("generation rejects missing inconsistent or malformed fixed source catalog evidence", () =>
        {
            var frame = Frame();
            foreach (var source in new[] { cards["CORE_UNG_912"] with { DbfId = 41353 }, cards["CORE_UNG_912"] with { Id = "OTHER" },
                cards["CORE_UNG_912"] with { Type = "SPELL" }, cards["CORE_UNG_912"] with { Text = "生成九张" },
                cards["CORE_UNG_912"] with { RawJson = "[]" }, cards["CORE_UNG_912"] with { RawJson = "broken" } })
            { var changed = cards.ToDictionary(); changed["CORE_UNG_912"] = source; Check(!Eval(frame, changed).ResolvesEffects, "Inconsistent source definitions were treated as a fixed native effect."); }
            var missing = cards.ToDictionary(); missing.Remove("CORE_UNG_912"); Check(!Eval(frame, missing).ResolvesEffects, "Missing source definition was filled by card name.");
        });
        yield return ("assembled finished offer malformed and conflicting source halves block native random generation", () =>
        {
            var frame = Frame();
            foreach (var value in new[] { "1", "97376", "-1", "bad" })
            foreach (var tag in new[] { "CRAFTABLE_FIRST_HALF_DBID", "4572", "CRAFTABLE_SECOND_HALF_DBID", "4573" })
                Check(!Eval(Tag(frame, 30, tag, value), cards).ResolvesEffects, "Assembled/offer/malformed source text reused the native Macaw Battlecry.");
            Check(!Eval(Tag(Tag(frame, 30, "4572", "0"), 30, "CRAFTABLE_FIRST_HALF_DBID", "1"), cards).ResolvesEffects, "Conflicting custom aliases reused the native effect.");
            Check(Eval(Tag(Tag(frame, 30, "4572", "0"), 30, "4573", "0"), cards).ResolvesEffects, "Explicit native zero halves suppressed the audited native Battlecry.");
        });
        yield return ("generation ignores draw blocking empty decks and fatigue and never reads hidden pool identities", () =>
        {
            var frame = Tag(Tag(Tag(Frame(), 1, "CANT_DRAW", "1"), 1, "NUM_CARDS_IN_DECK", "0"), 1, "FATIGUE", "99999");
            frame = Replace(frame, Entity(90, "PRIVATE_DECK_GENERATION_PROBE", "DECK", 1));
            frame = Replace(frame, Entity(91, "PRIVATE_OPPONENT_PROBE", "HAND", 2));
            frame = frame with { DeckTracking = new(frame.MatchId, frame.StateVersion, "deck", "255790", 1,
                new Dictionary<string,int> { ["PRIVATE_DECK_GENERATION_PROBE"] = 1 }, 0, true, []) };
            var result = Eval(frame, cards); Exact(result.RequestedCards, 1); Exact(result.CardsAddedToHand, 1);
            var json = JsonSerializer.Serialize(result);
            Check(!json.Contains("PRIVATE_", StringComparison.Ordinal) && !json.Contains("99999", StringComparison.Ordinal), "Generation used a hidden deck/hand identity or fatigue as a random result.");
        });
        yield return ("generation has no variable script count and keeps the authoritative input and result lists frozen", () =>
        {
            var frame = Tag(Tag(Frame(), 30, "TAG_SCRIPT_DATA_NUM_1", "9"), 30, "COST", "0");
            var before = JsonSerializer.Serialize(frame); var cardBefore = JsonSerializer.Serialize(cards); var result = Eval(frame, cards);
            Exact(result.RequestedCards, 1); Check(before == JsonSerializer.Serialize(frame) && cardBefore == JsonSerializer.Serialize(cards), "Generation changed the input snapshot/catalog or guessed a script count.");
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable generation unknowns."); } catch (NotSupportedException) { }
            try { ((IList<string>)result.Assumptions).Clear(); throw new Exception("Mutable generation assumptions."); } catch (NotSupportedException) { }
            try { ((IList<string>)result.RuleCoverage).Clear(); throw new Exception("Mutable generation coverage."); } catch (NotSupportedException) { }
        });
    }

    private static GenerationResolutionOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards,
        GenerationResolutionState? overlay = null) => GenerationResolutionRules.Evaluate(frame, 30, cards, overlay);
    private static GenerationResolutionState Overlay(GameSnapshot frame, int? hand, int? max) =>
        new(frame.MatchId, frame.StateVersion, frame.EventSequence, hand, max);
    private static GameSnapshot Frame(int hand = 5, int? maximum = 10)
    {
        var player = new Dictionary<string,string> { ["PLAYER_ID"] = "1", ["NUM_CARDS_IN_HAND"] = hand.ToString() };
        if (maximum.HasValue) player["MAXHANDSIZE"] = maximum.Value.ToString();
        var entities = new[] { new EntitySnapshot(1, null, EntityKind.Player, player.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Entity(30, "CORE_UNG_912", "HAND", 1) }.Concat(Enumerable.Range(0, hand - 1).Select(index => Entity(40 + index, "UNOBSERVED_HAND_" + index, "HAND", 1)));
        return new("generation-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Entity(int id, string card, string zone, int owner) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString() }.ToFrozenDictionary());
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int entity, string key, string value)
    { var tags = frame.Entities[entity].Tags.ToDictionary(); tags[key] = value; return Replace(frame, frame.Entities[entity] with { Tags = tags.ToFrozenDictionary() }); }
    private static GameSnapshot RemoveTag(GameSnapshot frame, int entity, string key)
    { var tags = frame.Entities[entity].Tags.ToDictionary(); tags.Remove(key); return Replace(frame, frame.Entities[entity] with { Tags = tags.ToFrozenDictionary() }); }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "generation-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Generation fixture build changed.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!,
            raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Exact(GenerationNumberRange range, long value) => Bounds(range, value, value);
    private static void Bounds(GenerationNumberRange range, long minimum, long? maximum) => Check(range.Minimum == minimum && range.Maximum == maximum,
        $"Generation bounds expected {minimum}..{maximum}; got {range.Minimum}..{range.Maximum}.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

file static class GenerationTestTags
{
    public static FrozenDictionary<string,string> SetItemForTest(this FrozenDictionary<string,string> source, string key, string value)
    { var tags = source.ToDictionary(); tags[key] = value; return tags.ToFrozenDictionary(); }
}
