using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class QuestProgressChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Quest observed progress zero and one advance exactly once without changing input", () =>
        {
            foreach (var progress in new[] { 0, 1 })
            {
                var frame = Frame(progress); var before = JsonSerializer.Serialize(frame); var result = Eval(frame, cards);
                Check(result is { CurrentProgress: var current, Total: 3, PlayedCardQualifies: true, Increment: 1,
                    ResultingProgress: var next, CompletesQuest: false, CanProjectProgress: true, RequiresRewardChoice: false } &&
                    current == progress && next == progress + 1 && !result.CanEvaluateFollowupActions &&
                    !result.HasAuthoritativeNextState && before == JsonSerializer.Serialize(frame), "A single covered play changed the wrong counter/input.");
            }
        });
        yield return ("Quest third covered play reaches reward choice without creating a hand result", () =>
        {
            var result = Eval(Frame(2), cards);
            Check(result is { CurrentProgress: 2, PlayedCardQualifies: true, Increment: 1, ResultingProgress: 3,
                CompletesQuest: true, RequiresRewardChoice: true, CanProjectProgress: true } && !result.CanEvaluateFollowupActions,
                "Third play fabricated a reward result or permitted actions beyond the component choice.");
        });
        yield return ("Quest Beast Undead dual tribes and real all-type weapon each increment only once", () =>
        {
            foreach (var (card, type, race) in new[] { ("EDR_227", "MINION", "UNDEAD"), ("ULD_186", "MINION", "BEAST"),
                ("CORE_TTN_866", "MINION", "DEMON"), ("DAL_087tw", "WEAPON", "ALL") })
            {
                var frame = Replace(Frame(), Hand(card: card, type: type, race: race));
                Check(Eval(frame, cards) is { PlayedCardQualifies: true, Increment: 1, ResultingProgress: 1 },
                    "OR tribes counted twice, lost a native secondary tribe, or wrongly required MINION.");
            }
        });
        yield return ("Quest known nonmatching race and native untyped spell produce explicit no increment", () =>
        {
            foreach (var hand in new[] { Hand(card: "FIR_921", race: "DRAENEI"), Hand(race: "DEMON"),
                Without(Hand(card: "CORE_DS1_185", type: "SPELL"), "CARDRACE") })
            {
                var result = Eval(Replace(Frame(1), hand), cards);
                Check(result is { PlayedCardQualifies: false, Increment: 0, ResultingProgress: 1, CompletesQuest: false, CanProjectProgress: true },
                    "Nonqualifying native card was treated as an unknown or advanced the quest.");
            }
        });
        yield return ("Quest missing HAND race and conflicting aliases never use printed or future race", () =>
        {
            foreach (var hand in new[] { Without(Hand(), "CARDRACE"), Tagged(Hand(), ("200", "11")),
                Tagged(Hand(), ("CARDRACE", "bad")), Tagged(Hand(), ("200", "999")) })
            {
                var result = Eval(Replace(Frame(), hand), cards);
                Check(result.PlayedCardQualifies is null && result.Increment is null && result.ResultingProgress is null && !result.CanProjectProgress,
                    "Missing/conflicting race became a printed or later observed tribe.");
            }
            var valid = Tagged(Hand(), ("200", "20"));
            Check(Eval(Replace(Frame(), valid), cards).PlayedCardQualifies == true, "Equivalent race aliases were rejected.");
        });
        yield return ("Quest current count only reads explicit named or numeric aliases", () =>
        {
            var frame = Frame(1); var numeric = Tagged(Without(Quest(1), "QUEST_PROGRESS", "QUEST_PROGRESS_TOTAL"), ("534", "1"), ("535", "3"));
            Check(Eval(Replace(frame, numeric), cards) is { CurrentProgress: 1, Total: 3, ResultingProgress: 2 }, "Numeric count aliases were ignored.");
            var same = Tagged(Quest(1), ("534", "1"), ("535", "3"));
            Check(Eval(Replace(frame, same), cards).CanProjectProgress, "Equivalent progress aliases were rejected.");
            foreach (var quest in new[] { Without(Quest(1), "QUEST_PROGRESS"), Without(Quest(1), "QUEST_PROGRESS_TOTAL") })
            {
                var result = Eval(Replace(frame, quest), cards);
                Check(result.ResultingProgress is null && !result.CanProjectProgress, "Missing progress/total became zero/printed three.");
            }
        });
        yield return ("Quest malformed contradictory or out of range counters cannot be repaired by overlay", () =>
        {
            foreach (var quest in new[] { Tagged(Quest(1), ("534", "2")), Tagged(Quest(1), ("535", "4")),
                Tagged(Quest(1), ("QUEST_PROGRESS", "bad")), Tagged(Quest(1), ("QUEST_PROGRESS", "-1")),
                Tagged(Quest(1), ("QUEST_PROGRESS", "2147483648")), Quest(4), Tagged(Quest(1), ("QUEST_PROGRESS_TOTAL", "2")) })
            {
                var frame = Replace(Frame(), quest);
                Check(!Eval(frame, cards, Overlay(frame, 1)).CanProjectProgress, "Valid overlay repaired an explicit invalid original task counter.");
            }
        });
        yield return ("Quest bound overlays continue prior simulated progress without old value refill", () =>
        {
            var frame = Frame(0);
            Check(Eval(frame, cards, Overlay(frame, 1)) is { CurrentProgress: 1, ResultingProgress: 2 }, "Earlier simulated progress was replaced by snapshot zero.");
            var final = Eval(frame, cards, Overlay(frame, 2));
            Check(final.ResultingProgress == 3 && final.RequiresRewardChoice, "Second simulated progress did not lead to a choice boundary.");
            foreach (var state in new[] { Overlay(frame, null), Overlay(frame, 1) with { Total = null }, Overlay(frame, -1), Overlay(frame, 4), Overlay(frame, 1) with { Total = 4 } })
                Check(!Eval(frame, cards, state).CanProjectProgress, "Unknown/invalid overlay used old count or total.");
        });
        yield return ("Quest overlays reject wrong match state sequence or quest identity", () =>
        {
            var frame = Frame(); var state = Overlay(frame, 0);
            foreach (var invalid in new[] { state with { MatchId = "other" }, state with { StateVersion = 9 },
                state with { EventSequence = 101 }, state with { QuestEntityId = 41 } })
                Check(!Eval(frame, cards, invalid).CanProjectProgress, "Unbound future/foreign overlay supplied task progress.");
        });
        yield return ("Quest pending earlier resolution only gives a conditional counter preview", () =>
        {
            var frame = Frame(2); var result = Eval(frame, cards, Overlay(frame, 2) with { PriorResolutionPending = true });
            Check(result is { ResultingProgress: 3, CompletesQuest: true, RequiresRewardChoice: true, IsConditionalPreview: true,
                CanProjectProgress: false, ResolvesEffects: false } && !result.CanEvaluateFollowupActions,
                "A reward or progress was committed before potentially fatal earlier draw/generation resolved.");
        });
        yield return ("Quest HAND installation requires explicit bound installation and ignores stale counters", () =>
        {
            var frame = Replace(Frame(), Tagged(Quest(2), ("ZONE", "HAND"), ("49", "3"), ("QUEST_PROGRESS", "bad"), ("535", "999")));
            Check(!Eval(frame, cards).CanProjectProgress && !Eval(frame, cards, Overlay(frame, 0)).CanProjectProgress,
                "HAND progress guessed a successful task installation.");
            var installed = Overlay(frame, 0) with { IsInstalled = true };
            Check(Eval(frame, cards, installed) is { CurrentProgress: 0, Total: 3, ResultingProgress: 1, CanProjectProgress: true },
                "A newly installed native task reused stale HAND progress instead of runtime's reset zero.");
            Check(Eval(frame, cards, installed with { Progress = 1 }) is { ResultingProgress: 2, CanProjectProgress: true },
                "An installed task could not continue covered simulated progress.");
            Check(!Eval(frame, cards, installed with { Progress = null }).CanProjectProgress, "Installation flag alone synthesized zero.");
        });
        yield return ("Quest consumes HAND evidence frozen before a later transformed current entity", () =>
        {
            var frozen = Hand(); var current = Tagged(Hand(card: "FIR_921", race: "DRAENEI"), ("ZONE", "PLAY"));
            var frame = Replace(Frame(), current);
            Check(QuestProgressRules.Evaluate(frame, 40, cards, frozen).PlayedCardQualifies is null,
                "Unbound earlier HAND evidence replaced a later current entity.");
            var result = QuestProgressRules.Evaluate(frame, 40, cards, frozen, Overlay(frame, 0));
            Check(result.PlayedCardQualifies == true && result.ResultingProgress == 1, "Bound HAND evidence read the later transformed identity.");
            var missing = Without(frozen, "CARDRACE");
            Check(QuestProgressRules.Evaluate(frame, 40, cards, missing, Overlay(frame, 0)).PlayedCardQualifies is null,
                "Missing frozen race was filled from a later entity or printed Beast.");
        });
        yield return ("Quest rejects unsafe source ownership aliases type duplicate or custom text", () =>
        {
            foreach (var source in new[] { Tagged(Quest(0), ("CONTROLLER", "2")), Tagged(Quest(0), ("50", "2")),
                Tagged(Quest(0), ("ZONE", "PLAY")), Tagged(Quest(0), ("49", "3")), Tagged(Quest(0), ("202", "MINION")),
                Tagged(Quest(0), ("1192", "0")), Tagged(Quest(0), ("SIDE_QUEST", "bad")), Tagged(Quest(0), ("4572", "1")),
                Quest(0) with { CardId = null }, Quest(0) with { Tags = null! } })
                Check(!Eval(Replace(Frame(), source), cards).CanProjectProgress, "Invalid source was treated as an installed native sidequest.");
            var duplicate = Replace(Frame(), Quest(0) with { Id = 41 });
            Check(!Eval(duplicate, cards).CanProjectProgress, "Duplicate active same task was silently selected.");
            var customCards = cards.ToDictionary(); customCards["TLC_EVENT_400"] = cards["TLC_EVENT_400"] with { DbfId = 2 };
            Check(!Eval(Frame(), customCards).CanProjectProgress, "Wrong DBF supplied the audited task effect.");
        });
        yield return ("Quest rejects malformed played identity type ownership zone and custom half evidence", () =>
        {
            foreach (var played in new[] { Hand() with { CardId = null }, Hand() with { Kind = EntityKind.Player },
                Tagged(Hand(), ("202", "SPELL")), Tagged(Hand(), ("CONTROLLER", "2")), Tagged(Hand(), ("50", "2")),
                Tagged(Hand(), ("ZONE", "PLAY")), Tagged(Hand(), ("49", "1")), Tagged(Hand(), ("4573", "1")),
                Hand() with { Tags = null! }, Hand() with { Id = 99 } })
            {
                var frame = Frame();
                Check(QuestProgressRules.Evaluate(frame, 40, cards, played, Overlay(frame, 0)).PlayedCardQualifies is null,
                    "Invalid played evidence acquired a tribe or counter increment.");
            }
            var originalCustom = Replace(Frame(), Tagged(Hand(), ("4572", "1")));
            Check(QuestProgressRules.Evaluate(originalCustom, 40, cards, Hand(), Overlay(originalCustom, 0)).PlayedCardQualifies is null,
                "Overlay erased original assembled evidence and applied base native quest progress.");
        });
        yield return ("Quest requires exact build stable own perspective and complete snapshot", () =>
        {
            foreach (var frame in new[] { Frame() with { ClientBuild = "255791" }, Frame() with { IsSpectating = true },
                Frame() with { ActivePlayerId = 2 }, Frame() with { Phase = GamePhase.Choice },
                Frame() with { Quality = Frame().Quality with { IsComplete = false } },
                Frame() with { Quality = Frame().Quality with { HasGameStart = false } },
                Frame() with { Quality = Frame().Quality with { HasLocalPerspective = false } },
                Frame() with { Quality = Frame().Quality with { OpenBlockCount = 1 } } })
                Check(!Eval(frame, cards).CanProjectProgress, "Unsafe observation acquired deterministic task progress.");
        });
        yield return ("Quest fixed played profiles reject conflicting or duplicate tribe definitions", () =>
        {
            var native = cards["EDR_227"];
            foreach (var raw in new[] { "{\"id\":\"EDR_227\",\"dbfId\":114020,\"type\":\"MINION\",\"text\":" + JsonSerializer.Serialize(native.Text) +
                    ",\"race\":\"BEAST\",\"races\":[\"BEAST\",\"BEAST\"]}",
                "{\"id\":\"EDR_227\",\"dbfId\":114020,\"type\":\"MINION\",\"text\":" + JsonSerializer.Serialize(native.Text) +
                    ",\"race\":\"BEAST\",\"races\":[\"DEMON\"]}" })
            {
                var altered = cards.ToDictionary(); altered[native.Id] = native with { RawJson = raw };
                Check(Eval(Frame(), altered).PlayedCardQualifies is null, "Malformed fixed tribe data was promoted to a qualifying play.");
            }
        });
        yield return ("Quest current inspection and completed task never replay historical plays", () =>
        {
            var frame = Frame(1) with { RecentEvents = [new(99, "PLAY", 30, 1, "EDR_227", "HAND", "PLAY", "old")] };
            var current = QuestProgressRules.Evaluate(frame, 40, cards);
            Check(current.CurrentProgress == 1 && current.ResultingProgress is null && current.Increment is null && !current.CanProjectProgress,
                "Current read replayed an old global history row.");
            var completed = Eval(Frame(3), cards);
            Check(completed is { CurrentProgress: 3, Increment: 0, ResultingProgress: 3, RequiresRewardChoice: true, CanProjectProgress: false },
                "Already-complete task incremented again or created an early reward.");
            Check(current.MatchId == frame.MatchId && current.StateVersion == frame.StateVersion && current.EventSequence == frame.EventSequence,
                "Result lost its exact observation binding.");
            try { ((IList<string>)current.RuleCoverage).Clear(); throw new Exception("Mutable coverage."); } catch (NotSupportedException) { }
        });
    }

    private static QuestProgressOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, QuestProgressState? state = null) =>
        QuestProgressRules.Evaluate(frame, 40, cards, frame.Entities.GetValueOrDefault(30), state);
    private static QuestProgressState Overlay(GameSnapshot frame, int? progress) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, 40, progress, 3);
    private static GameSnapshot Frame(int progress = 0) => new("quest-preview", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Quest(progress), Hand() }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Quest(int progress) => new(40, "TLC_EVENT_400", EntityKind.Card, new Dictionary<string, string>
        { ["ZONE"] = "SECRET", ["CONTROLLER"] = "1", ["CARDTYPE"] = "SPELL", ["SIDE_QUEST"] = "1", ["QUEST_PROGRESS"] = progress.ToString(), ["QUEST_PROGRESS_TOTAL"] = "3" }.ToFrozenDictionary());
    private static EntitySnapshot Hand(string card = "EDR_227", string type = "MINION", string race = "BEAST") => new(30, card, EntityKind.Card,
        new Dictionary<string, string> { ["ZONE"] = "HAND", ["CONTROLLER"] = "1", ["CARDTYPE"] = type, ["CARDRACE"] = race }.ToFrozenDictionary());
    private static EntitySnapshot Tagged(EntitySnapshot entity, params (string Key, string Value)[] values)
    { var tags = entity.Tags.ToDictionary(); foreach (var (key, value) in values) tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static EntitySnapshot Without(EntitySnapshot entity, params string[] keys) => entity with { Tags = entity.Tags.Where(pair => !keys.Contains(pair.Key)).ToFrozenDictionary() };
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "quest-progress-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Unpinned quest fixture.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(raw => raw.GetProperty("id").GetString()!, raw =>
            new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
                raw.TryGetProperty("text", out var text) ? text.GetString()! : "", raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText()), StringComparer.Ordinal);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
