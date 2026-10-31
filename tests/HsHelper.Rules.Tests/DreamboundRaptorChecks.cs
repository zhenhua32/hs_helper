using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class DreamboundRaptorChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Dreambound confirmed native spells and weapons produce exact zero without a random-resolution boundary", () =>
        {
            foreach (var (id, type, alias) in new[] { ("GAME_005", "SPELL", "5"), ("BE_349", "WEAPON", "7") })
            {
                var frame = Replace(Frame(), Tag(Tag(Entity(30, id, "HAND"), "CARDTYPE", type), "202", alias));
                var result = Eval(frame, cards); Exact(result.TriggerCount, 0);
                Check(result.ResolvesTriggerCount && !result.RequiresObservation && result.Triggers.Count == 0 && !result.CanEvaluateFollowupActions,
                    "An audited non-minion received a minion after-play boundary.");
                Reject(Eval(Tag(frame, 30, "202", "4"), cards));
                Reject(Eval(Tag(frame, 30, "CARDTYPE", "bad"), cards));
                Reject(Eval(Tag(frame, 30, "4572", "1"), cards));
                var changed = cards.ToDictionary(); changed[id] = changed[id] with { RawJson = "[]" }; Reject(Eval(frame, changed));
            }
        });
        yield return ("Dreambound confirms one existing native source after successful minion play without assigning a random effect", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, Overlay(frame));
            Exact(result.TriggerCount, 1); Check(result.ResolvesTriggerCount && result.RequiresObservation && !result.CanEvaluateFollowupActions &&
                !result.IsBonusPoolComplete && result.TargetEntityId == 30 && result.TargetCardId == "EDR_852", "Trigger boundary lost its scope.");
            var trigger = result.Triggers.Single(); Check(trigger.SourceEntityId == 40 && trigger.TargetEntityId == 30 &&
                trigger.Eligibility == "EligibleNativeAfterPlay" && trigger.EligibleBonusEffects.Count == 0 && trigger.GuaranteedBonusEffect is null,
                "The source/target binding or unknown pool became an invented keyword.");
        });
        yield return ("Dreambound adds one request per independently confirmed source without ordering them", () =>
        {
            var frame = Replace(Frame(), Entity(41, "EDR_849", "PLAY")); var result = Eval(frame, cards, Overlay(frame));
            Exact(result.TriggerCount, 2); Check(result.Triggers.Select(trigger => trigger.SourceEntityId).Order().SequenceEqual(new[] { 40, 41 }) &&
                result.Unknowns.Any(value => value.Contains("结算顺序", StringComparison.Ordinal)), "Multiple sources were collapsed or given a fabricated order.");
        });
        yield return ("Dreambound no-overlay preview waits for actual after-play survival and does not manufacture a board", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards); Bounds(result.TriggerCount, 0, 1);
            Check(!result.ResolvesTriggerCount && result.RequiresObservation, "A hand-only preview claimed actual after-play survival.");
            var suppressed = Tag(frame, 40, "SILENCED", "1"); Bounds(Eval(suppressed, cards).TriggerCount, 0, 1);
        });
        yield return ("Dreambound suppresses observed silenced and dormant sources at the after-play boundary", () =>
        {
            foreach (var (key, value) in new[] { ("SILENCED", "1"), ("188", "1"), ("DORMANT", "1"), ("1518", "1") })
            {
                var frame = Frame(); var overlay = Overlay(frame); var end = Tag(overlay.SourcesAfterPlay[0], key, value);
                var result = Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { end } }); Exact(result.TriggerCount, 0);
                Check(result.ResolvesTriggerCount && !result.RequiresObservation, "An observed inactive source remained an active native grant.");
            }
        });
        yield return ("Dreambound removed or dead sources cannot grant after the preceding battlecry", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var end in new[] { Tag(overlay.SourcesAfterPlay[0], "ZONE", "GRAVEYARD"), Tag(overlay.SourcesAfterPlay[0], "DAMAGE", "1"),
                Tag(overlay.SourcesAfterPlay[0], "HEALTH", "0") })
                Exact(Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { end } }).TriggerCount, 0);
        });
        yield return ("Dreambound has no eligible grant to a played target that already died or left play", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var end in new[] { Tag(overlay.PlayedAfter, "ZONE", "GRAVEYARD"), Tag(overlay.PlayedAfter, "DAMAGE", "4") })
            {
                var result = Eval(frame, cards, overlay with { PlayedAfter = end }); Exact(result.TriggerCount, 0);
                Check(result.Triggers.Single().Eligibility == "InactiveOrNoLiveTarget", "A dead target was treated as an observed keyword recipient.");
            }
        });
        yield return ("Dreambound dormant or ambiguous dormant recipients do not become definite granted effects", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var end in new[] { Tag(overlay.PlayedAfter, "DORMANT", "1"), Tag(overlay.PlayedAfter, "1518", "1"),
                Tag(overlay.PlayedAfter, "DORMANT", "bad"), Tag(Tag(overlay.PlayedAfter, "DORMANT", "0"), "1518", "1") })
            { var result = Eval(frame, cards, overlay with { PlayedAfter = end }); Bounds(result.TriggerCount, 0, 1); Check(!result.ResolvesTriggerCount, "A dormant or ambiguous recipient acquired a definite native grant."); }
        });
        yield return ("Dreambound missing malformed and conflicting current life tags preserve uncertainty", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var end in new[] { RemoveTag(overlay.SourcesAfterPlay[0], "HEALTH"), RemoveTag(overlay.SourcesAfterPlay[0], "DAMAGE"),
                Tag(overlay.SourcesAfterPlay[0], "HEALTH", "bad"), Tag(overlay.SourcesAfterPlay[0], "DAMAGE", "-1"),
                Tag(overlay.SourcesAfterPlay[0], "45", "8"), Tag(overlay.SourcesAfterPlay[0], "44", "3") })
            { var result = Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { end } }); Bounds(result.TriggerCount, 0, 1); Check(!result.ResolvesTriggerCount, "Current life uncertainty became a confirmed grant."); }
            var extreme = Tag(Tag(overlay.SourcesAfterPlay[0], "HEALTH", int.MaxValue.ToString()), "DAMAGE", "0");
            Exact(Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { extreme } }).TriggerCount, 1);
        });
        yield return ("Dreambound flag aliases agree or preserve a zero-or-one branch", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var end in new[] { Tag(overlay.SourcesAfterPlay[0], "SILENCED", "bad"), Tag(overlay.SourcesAfterPlay[0], "DORMANT", "-1"),
                Tag(Tag(overlay.SourcesAfterPlay[0], "SILENCED", "0"), "188", "1"), Tag(Tag(overlay.SourcesAfterPlay[0], "DORMANT", "0"), "1518", "1") })
                Bounds(Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { end } }).TriggerCount, 0, 1);
            Exact(Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { Tag(Tag(overlay.SourcesAfterPlay[0], "188", "0"), "1518", "0") } }).TriggerCount, 1);
        });
        yield return ("Dreambound freshly activated previous inactive sources are not assumed registered", () =>
        {
            var frame = Tag(Frame(), 40, "DORMANT", "1"); var overlay = Overlay(frame);
            var result = Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { Tag(overlay.SourcesAfterPlay[0], "DORMANT", "0") } });
            Bounds(result.TriggerCount, 0, 1); Check(!result.ResolvesTriggerCount, "An unproved new listener was considered definitely registered.");
        });
        yield return ("Dreambound pending native resolutions cannot pretend the after-play grant already happened", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, Overlay(frame) with { PriorResolutionPending = true });
            Bounds(result.TriggerCount, 0, 1); Check(!result.ResolvesTriggerCount && result.PriorResolutionPending && result.RequiresObservation &&
                result.Unknowns.Any(value => value.Contains("前置", StringComparison.Ordinal)), "An unresolved draw/generation was skipped before after-play triggers.");
        });
        yield return ("Dreambound self registration is a separate unknown and an existing Raptor remains a confirmed source", () =>
        {
            var frame = Replace(Frame(), Entity(30, "EDR_849", "HAND")); var overlay = Overlay(frame);
            var duplicateCopy = overlay.PlayedAfter with { Tags = overlay.PlayedAfter.Tags.ToDictionary().ToFrozenDictionary() };
            var result = Eval(frame, cards, overlay with { SourcesAfterPlay = overlay.SourcesAfterPlay.Append(duplicateCopy).ToArray() });
            Bounds(result.TriggerCount, 1, 2); Check(result.Triggers.Single(trigger => trigger.SourceEntityId == 30).Eligibility == "SelfRegistrationUnconfirmed" &&
                !result.ResolvesTriggerCount, "The newly played Raptor definitely did or did not grant itself a Bonus Effect without evidence.");
            var dead = Tag(overlay.PlayedAfter, "DAMAGE", "1");
            result = Eval(frame, cards, overlay with { PlayedAfter = dead, SourcesAfterPlay = overlay.SourcesAfterPlay.Append(dead).ToArray() }); Exact(result.TriggerCount, 0);
        });
        yield return ("Dreambound overlays require exact match state and event binding without echoing foreign metadata", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var bad in new[] { overlay with { MatchId = "PRIVATE_FOREIGN_MATCH" }, overlay with { StateVersion = 11 }, overlay with { EventSequence = 101 } })
            {
                var result = Eval(frame, cards, bad); Check(!result.ResolvesTriggerCount && result.TriggerCount.Maximum is null &&
                    !JsonSerializer.Serialize(result).Contains("PRIVATE_FOREIGN", StringComparison.Ordinal), "An unbound future/foreign overlay was used or echoed.");
            }
        });
        yield return ("Dreambound duplicate missing and fabricated source rows are rejected rather than double counted", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var bad in new[] { overlay with { SourcesBeforePlay = overlay.SourcesBeforePlay.Concat(overlay.SourcesBeforePlay).ToArray() },
                overlay with { SourcesAfterPlay = overlay.SourcesAfterPlay.Concat(overlay.SourcesAfterPlay).ToArray() },
                overlay with { SourcesAfterPlay = Array.Empty<EntitySnapshot>() }, overlay with { SourcesAfterPlay = new[] { Entity(99, "EDR_849", "PLAY") } },
                overlay with { SourcesBeforePlay = new[] { Entity(99, "EDR_849", "PLAY") }, SourcesAfterPlay = new[] { Entity(99, "EDR_849", "PLAY") } },
                overlay with { SourcesBeforePlay = new EntitySnapshot[] { null! } },
                overlay with { SourcesBeforePlay = new[] { overlay.SourcesBeforePlay[0] with { Tags = null! } } } })
                Reject(Eval(frame, cards, bad));
        });
        yield return ("Dreambound target before and after must preserve known own hand identity and minion traits", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var bad in new[] { overlay with { PlayedBefore = Tag(overlay.PlayedBefore, "ZONE", "PLAY") },
                overlay with { PlayedAfter = null! }, overlay with { PlayedBefore = overlay.PlayedBefore with { CardId = "CORE_UNG_912" } },
                overlay with { PlayedAfter = overlay.PlayedAfter with { CardId = "CORE_UNG_912" } },
                overlay with { PlayedAfter = Tag(overlay.PlayedAfter, "CONTROLLER", "2") },
                overlay with { PlayedBefore = Tag(overlay.PlayedBefore, "CARDTYPE", "SPELL") },
                overlay with { PlayedAfter = Tag(overlay.PlayedAfter, "202", "5") } }) Reject(Eval(frame, cards, bad));
        });
        yield return ("Dreambound ownership source identity and observed type conflicts cannot use printed native metadata", () =>
        {
            var frame = Frame(); var overlay = Overlay(frame);
            foreach (var source in new[] { Tag(overlay.SourcesBeforePlay[0], "CONTROLLER", "2"), Tag(overlay.SourcesBeforePlay[0], "50", "2"),
                Tag(overlay.SourcesBeforePlay[0], "49", "3"), Tag(overlay.SourcesBeforePlay[0], "CARDTYPE", "SPELL"),
                Tag(overlay.SourcesBeforePlay[0], "202", "5"), overlay.SourcesBeforePlay[0] with { CardId = "CORE_UNG_912" } })
                Reject(Eval(frame, cards, overlay with { SourcesBeforePlay = new[] { source } }));
            foreach (var source in new[] { Tag(overlay.SourcesAfterPlay[0], "CONTROLLER", "2"),
                Tag(overlay.SourcesAfterPlay[0], "CARDTYPE", "SPELL"), overlay.SourcesAfterPlay[0] with { CardId = "CORE_UNG_912" } })
                Bounds(Eval(frame, cards, overlay with { SourcesAfterPlay = new[] { source } }).TriggerCount, 0, 1);
        });
        yield return ("Dreambound custom assembled halves and offers never reuse the native Raptor trigger", () =>
        {
            foreach (var key in new[] { "CRAFTABLE_FIRST_HALF_DBID", "4572", "CRAFTABLE_SECOND_HALF_DBID", "4573" })
            foreach (var value in new[] { "1", "114933", "bad", "-1" })
            {
                var frame = Tag(Frame(), 40, key, value); Reject(Eval(frame, cards, Overlay(frame)));
            }
            var native = Tag(Tag(Frame(), 40, "4572", "0"), 40, "4573", "0"); Exact(Eval(native, cards, Overlay(native)).TriggerCount, 1);
            var conflict = Tag(Tag(Frame(), 40, "4572", "0"), 40, "CRAFTABLE_FIRST_HALF_DBID", "1"); Reject(Eval(conflict, cards, Overlay(conflict)));
        });
        yield return ("Dreambound rejects missing inconsistent and malformed fixed native source catalog definitions", () =>
        {
            var frame = Frame();
            foreach (var source in new[] { cards["EDR_849"] with { DbfId = 114934 }, cards["EDR_849"] with { Id = "OTHER" },
                cards["EDR_849"] with { Text = "Give two Bonus Effects" }, cards["EDR_849"] with { Type = "SPELL" },
                cards["EDR_849"] with { RawJson = "[]" }, cards["EDR_849"] with { RawJson = "broken" } })
            { var changed = cards.ToDictionary(); changed["EDR_849"] = source; Reject(Eval(frame, changed, Overlay(frame))); }
            var missing = cards.ToDictionary(); missing.Remove("EDR_849"); Reject(Eval(frame, missing, Overlay(frame)));
        });
        yield return ("Dreambound rejects wrong build perspective quality phase and malformed entity containers", () =>
        {
            var frame = Frame();
            foreach (var bad in new[] { frame with { ClientBuild = null }, frame with { ClientBuild = "255791" }, frame with { IsSpectating = true },
                frame with { LocalPlayerId = null }, frame with { ActivePlayerId = 2 }, frame with { Phase = GamePhase.Choice },
                frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
                frame with { Quality = frame.Quality with { HasGameStart = false } }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } },
                frame with { Quality = frame.Quality with { OpenBlockCount = 1 } }, Replace(frame, frame.Entities[40] with { Tags = null! }),
                Replace(frame, new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary())) })
            { var result = Eval(bad, cards); Reject(result); Check(result.TargetCardId is null && result.Triggers.Count == 0, "Rejected perspective exposed private identities."); }
        });
        yield return ("Dreambound reads no hidden hand secret deck or unrelated private donor identity", () =>
        {
            var frame = Replace(Replace(Replace(Frame(), Entity(90, "PRIVATE_OPPONENT_HAND", "HAND", 2)), Entity(91, "PRIVATE_OWN_DECK", "DECK")),
                Tag(Entity(92, "PRIVATE_OPPONENT_SECRET", "SECRET", 2), "4572", "987654321"));
            var result = Eval(frame, cards, Overlay(frame)); Exact(result.TriggerCount, 1);
            var json = JsonSerializer.Serialize(result); Check(!json.Contains("PRIVATE_", StringComparison.Ordinal) && !json.Contains("987654321", StringComparison.Ordinal), "Hidden identity or custom donor leaked into the result.");
            var other = DreamboundRaptorRules.Evaluate(frame, 90, cards); Check(other.TargetCardId is null, "An opposing hand target identity leaked.");
        });
        yield return ("Dreambound overlays and results freeze caller collections and do not mutate source snapshots", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var cardBefore = JsonSerializer.Serialize(cards);
            var source = new List<EntitySnapshot> { frame.Entities[40] }; var after = new List<EntitySnapshot>(source);
            var overlay = new DreamboundRaptorState(frame.MatchId, frame.StateVersion, frame.EventSequence, frame.Entities[30],
                Tag(frame.Entities[30], "ZONE", "PLAY"), source, after); source.Clear(); after.Clear();
            var result = Eval(frame, cards, overlay); Exact(result.TriggerCount, 1);
            Check(before == JsonSerializer.Serialize(frame) && cardBefore == JsonSerializer.Serialize(cards), "Evaluation mutated its authoritative inputs.");
            try { ((IList<EntitySnapshot>)overlay.SourcesBeforePlay).Clear(); throw new Exception("Mutable overlay sources."); } catch (NotSupportedException) { }
            try { ((IList<DreamboundRaptorTrigger>)result.Triggers).Clear(); throw new Exception("Mutable trigger list."); } catch (NotSupportedException) { }
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable unknowns."); } catch (NotSupportedException) { }
        });
    }

    private static DreamboundRaptorOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string,CardRecord> cards, DreamboundRaptorState? overlay = null) =>
        DreamboundRaptorRules.Evaluate(frame, 30, cards, overlay);
    private static DreamboundRaptorState Overlay(GameSnapshot frame)
    {
        var sources = frame.Entities.Values.Where(entity => entity.CardId == "EDR_849" && entity.Zone == "PLAY" && entity.ControllerId == 1).ToArray();
        return new(frame.MatchId, frame.StateVersion, frame.EventSequence, frame.Entities[30], Tag(frame.Entities[30], "ZONE", "PLAY"), sources, sources);
    }
    private static GameSnapshot Frame() => new("dreambound-fixture", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Entity(30, "EDR_852", "HAND"), Entity(40, "EDR_849", "PLAY") }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Entity(int id, string card, string zone, int owner = 1) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION", ["HEALTH"] = card == "EDR_849" ? "1" : "4", ["DAMAGE"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static EntitySnapshot RemoveTag(EntitySnapshot entity, string key)
    { var tags = entity.Tags.ToDictionary(); tags.Remove(key); return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int entity, string key, string value) => Replace(frame, Tag(frame.Entities[entity], key, value));
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string,CardRecord> Cards()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dreambound-255790.json")));
        Check(document.RootElement.GetProperty("build").GetString() == "255790", "Dreambound fixture build changed.");
        return document.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(),
            raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!, raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.GetProperty("cost").GetInt32(), raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Reject(DreamboundRaptorOutcome result) => Check(!result.ResolvesTriggerCount && result.RequiresObservation && result.TriggerCount.Maximum is null, "Invalid evidence acquired a bounded confirmed trigger result.");
    private static void Exact(DreamboundTriggerRange range, int amount) => Bounds(range, amount, amount);
    private static void Bounds(DreamboundTriggerRange range, int min, int? max) => Check(range.Minimum == min && range.Maximum == max, $"Expected {min}..{max}, got {range.Minimum}..{range.Maximum}.");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
