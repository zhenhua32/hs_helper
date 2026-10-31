using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class DrawResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("draw two removes its played source before checking a full hand", () =>
        {
            var result = Eval(Frame(hand: 10, deck: 3), cards);
            Exact(result.RequestedDraws, 2); Exact(result.CardsRemovedFromDeck, 2); Exact(result.CardsAddedToHand, 1);
            Exact(result.CardsBurned, 1); Exact(result.HandCountAfter!, 10); Exact(result.DeckCountAfter!, 1);
            Check(result.ResolvesEffects && result.IsDirectEffectOnly && !result.CanEvaluateFollowupActions, "Direct preview became an authoritative playable hand.");
        });
        yield return ("missing observed hand limit produces bounds instead of a default cap", () =>
        {
            var result = Eval(Frame(hand: 10, deck: 3, maximum: null), cards);
            Bounds(result.CardsAddedToHand, 0, 2); Bounds(result.CardsBurned, 0, 2); Bounds(result.HandCountAfter!, 9, 11);
            Exact(result.CardsRemovedFromDeck, 2);
        });
        yield return ("Runtime overlays are already after source departure and are not decremented twice", () =>
        {
            var frame = Frame(hand: 10, deck: 3); var result = Eval(frame, cards, overlay: State(frame, hand: 10));
            Exact(result.CardsAddedToHand, 0); Exact(result.CardsBurned, 2); Exact(result.HandCountAfter!, 10);
        });
        yield return ("cumulative imbue false and unknown preserve zero or two direct draws", () =>
        {
            var frame = Frame(hand: 5, deck: 3);
            var no = Eval(frame, cards, condition: false); Exact(no.RequestedDraws, 0); Exact(no.CardsRemovedFromDeck, 0); Exact(no.HandCountAfter!, 4);
            var maybe = Eval(frame, cards, condition: null); Bounds(maybe.RequestedDraws, 0, 2); Bounds(maybe.CardsRemovedFromDeck, 0, 2); Bounds(maybe.HandCountAfter!, 4, 6);
        });
        yield return ("ordinary empty deck draw computes escalating fatigue and armor consumption", () =>
        {
            var result = Eval(Frame(deck: 0, fatigue: 2, health: 10, armor: 1), cards);
            Exact(result.CardsRemovedFromDeck, 0); Exact(result.EmptyDeckAttempts, 2); Exact(result.FatigueDamage, 7);
            Exact(result.FatigueAfter!, 4); Exact(result.HeroArmorAfter!, 0); Exact(result.HeroHealthAfter!, 4);
        });
        yield return ("the last actual deck card is removed before the next attempt fatigues", () =>
        {
            var result = Eval(Frame(hand: 3, deck: 1), cards);
            Exact(result.CardsRemovedFromDeck, 1); Exact(result.CardsAddedToHand, 1); Exact(result.EmptyDeckAttempts, 1);
            Exact(result.FatigueDamage, 1); Exact(result.DeckCountAfter!, 0); Exact(result.HandCountAfter!, 3);
        });
        yield return ("a directly lethal fatigue packet stops later draw attempts", () =>
        {
            var result = Eval(Frame(deck: 0, health: 1), cards);
            Exact(result.RequestedDraws, 2); Exact(result.EmptyDeckAttempts, 1); Exact(result.FatigueDamage, 1);
            Exact(result.FatigueAfter!, 1); Exact(result.HeroHealthAfter!, 0); Check(result.MayStopAtHeroDeath, "Lethal direct draw was not identified as a stopping boundary.");
            var armored = Eval(Frame(deck: 0, health: 1, armor: 2), cards);
            Exact(armored.EmptyDeckAttempts, 2); Exact(armored.FatigueDamage, 3); Exact(armored.HeroHealthAfter!, 0);
        });
        yield return ("observed hero immunity blocks damage while fatigue still advances", () =>
        {
            var frame = Replace(Frame(deck: 0, fatigue: 5, health: 1), Hero(10, 1, 1, 0, ("IMMUNE", 1)));
            var result = Eval(frame, cards); Exact(result.FatigueDamage, 0); Exact(result.FatigueAfter!, 7);
            Exact(result.EmptyDeckAttempts, 2); Exact(result.HeroHealthAfter!, 1); Check(!result.MayStopAtHeroDeath, "Immune hero was killed by direct fatigue.");
        });
        yield return ("unknown prior fatigue stays unbounded and permits an early death branch", () =>
        {
            var result = Eval(Frame(deck: 0, fatigue: null, health: 2), cards);
            Bounds(result.EmptyDeckAttempts, 1, 2); Bounds(result.FatigueDamage, 2, null); Bounds(result.FatigueAfter!, 2, null);
            Exact(result.HeroHealthAfter!, 0);
        });
        yield return ("unknown hero health cannot force all nominal fatigue packets to resolve", () =>
        {
            var result = Eval(Frame(deck: 0, health: null), cards);
            Bounds(result.EmptyDeckAttempts, 1, 2); Bounds(result.FatigueDamage, 1, 3);
            Check(result.HeroHealthAfter is null, "Missing hero health was replaced by a printed or zero value.");
        });
        yield return ("an unidentified hero cannot be assumed to lack immunity or fatigue prevention", () =>
        {
            var frame = Frame(deck: 0); var entities = frame.Entities.ToDictionary(); entities.Remove(10);
            var missingHero = frame with { Entities = entities.ToFrozenDictionary() }; var result = Eval(missingHero, cards);
            Bounds(result.FatigueDamage, 0, 3); Bounds(result.FatigueAfter!, 0, 2); Bounds(result.EmptyDeckAttempts, 1, 2);
            Check(result.HeroHealthAfter is null && result.HeroArmorAfter is null, "An absent hero received invented final attributes.");
            var playerTags = missingHero.Entities[1].Tags.ToDictionary(); playerTags["CANT_BE_FATIGUED"] = "1";
            var prevented = Eval(Replace(missingHero, missingHero.Entities[1] with { Tags = playerTags.ToFrozenDictionary() }), cards);
            Exact(prevented.FatigueDamage, 0); Exact(prevented.FatigueAfter!, 0); Exact(prevented.EmptyDeckAttempts, 2);
        });
        yield return ("matched beast composition confirms one draw without exposing an identity or cost", () =>
        {
            var frame = Track(Frame("EDR_226", deck: 3), new Dictionary<string, int> { ["PRIVATE_BEAST"] = 2, ["PRIVATE_SPELL"] = 1 });
            var result = Eval(frame, cards); Exact(result.CardsRemovedFromDeck, 1); Exact(result.CardsAddedToHand, 1); Exact(result.EmptyDeckAttempts, 0);
            Check(!JsonSerializer.Serialize(result).Contains("PRIVATE_", StringComparison.Ordinal), "Deck identity escaped into the direct count projection.");
        });
        yield return ("known nonempty beastless deck is left intact by a targeted draw", () =>
        {
            var result = Eval(Track(Frame("EDR_226", deck: 2), new Dictionary<string, int> { ["PRIVATE_SPELL"] = 2 }), cards);
            Exact(result.CardsRemovedFromDeck, 0); Exact(result.CardsAddedToHand, 0); Exact(result.DeckCountAfter!, 2); Exact(result.EmptyDeckAttempts, 0);
        });
        yield return ("a guaranteed beast lower bound is enough despite other unknown deck cards", () =>
        {
            var frame = Track(Frame("EDR_226", deck: 3), new Dictionary<string, int> { ["PRIVATE_BEAST"] = 1 }, unknown: 2, exact: false);
            Exact(Eval(frame, cards).CardsRemovedFromDeck, 1);
            var unknown = Track(Frame("EDR_226", deck: 3), new Dictionary<string, int> { ["PRIVATE_SPELL"] = 1 }, unknown: 2, exact: false);
            Bounds(Eval(unknown, cards).CardsRemovedFromDeck, 0, 1);
        });
        yield return ("stale future or mismatched deck ledgers cannot guarantee a targeted draw", () =>
        {
            var frame = Track(Frame("EDR_226", deck: 2), new Dictionary<string, int> { ["PRIVATE_BEAST"] = 2 }); var tracking = frame.DeckTracking!;
            foreach (var invalid in new[] { tracking with { MatchId = "other" }, tracking with { StateVersion = 11 }, tracking with { CardBuild = "future" },
                tracking with { UnknownCount = 1 }, tracking with { KnownRemaining = new Dictionary<string, int> { ["PRIVATE_BEAST"] = -1 } } })
                Bounds(Eval(frame with { DeckTracking = invalid }, cards).CardsRemovedFromDeck, 0, 1);
        });
        yield return ("missing beast classification stays uncertain even with an exact identity ledger", () =>
        {
            var frame = Track(Frame("EDR_226", deck: 2), new Dictionary<string, int> { ["UNKNOWN_PROFILE"] = 2 });
            Bounds(Eval(frame, cards).CardsRemovedFromDeck, 0, 1);
        });
        yield return ("targeted empty deck fallback is explicit rather than falsely confirmed fatigue immunity", () =>
        {
            var result = Eval(Track(Frame("EDR_226", deck: 0, fatigue: 3), new Dictionary<string, int>()), cards);
            Exact(result.CardsRemovedFromDeck, 0); Bounds(result.EmptyDeckAttempts, 0, 1); Bounds(result.FatigueDamage, 0, 4); Bounds(result.FatigueAfter!, 3, 4);
            Check(result.Unknowns.Any(value => value.Contains("定向", StringComparison.Ordinal)), "Unproven fixed-client empty-deck behavior was hidden.");
        });
        yield return ("draw source frame build perspective and overlay bindings are required", () =>
        {
            var frame = Frame();
            foreach (var invalid in new[] { frame with { ClientBuild = "newer" }, frame with { IsSpectating = true }, frame with { ActivePlayerId = 2 },
                frame with { Quality = frame.Quality with { IsStable = false } }, Replace(frame, Source("FIR_921", owner: 2)),
                Replace(frame, Source("FIR_921", zone: "PLAY")), Replace(frame, Source("FIR_921") with { Tags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = "HAND", ["CARDTYPE"] = "SPELL" }.ToFrozenDictionary() }) })
                Check(!Eval(invalid, cards).ResolvesEffects, "Unsafe draw source was expanded.");
            Check(!Eval(frame, cards, overlay: State(frame) with { EventSequence = 101 }).ResolvesEffects, "Future count overlay was accepted.");
            var badCards = cards.ToDictionary(); badCards["FIR_921"] = cards["FIR_921"] with { DbfId = 1 };
            Check(!Eval(frame, badCards).ResolvesEffects, "Wrong fixed source profile was used.");
        });
        yield return ("inconsistent observed hand and deck counts cannot become exact future counts", () =>
        {
            var frame = Frame(deck: 3); var player = frame.Entities[1]; var tags = player.Tags.ToDictionary(); tags["NUM_CARDS_IN_HAND"] = "99"; tags["NUM_CARDS_IN_DECK"] = "99";
            var result = Eval(Replace(frame, player with { Tags = tags.ToFrozenDictionary() }), cards);
            Check(result.HandCountAfter is null && result.DeckCountAfter is { Minimum: 0, Maximum: null }, "Conflicting counts were trusted.");
            Bounds(result.CardsRemovedFromDeck, 0, 2);
        });
        yield return ("earlier unresolved deck changes disable reuse of original beast composition", () =>
        {
            var frame = Track(Frame("EDR_226", deck: 3), new Dictionary<string, int> { ["PRIVATE_BEAST"] = 3 });
            var result = Eval(frame, cards, overlay: State(frame) with { DeckCompositionStillMatchesSnapshot = false });
            Bounds(result.CardsRemovedFromDeck, 0, 1);
        });
        yield return ("silenced or explicitly disabled battlecry does not draw", () =>
        {
            foreach (var flag in new[] { ("SILENCED", 1), ("BATTLECRY", 0) })
            {
                var frame = Replace(Frame(), Source("FIR_921", flags: [flag]));
                Exact(Eval(frame, cards).RequestedDraws, 0);
            }
        });
        yield return ("draw prevention leaves deck and fatigue unchanged", () =>
        {
            var frame = Frame(deck: 0, fatigue: 4); var tags = frame.Entities[1].Tags.ToDictionary(); tags["CANT_DRAW"] = "1";
            var result = Eval(Replace(frame, frame.Entities[1] with { Tags = tags.ToFrozenDictionary() }), cards);
            Exact(result.RequestedDraws, 2); Exact(result.CardsRemovedFromDeck, 0); Exact(result.EmptyDeckAttempts, 0); Exact(result.FatigueAfter!, 4);
        });
        yield return ("numeric draw prevention and hand size tags retain their protocol meaning", () =>
        {
            var frame = Frame(hand: 10, deck: 3, maximum: null); var tags = frame.Entities[1].Tags.ToDictionary(); tags["28"] = "10";
            var numeric = Replace(frame, frame.Entities[1] with { Tags = tags.ToFrozenDictionary() }); Exact(Eval(numeric, cards).CardsBurned, 1);
            tags["232"] = "1";
            var prevented = Eval(Replace(frame, frame.Entities[1] with { Tags = tags.ToFrozenDictionary() }), cards);
            Exact(prevented.CardsRemovedFromDeck, 0); Exact(prevented.CardsBurned, 0);
        });
        yield return ("observed fatigue prevention differs from damage immunity", () =>
        {
            foreach (var tag in new[] { "CANT_BE_FATIGUED", "456" })
            {
                var frame = Replace(Frame(deck: 0, fatigue: 4, health: 1), Hero(10, 1, 1, 0, (tag, 1)));
                var result = Eval(frame, cards); Exact(result.EmptyDeckAttempts, 2); Exact(result.FatigueAfter!, 4); Exact(result.FatigueDamage, 0);
                Exact(result.HeroHealthAfter!, 1);
            }
        });
        yield return ("malformed observed silence and conflicting immunity flags stay conditional", () =>
        {
            var frame = Frame(deck: 0, health: 1); var sourceTags = frame.Entities[30].Tags.ToDictionary(); sourceTags["SILENCED"] = "unknown";
            Bounds(Eval(Replace(frame, frame.Entities[30] with { Tags = sourceTags.ToFrozenDictionary() }), cards).RequestedDraws, 0, 2);
            var heroTags = frame.Entities[10].Tags.ToDictionary(); heroTags["IMMUNE"] = "1"; heroTags["240"] = "0";
            var result = Eval(Replace(frame, frame.Entities[10] with { Tags = heroTags.ToFrozenDictionary() }), cards);
            Bounds(result.FatigueDamage, 0, 1); Bounds(result.HeroHealthAfter!, 0, 1);
        });
        yield return ("an explicitly unknown Runtime immunity overlay preserves both fatigue branches", () =>
        {
            var frame = Frame(deck: 0, health: 1);
            var result = Eval(frame, cards, overlay: State(frame) with { HeroImmune = null });
            Bounds(result.FatigueDamage, 0, 1); Bounds(result.HeroHealthAfter!, 0, 1);
            Bounds(result.EmptyDeckAttempts, 1, 2); Bounds(result.FatigueAfter!, 1, 2);
            Check(result.MayStopAtHeroDeath, "Unknown overlay immunity was treated as guaranteed immunity.");
        });
        yield return ("fatigue arithmetic uses wide values instead of wrapping Int32", () =>
        {
            var result = Eval(Frame(deck: 0, fatigue: int.MaxValue, health: int.MaxValue, armor: int.MaxValue), cards);
            Exact(result.EmptyDeckAttempts, 2); Exact(result.FatigueDamage, 2L * int.MaxValue + 3); Exact(result.FatigueAfter!, (long)int.MaxValue + 2);
            Exact(result.HeroHealthAfter!, 0);
        });
        yield return ("numeric fatigue tags work and conflicting aliases remain unknown", () =>
        {
            var frame = Frame(deck: 0, fatigue: null); var tags = frame.Entities[1].Tags.ToDictionary(); tags["22"] = "2";
            var numeric = Replace(frame, frame.Entities[1] with { Tags = tags.ToFrozenDictionary() }); Exact(Eval(numeric, cards).FatigueDamage, 7);
            tags["FATIGUE"] = "3";
            var conflict = Eval(Replace(frame, frame.Entities[1] with { Tags = tags.ToFrozenDictionary() }), cards);
            Check(conflict.FatigueDamage.Maximum is null, "Conflicting fatigue counters selected a convenient alias.");
        });
        yield return ("draw result collections are frozen and preview leaves the input untouched", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame, cards);
            Check(before == JsonSerializer.Serialize(frame), "Direct draw preview mutated its observed source.");
            foreach (var values in new[] { result.Unknowns, result.Assumptions, result.RuleCoverage })
                try { ((IList<string>)values).Add("changed"); throw new Exception("Projection list remained writable."); } catch (NotSupportedException) { }
        });
    }

    private static DrawResolutionOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, bool? condition = true, DrawResolutionState? overlay = null) =>
        DrawResolutionRules.Evaluate(frame, 30, cards, condition, overlay);
    private static DrawResolutionState State(GameSnapshot frame, int? hand = null) => new(frame.MatchId, frame.StateVersion, frame.EventSequence,
        hand ?? frame.Hand.Count() - 1, 10, frame.Entities.Values.Count(entity => entity.ControllerId == 1 && entity.Zone == "DECK"),
        frame.Entities[1].GetIntTag("FATIGUE"), frame.Entities[10].RemainingHealth, frame.Entities[10].GetIntTag("ARMOR"));
    private static GameSnapshot Frame(string sourceId = "FIR_921", int hand = 1, int deck = 3, int? maximum = 10, int? fatigue = 0, int? health = 30, int armor = 0)
    {
        var playerTags = new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["NUM_CARDS_IN_HAND"] = hand.ToString(), ["NUM_CARDS_IN_DECK"] = deck.ToString(), ["HERO_ENTITY"] = "10" };
        if (maximum is { } max) playerTags["MAXHANDSIZE"] = max.ToString(); if (fatigue is { } f) playerTags["FATIGUE"] = f.ToString();
        var entities = new List<EntitySnapshot> { new(1, null, EntityKind.Player, playerTags.ToFrozenDictionary()),
            new(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "11" }.ToFrozenDictionary()),
            Hero(10, 1, health, armor), Hero(11, 2, 30, 0), Source(sourceId) };
        for (var i = 1; i < hand; i++) entities.Add(Entity(30 + i, "OTHER_HAND", "HAND", 1));
        for (var i = 0; i < deck; i++) entities.Add(Entity(1000 + i, null, "DECK", 1));
        return new("draw-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Source(string id, string zone = "HAND", int owner = 1, params (string, int)[] flags) => Entity(30, id, zone, owner, [("COST", 77), .. flags]);
    private static EntitySnapshot Hero(int id, int owner, int? health, int armor, params (string, int)[] flags)
    {
        var tags = Entity(id, "HERO_05", "PLAY", owner, [("ARMOR", armor), .. flags]).Tags.ToDictionary(); tags["CARDTYPE"] = "HERO";
        if (health is { } h) { tags["HEALTH"] = h.ToString(); tags["DAMAGE"] = "0"; }
        return new(id, "HERO_05", EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Entity(int id, string? card, string zone, int owner, params (string, int)[] flags)
    {
        var tags = new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION" };
        foreach (var (tag, value) in flags) tags[tag] = value.ToString(); return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot Track(GameSnapshot frame, IReadOnlyDictionary<string, int> counts, int unknown = 0, bool exact = true) => frame with
    { DeckTracking = new(frame.MatchId, frame.StateVersion, "deck-version", "255790", frame.Entities[1].GetIntTag("NUM_CARDS_IN_DECK"), counts, unknown, exact, []) };
    private static Dictionary<string, CardRecord> Cards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "beast-hunter-255790.json")));
        var result = new Dictionary<string, CardRecord>(StringComparer.Ordinal);
        foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray().Where(raw => raw.GetProperty("id").GetString() is "FIR_921" or "EDR_226"))
        {
            var id = raw.GetProperty("id").GetString()!; result[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!, raw.GetProperty("cost").GetInt32(), true, [], [], raw.GetRawText());
        }
        result["PRIVATE_BEAST"] = new("PRIVATE_BEAST", 500, "Private Beast", "", "NEUTRAL", "TEST", "MINION", 99, false, [], [], "{\"id\":\"PRIVATE_BEAST\",\"race\":\"BEAST\"}");
        result["PRIVATE_SPELL"] = new("PRIVATE_SPELL", 501, "Private Spell", "", "NEUTRAL", "TEST", "SPELL", 99, false, [], [], "{}");
        return result;
    }
    private static void Exact(DrawNumberRange range, long value) => Bounds(range, value, value);
    private static void Bounds(DrawNumberRange range, long min, long? max) => Check(range.Minimum == min && range.Maximum == max, $"Expected [{min},{max}], got [{range.Minimum},{range.Maximum}].");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
