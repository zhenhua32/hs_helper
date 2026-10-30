using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class CraftChoiceChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> sourceCards)
    {
        var cards = sourceCards.ToDictionary();
        using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "craft-choice-255790.json"))))
        {
            Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Unpinned crafting fixture.");
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                cards[id] = new(id, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
                    raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                    raw.GetProperty("cost").GetInt32(), raw.GetProperty("collectible").GetBoolean(), [], [], raw.GetRawText());
            }
        }
        // Deliberately different printed numbers ensure projections only consume observed values.
        cards["CRAFT_A"] = Card("CRAFT_A"); cards["CRAFT_B"] = Card("CRAFT_B");
        yield return ("craft first component is a confirmed choice without an early hand card or reward", () =>
        {
            var result = Evaluate(Frame(false), cards);
            Check(result.Stage == "ComponentOne" && result.Mechanism == "BuildABeast" && result.ResolvesEffects &&
                result.AddedToHand == false && result.RequiresAnotherChoice == true && result.GrantedCostReduction is null &&
                result.ResultingCost is null && result.ResultingAttack is null && result.ResultingHealth is null &&
                result.Components.Count == 1 && !result.CanEvaluateFollowupActions, "First pick prematurely crafted a hand card.");
            Unconfirmed(Evaluate(Replace(Frame(false), Entity(30, "TLC_EVENT_400", "GRAVEYARD", 1)), cards));
        });
        yield return ("craft second component uses frozen prior observations and grants the quest reduction once", () =>
        {
            var frame = Replace(Frame(), Entity(50, "CRAFT_B", "HAND", 1, ("COST", 99), ("ATK", 99), ("HEALTH", 99)));
            frame = Replace(frame, Player(1, 1, 1));
            var before = JsonSerializer.Serialize(frame); var result = Evaluate(frame, cards);
            Check(result.Stage == "ComponentTwo" && result.ResolvesEffects && result.Components.Count == 2 &&
                result.Components[0] is { EntityId: 50, CardId: "CRAFT_A", FromHistory: true, ObservedCost: 2, ObservedAttack: 3, ObservedHealth: 4 } &&
                result.ResultingCost == 3 && result.ResultingAttack == 8 && result.ResultingHealth == 10 &&
                result.GrantedCostReduction == 3 && result.AddedToHand == true && result.RequiresAnotherChoice == false &&
                !result.CanEvaluateFollowupActions && before == JsonSerializer.Serialize(frame), "Second pick reused later entity/printed values or mutated input.");
            Check(Evaluate(Replace(Frame(), Entity(30, "TLC_EVENT_400", "GRAVEYARD", 1)), cards).Stage == "ComponentTwo",
                "Positive previous chosen evidence was discarded solely for missing current quest progress tags.");
            Unconfirmed(Evaluate(Replace(Frame(), Entity(30, "TLC_EVENT_400", "GRAVEYARD", 1, ("QUEST_PROGRESS", 2), ("QUEST_PROGRESS_TOTAL", 3))), cards));
        });
        yield return ("craft zero floor missing observations and numeric overflow stay explicit", () =>
        {
            var frame = Replace(Frame(), Entity(60, "CRAFT_B", "SETASIDE", 1, ("COST", 0), ("ATK", int.MaxValue), ("HEALTH", int.MaxValue)));
            var result = Evaluate(frame, cards);
            Check(result.ResultingCost == 0 && result.ResultingAttack is null && result.ResultingHealth is null,
                "Craft arithmetic wrapped or reduced cost below zero.");
            frame = Bind(Frame(), [Entry(entities: [])]);
            result = Evaluate(frame, cards);
            Check(result.Stage == "ComponentTwo" && result.ResolvesEffects && result.GrantedCostReduction == 3 &&
                result.ResultingCost is null && result.ResultingAttack is null && result.ResultingHealth is null &&
                result.Components[0].ObservedCost is null, "Missing frozen observations were filled from print/current entities.");
            frame = Replace(Frame(), Entity(60, "CRAFT_B", "SETASIDE", 1, ("COST", -1), ("ATK", -1), ("HEALTH", 0)));
            result = Evaluate(frame, cards);
            Check(result.ResultingCost is null && result.ResultingAttack is null && result.ResultingHealth is null,
                "Invalid component statistics were reused.");
        });
        yield return ("craft hand addition follows observed capacity and never invents an upper bound", () =>
        {
            var full = Frame();
            full = Replace(full, Player(1, 1, 1, 1)); full = Replace(full, Entity(80, "CRAFT_A", "HAND", 1));
            Check(Evaluate(full, cards).AddedToHand == false, "Full hand gained a craft card.");
            var missing = Replace(Frame(), Player(1, 1, 0, null));
            Check(Evaluate(missing, cards).AddedToHand is null, "Missing hand limit defaulted to ten.");
            var conflict = Replace(Frame(), Player(1, 1, 2, 10));
            Check(Evaluate(conflict, cards).AddedToHand is null, "Conflicting hand count was used.");
        });
        yield return ("craft requires ledger match state event completeness and untruncated evidence", () =>
        {
            var frame = Frame();
            var variants = new[]
            {
                frame with { ChoiceHistory = null },
                Bind(frame, [Entry()], match: "another"), Bind(frame, [Entry()], state: 9), Bind(frame, [Entry()], sequence: 99),
                Bind(frame, [Entry()], complete: false), Bind(frame, [Entry()], truncated: 1),
                Bind(frame, [Entry()], warnings: ["untrusted warning must not be echoed"]),
                frame with { Choice = frame.Choice! with { OfferedAtEventSequence = 0 } },
                frame with { Choice = frame.Choice! with { SourceCardId = null } }
            };
            foreach (var variant in variants) Unconfirmed(Evaluate(variant, cards));
            Check(!Evaluate(variants[6], cards).Unknowns.Any(value => value.Contains("untrusted", StringComparison.Ordinal)),
                "Untrusted ledger text escaped through unknowns.");
        });
        yield return ("craft does not connect prior components across another offer or conflicting sequences", () =>
        {
            var frame = Frame();
            foreach (var entry in new[] { Entry(id: 9), Entry(offered: 0), Entry(offered: 80, chosen: 79), Entry(chosen: 90), Entry(chosen: 101) })
                Unconfirmed(Evaluate(Bind(frame, [entry]), cards));
            Unconfirmed(Evaluate(Bind(frame, [Entry(), Entry()]), cards));
            Unconfirmed(Evaluate(Bind(frame, [Entry(), Entry(id: 8, offered: 84, chosen: 85)]), cards));
            var other = Entry(id: 8, source: 31, offered: 84, chosen: 85);
            var interleaved = Bind(frame, [Entry(), other]) with { Choice = frame.Choice! with { PreviousOfferedAtEventSequence = 84 } };
            Unconfirmed(Evaluate(interleaved, cards));
            Unconfirmed(Evaluate(frame with { Choice = frame.Choice! with { PreviousOfferedAtEventSequence = 0 } }, cards));
        });
        yield return ("craft validates frozen prior choice ownership identities counts and chosen membership", () =>
        {
            var frame = Frame();
            var badEntries = new[]
            {
                Entry(player: 2), Entry(type: "MULLIGAN"), Entry(sourceCard: "BE_044"), Entry(min: 0), Entry(max: 2),
                Entry(complete: false), Entry(chosenIds: []), Entry(chosenIds: [50, 51]), Entry(chosenIds: [999]),
                Entry(options: [new(50, "CRAFT_A", 2, 1)]), Entry(options: [new(50, null, 1, 1)]),
                Entry(options: [new(50, "CRAFT_A", 1, 1), new(50, "CRAFT_A", 1, 2)]),
                Entry(options: [new(60, "CRAFT_B", 1, 1)], entities: [], chosenIds: [60]),
                Entry(entities: [Entity(99, "CRAFT_A", "SETASIDE", 1)]),
                Entry(entities: [Entity(50, "CRAFT_A", "SETASIDE", 1), Entity(50, "CRAFT_A", "SETASIDE", 1)])
            };
            foreach (var entry in badEntries) Unconfirmed(Evaluate(Bind(frame, [entry]), cards));
        });
        yield return ("craft rejects damaged wrong-zone wrong-kind and conflicting candidate observations", () =>
        {
            var frame = Frame();
            var bad = new[]
            {
                Entity(50, "CRAFT_A", "SETASIDE", 2), Entity(50, "CRAFT_B", "SETASIDE", 1),
                Entity(50, "CRAFT_A", "HAND", 1), Entity(50, "CRAFT_A", "DECK", 1),
                Entity(50, "CRAFT_A", "SETASIDE", 1, ("CARDTYPE", 5)),
                Entity(50, "CRAFT_A", "SETASIDE", 1, ("CARDRACE", 14)),
                Entity(50, "CRAFT_A", "SETASIDE", 1, ("DAMAGE", 1), ("HEALTH", 4)),
                Entity(50, "CRAFT_A", "SETASIDE", 1, ("DAMAGE", -1)),
                Entity(50, "CRAFT_A", "SETASIDE", 1) with { Kind = EntityKind.Player },
                Entity(50, "CRAFT_A", "SETASIDE", 1) with { Tags = null! }
            };
            foreach (var candidate in bad)
            {
                Unconfirmed(Evaluate(Bind(frame, [Entry(entities: [candidate])]), cards));
                Unconfirmed(Evaluate(Replace(frame, candidate with { Id = 60, CardId = candidate.CardId == "CRAFT_B" ? "CRAFT_A" : "CRAFT_B" }), cards));
            }
        });
        yield return ("craft requires fixed Beast minion records and accepts dual-type Beast components", () =>
        {
            foreach (var raw in new[] { "{}", "null", "[]", "broken", "{\"race\":\"UNDEAD\"}", "{\"races\":[\"DRAGON\"]}" })
            {
                var changed = cards.ToDictionary(); changed["CRAFT_A"] = changed["CRAFT_A"] with { RawJson = raw };
                Unconfirmed(Evaluate(Frame(), changed));
            }
            var spell = cards.ToDictionary(); spell["CRAFT_B"] = spell["CRAFT_B"] with { Type = "SPELL" };
            Unconfirmed(Evaluate(Frame(), spell));
            var dual = cards.ToDictionary(); dual["CRAFT_A"] = dual["CRAFT_A"] with { RawJson = "{\"race\":\"UNDEAD\",\"races\":[\"UNDEAD\",\"BEAST\"]}" };
            Check(Evaluate(Frame(), dual).Stage == "ComponentTwo", "Dual Beast/Undead was mistaken for a non-Beast.");
        });
        yield return ("assemble is not a zombeast and never guesses the four-cost primary or completed stage", () =>
        {
            var frame = Frame() with { Choice = Frame().Choice! with { SourceCardId = "BE_044" } };
            frame = Replace(frame, Entity(30, "BE_044", "PLAY", 1));
            frame = Replace(frame, Entity(80, "CRAFT_A", "HAND", 1, ("CREATOR", 30), ("COST", 4), ("ATK", 7), ("HEALTH", 8)));
            var result = Evaluate(frame, cards);
            Unconfirmed(result);
            Check(result.Mechanism == "Assemble" && result.PrimaryEntityId is null && result.GrantedCostReduction is null &&
                result.ResultingCost is null && result.ResultingAttack is null && result.ResultingHealth is null &&
                result.RuleCoverage.Contains("BE_044-assemble-is-not-zombeast"), "Assemble guessed primary, merged stats or used a Zombeast reward.");
        });
        yield return ("craft rejects unsupported builds sources perspectives and non-current selections", () =>
        {
            var frame = Frame();
            foreach (var changed in new[] { frame with { ClientBuild = "other" }, frame with { IsSpectating = true },
                frame with { Phase = GamePhase.Main }, Replace(frame, Entity(30, "TLC_EVENT_400", "HAND", 1)),
                Replace(frame, Entity(30, "TLC_EVENT_400", "PLAY", 2)), Replace(frame, Entity(30, "BE_044", "PLAY", 1)) })
                Unconfirmed(Evaluate(changed, cards));
            Unconfirmed(CraftChoiceRules.Evaluate(frame, 999, cards));
        });
        yield return ("craft outcomes and component collections are immutable projections", () =>
        {
            var result = Evaluate(Frame(), cards);
            try { ((IList<CraftComponentOutcome>)result.Components).Clear(); throw new Exception("Mutable component outcomes."); }
            catch (NotSupportedException) { }
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable craft unknowns."); }
            catch (NotSupportedException) { }
        });
        yield return ("choice evaluator exposes craft consequences without treating a component as the generated hand card", () =>
        {
            var evaluator = new LineEvaluator();
            var first = evaluator.EvaluateChoice(Frame(false), [60], cards, 9);
            var second = evaluator.EvaluateChoice(Frame(), [60], cards, 9);
            Check(first.Verification == LineVerification.Conditional && first.Crafting?.Stage == "ComponentOne" &&
                first.CardOutcomes.Count == 0 && first.ResolvesEffects && !first.SelectionOnly &&
                first.HandCountBefore == 0 && first.HandCountAfter == 0 && !first.CanEvaluateFollowupActions,
                "First component was exposed as an acquired card or lost its zero hand delta.");
            Check(second.Verification == LineVerification.Conditional && second.Crafting?.Stage == "ComponentTwo" &&
                second.CardOutcomes.Count == 0 && second.ResolvesEffects && !second.SelectionOnly &&
                second.HandCountBefore == 0 && second.HandCountAfter == 1 && !second.CanEvaluateFollowupActions,
                "Generated craft consequences were not integrated with selection/hand delta boundaries.");
        });
    }

    private static CraftChoiceOutcome Evaluate(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards) =>
        CraftChoiceRules.Evaluate(frame, 60, cards);

    private static CardRecord Card(string id) => new(id, null, id, "printed", "NEUTRAL", "TEST", "MINION", 88,
        false, [], [], "{\"race\":\"BEAST\",\"attack\":88,\"health\":88,\"cost\":88}");

    private static GameSnapshot Frame(bool second = true)
    {
        var frame = new GameSnapshot("craft-fixture", 10, 100, 5, 1, 1, GamePhase.Choice,
            new[] { Player(1, 1), Player(2, 2), Entity(30, "TLC_EVENT_400", "GRAVEYARD", 1, ("QUEST_PROGRESS", 3), ("QUEST_PROGRESS_TOTAL", 3)),
                Entity(60, "CRAFT_B", "SETASIDE", 1, ("COST", 4), ("ATK", 5), ("HEALTH", 6)) }.ToFrozenDictionary(entity => entity.Id),
            new(true, true, true, true, 0, []))
        {
            ClientBuild = "255790",
            Choice = new(9, 1, "GENERAL", 30, 1, 1, [new(60, "CRAFT_B", 1, 1)], true)
            { OfferedAtEventSequence = 90, PreviousOfferedAtEventSequence = second ? 80 : 0, SourceCardId = "TLC_EVENT_400" }
        };
        return Bind(frame, second ? [Entry()] : []);
    }

    private static ChoiceHistoryEntry Entry(int id = 7, int player = 1, string type = "GENERAL", int source = 30,
        string sourceCard = "TLC_EVENT_400", int min = 1, int max = 1, long offered = 80, long chosen = 81,
        IReadOnlyList<ChoiceOptionSnapshot>? options = null, IReadOnlyList<EntitySnapshot>? entities = null,
        IReadOnlyList<int>? chosenIds = null, bool complete = true) =>
        new(id, player, type, source, sourceCard, min, max, offered, chosen,
            options ?? [new(50, "CRAFT_A", 1, 1)],
            entities ?? [Entity(50, "CRAFT_A", "SETASIDE", 1, ("COST", 2), ("ATK", 3), ("HEALTH", 4))],
            chosenIds ?? [50], complete);

    private static GameSnapshot Bind(GameSnapshot frame, IReadOnlyList<ChoiceHistoryEntry> entries,
        string? match = null, long? state = null, long? sequence = null, bool complete = true, long truncated = 0,
        IReadOnlyList<string>? warnings = null) => frame with
    { ChoiceHistory = new(match ?? frame.MatchId, state ?? frame.StateVersion, sequence ?? frame.EventSequence, entries, complete, truncated, warnings ?? []) };

    private static EntitySnapshot Player(int id, int player, int count = 0, int? limit = 10)
    {
        var tags = new Dictionary<string, string> { ["PLAYER_ID"] = player.ToString(), ["NUM_CARDS_IN_HAND"] = count.ToString() };
        if (limit.HasValue) tags["MAXHANDSIZE"] = limit.Value.ToString();
        return new(id, null, EntityKind.Player, tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Entity(int id, string card, string zone, int owner, params (string Key, int Value)[] more)
    {
        var tags = new Dictionary<string, string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = zone };
        foreach (var (key, value) in more) tags[key] = value.ToString();
        return new(id, card, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static void Unconfirmed(CraftChoiceOutcome result) => Check(result.Stage == "Unconfirmed" && !result.ResolvesEffects &&
        result.AddedToHand is null && result.ResultingCost is null && result.ResultingAttack is null && result.ResultingHealth is null &&
        result.GrantedCostReduction is null && result.RequiresAnotherChoice is null && result.Unknowns.Count > 0, "Unconfirmed evidence resolved a craft effect.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
