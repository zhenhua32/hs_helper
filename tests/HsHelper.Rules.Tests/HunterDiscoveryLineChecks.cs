using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterDiscoveryLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> baseCards)
    {
        var cards = LoadCards(baseCards);
        var sourceIds = new[] { "CORE_DS1_184", "TLC_824", "EDR_888", "JAIL_831", "BE_044", "CORE_EDR_004_2026", "CORE_EDR_004" };
        LineEvaluation Eval(GameSnapshot frame, params PlannedAction[] actions) => new LineEvaluator().EvaluateLine(frame,
            actions.Length == 0 ? [new(ActionKind.PlayCard, 30)] : actions, cards);
        yield return ("native discovery sources pay current cost and leave hand without fabricating candidates", () =>
        {
            foreach (var id in sourceIds)
            {
                var frame = Frame(id, cards); var before = JsonSerializer.Serialize(frame); var result = Eval(frame);
                var source = Find(result, 30); var preview = result.Result.DiscoveryPreviews.Single();
                Check(result.AppliedActions == 1 && result.Verification == LineVerification.Conditional && result.Result.AvailableMana == 9 &&
                    source.Zone == (cards[id].Type == "SPELL" ? "GRAVEYARD" : "PLAY") && source.Cost == 1 &&
                    result.Result.DiscoveryBoundaryReached && preview.SourceCardId == id && preview.ResolvesEffects &&
                    preview.RequiresChoice != false && !preview.CanEvaluateFollowupActions && !preview.HasAuthoritativeNextState &&
                    result.Result.Entities.Select(entity => entity.EntityId).Order().SequenceEqual(frame.Entities.Keys.Order()) &&
                    !result.IsLethal && JsonSerializer.Serialize(frame) == before,
                    "Native use retained the source, used printed cost, invented candidates or mutated its observation.");
                if (cards[id].Type == "MINION") Check(source.Attack == 5 && source.MaximumHealth == 6 && source.RemainingHealth == 6,
                    "Native entry replaced already observed minion stats with printed stats.");
            }
        });
        yield return ("native discovery observation boundary leaves every submitted following action unapplied", () =>
        {
            foreach (var id in sourceIds)
            {
                var result = Eval(Frame(id, cards), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
                Check(result.AppliedActions == 1 && result.Verification == LineVerification.Unsupported && !result.Result.TurnEnded &&
                    result.Result.DiscoveryBoundaryReached && result.UnsupportedMechanics.Contains("AWAIT_DISCOVERY_SNAPSHOT") &&
                    result.Result.DiscoveryPreviews.Count == 1 && !result.IsLethal,
                    "A native source crossed discovery/assembly/Rewind into an unobserved next action.");
            }
        });
        yield return ("Tracking reliably empty deck has no discovery or fatigue and permits end turn", () =>
        {
            var frame = EmptyDeck(Frame("CORE_DS1_184", cards)); frame = HunterGenerationLineChecks.Player(frame, ("FATIGUE", "4"));
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.AppliedActions == 2 && result.Verification == LineVerification.Verified && result.Result.TurnEnded &&
                result.Result.AvailableMana == 9 && !result.Result.DiscoveryBoundaryReached && !result.Result.DrawBoundaryReached &&
                result.Result.DrawPreviews.Count == 0 && result.Result.DiscoveryPreviews.Single() is
                    { DeckCountBeforeEffect: 0, RequiresChoice: false, EffectActive: true } && Find(result, 10).RemainingHealth == 30 &&
                Find(result, 2).Flags["FATIGUE"] == 4,
                "Discover from a confirmed empty deck was treated as a draw/fatigue or an invented choice.");
        });
        yield return ("Tracking missing current count never equates no created deck entities with empty", () =>
        {
            var frame = EmptyDeck(Frame("CORE_DS1_184", cards));
            frame = Add(frame, frame.Entities[2] with { Tags = frame.Entities[2].Tags.Where(pair => pair.Key != "NUM_CARDS_IN_DECK").ToFrozenDictionary() });
            var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.AppliedActions == 1 && result.Result.DiscoveryBoundaryReached && !result.Result.TurnEnded &&
                result.Result.DiscoveryPreviews.Single() is { DeckCountBeforeEffect: null, RequiresChoice: null },
                "An absent count became zero and enabled an unobserved followup.");
        });
        yield return ("suppressed native discovery battlecries enter as ordinary bodies without a choice", () =>
        {
            foreach (var id in new[] { "EDR_888", "JAIL_831", "BE_044" })
            {
                var frame = Frame(id, cards); frame = Add(frame, Tagged(frame.Entities[30], ("BATTLECRY", "0")));
                var result = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
                Check(result.AppliedActions == 2 && result.Result.TurnEnded && result.Verification == LineVerification.Verified &&
                    Find(result, 30).Zone == "PLAY" && !result.Result.DiscoveryBoundaryReached &&
                    result.Result.DiscoveryPreviews.Single() is { EffectActive: false, RequiresDiscoveryChoice: false, RequiresChoice: false },
                    "Suppressed battlecry either refused a valid body or produced an unnecessary choice.");
            }
        });
        yield return ("Raptor suppressed battlecry retains independent Rewind and explicit removed Rewind permits continuation", () =>
        {
            foreach (var id in new[] { "CORE_EDR_004_2026", "CORE_EDR_004" })
            {
                var frame = Frame(id, cards); frame = Add(frame, Tagged(frame.Entities[30], ("BATTLECRY", "0"), ("REWIND", "1")));
                var pending = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
                Check(pending.AppliedActions == 1 && pending.Result.DiscoveryBoundaryReached && !pending.Result.TurnEnded &&
                    pending.Result.DiscoveryPreviews.Single() is { EffectActive: false, RewindActive: true, RequiresChoice: true },
                    "Battlecry zero erased Raptor's independent timeline choice.");
                frame = Add(frame, Tagged(frame.Entities[30], ("REWIND", "0")));
                var removed = Eval(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
                Check(removed.AppliedActions == 2 && removed.Verification == LineVerification.Verified && removed.Result.TurnEnded &&
                    !removed.Result.DiscoveryBoundaryReached && removed.Result.DiscoveryPreviews.Single().RequiresChoice == false,
                    "A current removed Rewind prevented the reviewed ordinary-body continuation or differed between aliases.");
            }
        });
        yield return ("native discovery invalid target mana and full board fail before payment or entry", () =>
        {
            foreach (var id in sourceIds)
            {
                var frame = Frame(id, cards);
                foreach (var result in new[] { Eval(frame, new PlannedAction(ActionKind.PlayCard, 30, 11)),
                    Eval(HunterGenerationLineChecks.Player(frame, ("RESOURCES", "0"))) })
                    Check(result.AppliedActions == 0 && result.Verification == LineVerification.Invalid && Find(result, 30).Zone == "HAND" &&
                        result.Result.DiscoveryPreviews.Count == 0 && result.Result.OpponentHealthPayments.Count == 0,
                        "Illegal native use paid or changed the source before validating targets/resources.");
                if (cards[id].Type == "MINION")
                {
                    var full = frame;
                    for (var i = 0; i < 7; i++) full = Add(full, Minion(50 + i, "VANILLA", "PLAY", 1));
                    var result = Eval(full);
                    Check(result.AppliedActions == 0 && result.Verification == LineVerification.Invalid && result.Result.AvailableMana == 10 &&
                        Find(result, 30).Zone == "HAND" && result.Result.DiscoveryPreviews.Count == 0, "Full board charged for a discovery minion.");
                }
            }
        });
        yield return ("native discovery assembled or metadata-conflicting sources fail before payment", () =>
        {
            foreach (var id in sourceIds)
            {
                var frame = Frame(id, cards);
                foreach (var (key, value) in new[] { ("4572", "1"), ("4573", "bad"), ("CRAFTABLE_FIRST_HALF_DBID", "123") })
                {
                    var result = Eval(Add(frame, Tagged(frame.Entities[30], (key, value))));
                    Check(result.AppliedActions == 0 && result.Result.AvailableMana == 10 && Find(result, 30).Zone == "HAND" &&
                        result.Result.DiscoveryPreviews.Count == 0 && result.UnsupportedMechanics.Contains("CUSTOM_ASSEMBLED_SOURCE"),
                        "Assembled native ID executed its base Discover/Assemble/Rewind script.");
                }
                var conflict = cards.ToDictionary(); conflict[id] = conflict[id] with { DbfId = 2 };
                var rejected = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.PlayCard, 30)], conflict);
                Check(rejected.AppliedActions == 0 && rejected.Result.AvailableMana == 10 && Find(rejected, 30).Zone == "HAND" &&
                    rejected.Result.DiscoveryPreviews.Count == 0, "A substituted fixed DBF executed before native metadata audit.");
            }
        });
        yield return ("native discovery identity and flag alias contradictions are preserved before effects", () =>
        {
            var frame = Frame("EDR_888", cards);
            foreach (var (key, value) in new[] { ("49", "1"), ("50", "2"), ("202", "SPELL"), ("53", "31") })
            {
                var result = Eval(Add(frame, Tagged(frame.Entities[30], (key, value))));
                Check(result.AppliedActions == 0 && result.Result.AvailableMana == 10 && Find(result, 30).Zone == "HAND",
                    "An original source identity contradiction was repaired after payment.");
            }
            var ambiguous = Add(frame, Tagged(frame.Entities[30], ("BATTLECRY", "0"), ("218", "1")));
            var preview = Eval(ambiguous, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(preview.AppliedActions == 1 && preview.Result.DiscoveryBoundaryReached &&
                preview.Result.DiscoveryPreviews.Single() is { EffectActive: null, RequiresChoice: null },
                "Runtime's single battlecry value erased a contradictory original alias.");
        });
        yield return ("lethal Agamaggan health payment stops every discovery source before its body or native effect", () =>
        {
            foreach (var id in sourceIds)
            {
                var frame = PaymentFrame(id, cards); var result = Eval(frame, new(ActionKind.PlayCard, 31), new(ActionKind.PlayCard, 30));
                Check(result.AppliedActions == 2 && result.IsLethal && result.Result.OpponentDefeatedByPayment &&
                    result.Result.OpponentHealthPayments.Single() is { CardEntityId: 30, Cost: 1, HealthBefore: 1, HealthAfter: 0 } &&
                    Find(result, 30).Zone == "HAND" && result.Result.DiscoveryPreviews.Count == 0 &&
                    !result.Result.DiscoveryBoundaryReached && result.Result.AvailableMana == 2,
                    "Game-ending payment still summoned/resolved a discovery card or created a pending choice.");
            }
        });
        yield return ("limited lethal search includes verified Agamaggan then native Tracking terminal payment", () =>
        {
            var frame = PaymentFrame("CORE_DS1_184", cards);
            var result = new LineEvaluator().FindLethal(frame, cards, maximumDepth: 2, maximumNodes: 100);
            Check(result.VerifiedLethalFound && result.Actions.Count == 2 && result.Actions[0] == new PlannedAction(ActionKind.PlayCard, 31) &&
                result.Actions[1] == new PlannedAction(ActionKind.PlayCard, 30) && result.Evaluation!.Result.OpponentDefeatedByPayment &&
                result.Evaluation.Result.DiscoveryPreviews.Count == 0 && Find(result.Evaluation, 30).Zone == "HAND",
                "Search omitted the legal terminal discovery-source payment or used a favorable invented discovery outcome.");
        });
        yield return ("pending native discovery keeps after-play quest and Raptor previews conditional", () =>
        {
            var frame = Frame("EDR_888", cards);
            frame = Add(frame, Card(60, "TLC_EVENT_400", "SECRET", "SPELL", ("SIDE_QUEST", "1"), ("QUEST_PROGRESS", "2"), ("QUEST_PROGRESS_TOTAL", "3")));
            frame = Add(frame, Minion(50, "EDR_849", "PLAY", 1));
            var result = Eval(frame);
            Check(result.Result.DiscoveryBoundaryReached && result.Result.QuestProgressPreviews.Single() is
                { IsConditionalPreview: true, ResultingProgress: 3, CanProjectProgress: false } && Find(result, 60).Flags["QUEST_PROGRESS"] == 2 &&
                result.Result.RaptorPreviews.Single().PriorResolutionPending && !Find(result, 30).Flags.ContainsKey("DIVINE_SHIELD"),
                "After-play task reward or random Raptor effect happened before the source's native discovery settled.");
            frame = EmptyDeck(Frame("CORE_DS1_184", cards));
            frame = Add(frame, Card(70, "UNKNOWN_OPPONENT_SECRET", "SECRET", "SPELL", ("CONTROLLER", "2")));
            result = Eval(frame);
            Check(result.Result.DiscoveryPreviews.Single() is { IsConditionalPreview: true, DeckCountBeforeEffect: null, RequiresChoice: null } &&
                result.Result.DiscoveryBoundaryReached, "Preexisting unresolved trigger scope was erased by original empty-deck evidence.");
        });
    }

    private static GameSnapshot Frame(string id, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = HunterGenerationLineChecks.Frame(); var card = cards[id];
        var source = card.Type == "SPELL" ? Card(30, id, "HAND", "SPELL", ("COST", "1")) : Minion(30, id, "HAND", 1);
        return HunterGenerationLineChecks.Player(Add(frame, source), ("NUM_CARDS_IN_DECK", "1"));
    }
    private static GameSnapshot EmptyDeck(GameSnapshot frame) => HunterGenerationLineChecks.Player(frame with
        { Entities = frame.Entities.Where(pair => pair.Value.Zone != "DECK").ToFrozenDictionary() }, ("NUM_CARDS_IN_DECK", "0"));
    private static GameSnapshot PaymentFrame(string id, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var frame = Frame(id, cards); frame = Add(frame, Minion(31, "EDR_489", "HAND", 8));
        frame = HunterGenerationLineChecks.Player(frame, ("NUM_CARDS_IN_HAND", "2"));
        return Add(frame, Tagged(frame.Entities[11], ("HEALTH", "1")));
    }
    private static EntitySnapshot Minion(int id, string card, string zone, int cost) => Card(id, card, zone, "MINION",
        ("COST", cost.ToString()), ("ATK", "5"), ("HEALTH", "6"), ("DAMAGE", "0"), ("CARDRACE", "BEAST"));
    private static EntitySnapshot Card(int id, string card, string zone, string type, params (string Key, string Value)[] extra)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = "1", ["ZONE"] = zone, ["CARDTYPE"] = type };
        foreach (var (key, value) in extra) tags[key] = value;
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Tagged(EntitySnapshot entity, params (string Key, string Value)[] values)
    { var tags = entity.Tags.ToDictionary(); foreach (var (key, value) in values) tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity) => HunterGenerationLineChecks.Add(frame, entity);
    private static SimulatedEntity Find(LineEvaluation result, int id) => result.Result.Entities.Single(entity => entity.EntityId == id);
    private static IReadOnlyDictionary<string, CardRecord> LoadCards(IReadOnlyDictionary<string, CardRecord> original)
    {
        var cards = original.ToDictionary();
        foreach (var name in new[] { "beast-hunter-255790.json", "discovery-play-255790.json", "quest-progress-255790.json" })
        {
            using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            foreach (var raw in input.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.TryGetProperty("text", out var text) ? text.GetString()! : "",
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText());
            }
        }
        return cards;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
