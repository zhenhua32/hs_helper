using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class HunterMapPlayChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        LineEvaluation Eval(GameSnapshot frame, params PlannedAction[] actions) => new LineEvaluator().EvaluateLine(frame,
            actions.Length == 0 ? [new(ActionKind.PlayCard, 80)] : actions, cards);
        yield return ("Map playing its proven first selected entity creates a conditional runtime boundary without rewriting actual history", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame); var preview = result.Result.MapPlayPreviews.Single();
            Check(result.AppliedActions == 1 && result.Result.MapFollowupBoundaryReached && result.Verification == LineVerification.Conditional &&
                result.Result.Entities.Single(entity => entity.EntityId == 80).Zone == "PLAY" && result.Result.AvailableMana == 6 &&
                preview.MatchId == frame.MatchId && preview.StateVersion == frame.StateVersion && preview.EventSequence == frame.EventSequence &&
                preview.MapSourceEntityId == 30 && preview.PlayedEntityId == 80 && preview.InitialChoiceId == 1 &&
                preview.OriginalChosenCardId == "EDR_227" && preview.PlayedCardId == "EDR_227" && preview.IdentityMatch == true &&
                preview.RequiresObservation && !preview.PriorResolutionPending && preview.IsConditionalRequest &&
                !preview.HasAuthoritativeNextState && !preview.CanEvaluateFollowupActions && before == JsonSerializer.Serialize(frame) &&
                frame.Choice is null && frame.TurnHistory!.Turns.Single().Plays.Count == 1 && result.Result.Entities.Count == frame.Entities.Count,
                "A simulated selected play failed to stop, fabricated a real play/choice/card, or lost its evidence binding.");
        });
        yield return ("Map followup boundary blocks the next submitted action until an actual offer is observed", () =>
        {
            var frame = Add(Frame(), Card(90, "EDR_227", "HAND")); var result = Eval(frame, new(ActionKind.PlayCard, 80), new(ActionKind.PlayCard, 90));
            Check(result.AppliedActions == 1 && result.Result.MapPlayPreviews.Count == 1 && result.Result.MapFollowupBoundaryReached &&
                result.Result.Entities.Single(entity => entity.EntityId == 90).Zone == "HAND" &&
                result.UnsupportedMechanics.Contains("AWAIT_MAP_FOLLOWUP_SNAPSHOT"), "The line crossed the unobserved second-choice boundary.");
        });
        yield return ("Map a cloned same-name hand card is not the original selected entity", () =>
        {
            var frame = Add(Frame(), Card(90, "EDR_227", "HAND")); var result = Eval(frame, new PlannedAction(ActionKind.PlayCard, 90));
            Check(result.AppliedActions == 1 && !result.Result.MapFollowupBoundaryReached && result.Result.MapPlayPreviews.Count == 0 &&
                result.Result.Entities.Single(entity => entity.EntityId == 80).Zone == "HAND", "A same CardId clone triggered the original selection's Map effect.");
        });
        yield return ("Map same selected entity with a changed known identity remains an unknown conditional request", () =>
        {
            var frame = Frame(original: "CORE_UNG_912", current: "EDR_227"); var result = Eval(frame); var preview = result.Result.MapPlayPreviews.Single();
            Check(preview.OriginalChosenCardId == "CORE_UNG_912" && preview.PlayedCardId == "EDR_227" && preview.IdentityMatch == false &&
                preview.RequiresObservation && result.Result.MapFollowupBoundaryReached &&
                preview.Unknowns.Any(value => value.Contains("不同或未知", StringComparison.Ordinal)), "Changed identity was silently treated as matching or definitely no trigger.");
        });
        yield return ("Map preceding native generation remains pending before the simulated followup request", () =>
        {
            var frame = Frame("CORE_UNG_912", "CORE_UNG_912"); var result = Eval(frame); var preview = result.Result.MapPlayPreviews.Single();
            Check(result.Result.GenerationBoundaryReached && result.Result.GenerationPreviews.Single().RequestedCards.Minimum == 1 &&
                preview.PriorResolutionPending && preview.RequiresObservation && result.Result.MapFollowupBoundaryReached &&
                result.Result.Entities.Count == frame.Entities.Count, "Map pretended the Macaw generation or extra choice already resolved.");
        });
        yield return ("Map an ended original actual turn cannot arm a new-turn simulated request", () =>
        {
            var frame = Frame(); var original = frame.TurnHistory!.Turns.Single(); frame = frame with { Turn = 6 };
            frame = frame with { TurnHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence,
                [new(5, 1, 10, 90, true, true, original.Plays), new(6, 1, 91, null, true, false, [])], true, 0, []) };
            var result = Eval(frame); Check(result.AppliedActions == 1 && !result.Result.MapFollowupBoundaryReached && result.Result.MapPlayPreviews.Count == 0,
                "A later actual extra own turn rearmed an expired Map instance.");
        });
        yield return ("Map missing actual chosen or Map source HAND-play history never arms a runtime request", () =>
        {
            var frame = Frame();
            foreach (var bad in new[] { frame with { ChoiceHistory = null }, frame with { TurnHistory = null },
                frame with { TurnHistory = new(frame.MatchId, frame.StateVersion, frame.EventSequence, [new(5, 1, 10, null, true, false, [])], true, 0, []) } })
            { var result = Eval(bad); Check(result.Result.MapPlayPreviews.Count == 0 && !result.Result.MapFollowupBoundaryReached, "Missing actual evidence was replaced by a simulated Map history."); }
        });
        yield return ("Map unobserved followup blocks later quest counter projection in the same after-play stage", () =>
        {
            var quest = Tag(Tag(Tag(Card(50, "TLC_EVENT_400", "SECRET"), "CARDTYPE", "SPELL"), "SIDE_QUEST", "1"), "QUEST_PROGRESS", "0");
            quest = Tag(quest, "QUEST_PROGRESS_TOTAL", "3"); var result = Eval(Add(Frame(), quest));
            Check(result.Result.MapFollowupBoundaryReached && result.Result.QuestProgressPreviews.Single().IsConditionalPreview &&
                !result.Result.QuestProgressPreviews.Single().CanProjectProgress &&
                result.Result.Entities.Single(entity => entity.EntityId == 50).Flags["QUEST_PROGRESS"] == 0,
                "The quest was projected beyond an unresolved Map followup choice.");
        });
        yield return ("Map unsuccessful source payment does not report a successful selected play", () =>
        {
            var frame = Tag(Frame(), 2, "RESOURCES", "0"); var result = Eval(frame);
            Check(result.AppliedActions == 0 && !result.Result.MapFollowupBoundaryReached && result.Result.MapPlayPreviews.Count == 0 &&
                result.Result.Entities.Single(entity => entity.EntityId == 80).Zone == "HAND", "A failed play emitted a Map request.");
        });
        yield return ("Map runtime requests ignore unrelated hidden identities and freeze their public lists", () =>
        {
            var frame = Add(Frame(), Card(99, "PRIVATE_MAP_OPPONENT", "HAND") with { Tags = Tag(Card(99, "PRIVATE_MAP_OPPONENT", "HAND"), "CONTROLLER", "2").Tags });
            var result = Eval(frame); var preview = result.Result.MapPlayPreviews.Single();
            Check(!JsonSerializer.Serialize(preview).Contains("PRIVATE_", StringComparison.Ordinal), "A Map conditional request exported an opposing hand identity.");
            try { ((IList<string>)preview.Unknowns).Clear(); throw new Exception("Mutable Map play unknowns."); } catch (NotSupportedException) { }
        });
    }

    private static GameSnapshot Frame(string original = "EDR_227", string current = "EDR_227")
    {
        var options = new[] { new ChoiceOptionSnapshot(80, original, 1, 0), new ChoiceOptionSnapshot(81, original == "EDR_227" ? "CORE_UNG_912" : "EDR_227", 1, 1), new ChoiceOptionSnapshot(82, "EDR_480", 1, 2) };
        var entities = new[] { new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1", ["CONTROLLER"] = "1", ["RESOURCES"] = "10", ["RESOURCES_USED"] = "0", ["HERO_ENTITY"] = "10", ["MAXHANDSIZE"] = "10" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2", ["HERO_ENTITY"] = "11" }.ToFrozenDictionary()),
            Hero(10, 1), Hero(11, 2), Tag(Card(30, "TLC_824", "GRAVEYARD"), "CARDTYPE", "SPELL"), Card(80, current, "HAND") };
        var frame = new GameSnapshot("map-play-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
        return frame with { ChoiceHistory = new(frame.MatchId, 10, 100, [new(1, 1, "GENERAL", 30, "TLC_824", 1, 1, 30, 40,
            options, options.Select(option => Card(option.EntityId, option.CardId!, "SETASIDE")).ToArray(), [80], true)], true, 0, []),
            TurnHistory = new(frame.MatchId, 10, 100, [new(5, 1, 10, null, true, false, [new(20, Tag(Card(30, "TLC_824", "HAND"), "CARDTYPE", "SPELL"))])], true, 0, []) };
    }
    private static EntitySnapshot Hero(int id, int owner) => new(id, "HERO_" + owner, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO", ["CONTROLLER"] = owner.ToString(), ["HEALTH"] = "30", ["DAMAGE"] = "0", ["ARMOR"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Card(int id, string card, string zone) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = zone, ["CONTROLLER"] = "1", ["CARDTYPE"] = "MINION", ["CARDRACE"] = "BEAST",
            ["ATK"] = card == "EDR_227" ? "5" : "1", ["HEALTH"] = "2", ["DAMAGE"] = "0", ["COST"] = card == "EDR_227" ? "4" : "1" }.ToFrozenDictionary());
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int entity, string key, string value) => Add(frame, Tag(frame.Entities[entity], key, value));
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string,CardRecord> Cards()
    {
        var cards = new Dictionary<string,CardRecord>();
        foreach (var name in new[] { "map-followup-255790.json", "quest-progress-255790.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            foreach (var raw in document.RootElement.GetProperty("cards").EnumerateArray())
            {
                if (name == "quest-progress-255790.json" && raw.GetProperty("id").GetString() != "TLC_EVENT_400") continue;
                var card = new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
                    raw.GetProperty("text").GetString()!, raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
                cards[card.Id] = card;
            }
        }
        return cards.ToFrozenDictionary();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
