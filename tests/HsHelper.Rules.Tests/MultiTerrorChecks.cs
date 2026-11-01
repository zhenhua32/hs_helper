using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using HsHelper.Core;
using HsHelper.Rules;

public static partial class TerrorEndTurnResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> MultiCases()
    {
        var cards = Cards();
        TerrorEndTurnOutcome Eval(GameSnapshot frame, TerrorEndTurnState? overlay = null) => TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, overlay);
        yield return ("multi Terror correlated terminal states match an independent permutation oracle", () =>
        {
            for (var attack = 1; attack <= 3; attack++)
                for (var health = 1; health <= 3; health++)
                    foreach (var shield in new[] { false, true })
                    {
                        var frame = Replace(Two(), Card(30, "CORE_TTN_866", 1, attack, health), Card(40, "CORE_TTN_866", 1, 2, 6),
                            Card(21, "CS2_172", 2, 2, 3), Card(22, "CORE_CS2_231", 2, 1, 4), Hero(10, 1, 17));
                        if (shield) frame = Tag(frame, 21, "DIVINE_SHIELD", "1");
                        var result = Eval(frame);
                        Check(result.IsEnumerationComplete && result.SourceEntityIds!.SequenceEqual([30, 40]), "Two actual sources did not produce complete joint conditional branches.");
                        Check(result.Branches.Select(TerminalKey).ToHashSet().SetEquals(Oracle(frame, [30, 40])), "Joint terminals differ from independent full schedule replay.");
                        Check(result.Branches.All(ValidStages), "A stage witness does not cover exactly its shared combat slice.");
                        var otherEntry = TerrorEndTurnResolutionRules.Evaluate(frame, 40, cards);
                        Check(otherEntry.TerrorEntityId == 40 && otherEntry.SourceEntityIds!.SequenceEqual([30, 40]) &&
                            otherEntry.Branches.Select(TerminalKey).ToHashSet().SetEquals(result.Branches.Select(TerminalKey)), "Selecting the other actual source changed joint physics or its complete source set.");
                    }
        });
        yield return ("multi Terror shares shields survivors and repeated forced attackers across source stages", () =>
        {
            var frame = Replace(Two(), Card(30, "CORE_TTN_866", 1, 1, 1), Card(40, "CORE_TTN_866", 1, 2, 20), Card(21, "CS2_172", 2, 5, 20));
            frame = frame with { Entities = frame.Entities.Where(pair => pair.Key != 22).ToFrozenDictionary() };
            frame = Tag(frame, 21, "DIVINE_SHIELD", "1"); var before = JsonSerializer.Serialize(frame); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.StopReason == "AllSourcesResolved" && branch.Combats.Count == 2 &&
                branch.Combats.All(combat => combat.AttackerEntityId == 21) && branch.Combats.Count(combat => combat.DamageToAttacker == 0) == 1 &&
                branch.Stages!.Any(stage => stage.SourceEntityId == 30 && stage.StopReason == "TerrorDied") && branch.SourceOrder!.Count == 2 && ValidStages(branch)),
                "The first source death stopped the whole turn, revived a shield or omitted a surviving repeated attacker.");
            Check(JsonSerializer.Serialize(frame) == before && !result.HasAuthoritativeNextState && !result.CanEvaluateFollowupActions && result.IsLegal is null,
                "Conditional witnesses mutated the current snapshot or became an actionable successor.");
        });
        yield return ("multi Terror lifesteal uses one shared actual hero cap and per-source current keywords", () =>
        {
            var frame = Replace(Two(), Hero(10, 1, 29), Card(30, "CORE_TTN_866", 1, 2, 50), Card(40, "CORE_TTN_866", 1, 7, 50));
            var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.OwnHeroHealthAfter == 30 && branch.LifestealHealing == 1),
                "Each source independently reset the shared maximum or reported nominal rather than actual healing.");
            frame = Tag(Tag(frame, 30, "LIFESTEAL", "0"), 40, "LIFESTEAL", "0");
            Check(Eval(frame).Branches.All(branch => branch.OwnHeroHealthAfter == 29 && branch.LifestealHealing == 0), "Printed lifesteal overrode actual per-source zero.");
        });
        yield return ("multi Terror supports three through seven sources and rejects an eighth without dropping it", () =>
        {
            for (var count = 3; count <= 7; count++)
            {
                var frame = Replace(Frame(), Enumerable.Range(0, count).Select(index => Card(30 + index, "CORE_TTN_866", 1, 0, 100)).ToArray());
                frame = Replace(frame, Card(21, "CS2_172", 2, 0, 100), Card(22, "CORE_CS2_231", 2, 0, 100));
                var result = Eval(frame);
                Check(result.IsEnumerationComplete && result.SourceEntityIds!.Count == count && result.Branches.All(branch =>
                    branch.SourceOrder!.Count == count && branch.Stages!.Count == count && branch.Combats.Count == count * 2 && ValidStages(branch)),
                    "Ordinary finite multi-source state was artificially limited to two sources or lost a stage.");
            }
            var tooMany = Replace(Frame(), Enumerable.Range(0, 8).Select(index => Card(30 + index, "CORE_TTN_866", 1, 0, 100)).ToArray());
            Check(!Eval(tooMany).ResolvesBranches && Eval(tooMany).Branches.Count == 0, "An over-cap board silently omitted a source.");
        });
        yield return ("multi Terror unknown death reborn and after-attack stop the entire joint prefix", () =>
        {
            foreach (var flag in new[] { "DEATHRATTLE", "REBORN" })
            {
                var frame = Replace(Two(), Card(21, "CS2_172", 2, 0, 1), Card(22, "CORE_CS2_231", 2, 0, 1)); frame = Tag(Tag(frame, 21, flag, "1"), 22, flag, "1");
                var result = Eval(frame);
                Check(!result.IsEnumerationComplete && result.Branches.Count > 0 && result.Branches.All(branch => !branch.IsComplete && branch.SourceOrder!.Count == 1 &&
                    branch.Stages!.Count == 1 && !branch.Stages[0].IsComplete && branch.Combats.Count == 1 && ValidStages(branch)), "An unknown death spawned a later source stage or a complete global result.");
            }
            var magma = Replace(Two(), Card(21, "FIR_953", 2, 1, 100));
            magma = magma with { Entities = magma.Entities.Where(pair => pair.Key != 22).ToFrozenDictionary() };
            Check(Eval(magma).Branches.All(branch => !branch.IsComplete && branch.SourceOrder!.Count == 1 && branch.StopReason == "AfterAttackRequiresObservation"), "An unknown random after-attack effect was crossed.");
        });
        yield return ("multi Terror global budgets truncate honestly and expose actual serialized output size", () =>
        {
            var result = Eval(MultiBudgetFrame());
            Check(!result.IsEnumerationComplete && !result.ResolvesBranches && result.Unknowns.Count > 0 && result.Branches.Count is > 0 and <= 256 && result.SourceEntityIds!.Count == 3 &&
                result.Branches.All(branch => branch.Combats.Count <= 21 && ValidStages(branch)), "The global budget reset per source or claimed a complete omitted set.");
            Console.WriteLine($"MULTI_TERROR_BYTES={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result))}; BRANCHES={result.Branches.Count}");
            var line = new LineEvaluator().EvaluateLine(Tag(Tag(MultiBudgetFrame(), 2, "RESOURCES", "10"), 2, "RESOURCES_USED", "0"), [new(ActionKind.EndTurn, 2)], cards);
            Console.WriteLine($"MULTI_TERROR_LINE_BYTES={Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(line))}; PREVIEWS={line.Result.TerrorEndTurnPreviews.Count}");
            Check(line.Result.TerrorEndTurnPreviews.Count == 1, "The actual high-branch line never reached its joint preview.");
        });
        yield return ("multi Terror overlays and line end-turn publish one joint preview while retaining pretrigger state", () =>
        {
            var frame = Tag(Tag(Two(), 2, "RESOURCES", "10"), 2, "RESOURCES_USED", "0");
            var result = new LineEvaluator().EvaluateLine(frame, [new(ActionKind.EndTurn, 2)], cards);
            Check(result.Result.TerrorEndTurnPreviews.Count == 1 && result.Result.TerrorEndTurnPreviews[0].SourceEntityIds!.Count == 2 && !result.IsLethal &&
                result.Result.Entities.Single(entity => entity.EntityId == 30).RemainingHealth == 5 && result.Result.Entities.Single(entity => entity.EntityId == 21).RemainingHealth == 5,
                "Line omitted the joint preview, concatenated independent previews or applied a favorable branch.");
            var overlay = State(Tag(frame, 21, "DAMAGE", "1")); Check(Eval(frame, overlay).IsEnumerationComplete, "A legal same-bound host prefix was refused.");
            Check(!Eval(frame, overlay with { EventSequence = frame.EventSequence + 1 }).ResolvesBranches, "A future overlay was accepted.");
            var missing = Remove(frame, 40, "DAMAGE"); Check(!Eval(missing, State(frame)).ResolvesBranches, "Overlay repaired missing original observations of another source.");
            var original = Tag(frame, 40, "ZONE", "HAND"); original = Tag(original, 40, "COST", "7");
            Check(Eval(original, State(Tag(original, 40, "ZONE", "PLAY"))).IsEnumerationComplete, "An actual known hand entity legally projected into PLAY was not included.");
            var played = new LineEvaluator().EvaluateLine(original, [new(ActionKind.PlayCard, 40), new(ActionKind.EndTurn, 2)], cards);
            Check(played.Result.TerrorEndTurnPreviews.Single().SourceEntityIds!.SequenceEqual([30, 40]), "A legal planned hand source was not included in the actual Line overlay.");
        });
        yield return ("multi Terror rejects counterevidence and hidden sources while selected inactive stays legacy", () =>
        {
            foreach (var frame in new[] { Tag(Two(), 40, "202", "SPELL"), Remove(Two(), 40, "CONTROLLER"), Tag(Two(), 40, "4573", "97376"), Tag(Two(), 40, "IMMUNE", "1"), Tag(Two(), 40, "UNTOUCHABLE", "1") })
                Check(!Eval(frame).ResolvesBranches, "A conflicting source disappeared from the joint model.");
            var restricted = Tag(Tag(Tag(Two(), 40, "UNTOUCHABLE", "1"), 2, "RESOURCES", "10"), 2, "RESOURCES_USED", "0");
            var refused = new LineEvaluator().EvaluateLine(restricted, [new(ActionKind.EndTurn, 2)], cards);
            Check(refused.Result.ForcedAttacks.Count == 0 && refused.Result.TerrorEndTurnPreviews.Single().Branches.Count == 0,
                "Line silently omitted a restricted source and applied the remaining source as a single direct exchange.");
            foreach (var tag in new[] { "EXTRA_END_TURN_EFFECT", "EXTRA_TURN_END_TRIGGERS" })
            {
                Check(!Eval(Tag(Two(), 2, tag, "1")).ResolvesBranches, "An unknown turn-end multiplier became a second native stage.");
                Check(Eval(Tag(Two(), 2, tag, "0")).IsEnumerationComplete, "An expired zero multiplier was required for ordinary multi-source behavior.");
            }
            var inactive = Eval(Tag(Two(), 30, "SILENCED", "1"));
            Check(inactive.Branches.Single().StopReason == "InactiveSource" && inactive.SourceEntityIds is null, "Selecting an inactive source silently switched the entry point to another source.");
            var hidden = Change(Card(90, "PRIVATE_SOURCE_TOKEN", 2, 99, 99), "ZONE", "HAND");
            Check(!JsonSerializer.Serialize(Eval(Replace(Two(), hidden))).Contains("PRIVATE_SOURCE_TOKEN"), "A hidden entity entered joint witnesses.");
            var dormant = Eval(Tag(Two(), 22, "DORMANT", "1"));
            Check(dormant.IsEnumerationComplete && dormant.Branches.All(branch => branch.Combats.All(combat => combat.AttackerEntityId != 22)),
                "A later source restarted a dormant enemy as a forced attacker.");
            var empty = Two() with { Entities = Two().Entities.Where(pair => pair.Key is not (21 or 22)).ToFrozenDictionary() };
            Check(Eval(empty).Branches.Single().Stages!.All(stage => stage.CombatCount == 0), "No-attacker source stages were invented or omitted.");
            var noEnemies = new LineEvaluator().EvaluateLine(Tag(Tag(empty, 2, "RESOURCES", "10"), 2, "RESOURCES_USED", "0"), [new(ActionKind.EndTurn, 2)], cards);
            Check(noEnemies.Result.TerrorEndTurnPreviews.Count == 0 && noEnemies.Result.ForcedAttacks.Count == 0, "An enemy-free Line lost its legacy no-op boundary.");
        });
        yield return ("multi Terror optional evidence lists are frozen and legacy single JSON has no new fields", () =>
        {
            var legacy = Eval(Frame()); var json = JsonSerializer.Serialize(legacy);
            Check(!json.Contains("SourceEntityIds") && !json.Contains("SourceOrder") && !json.Contains("Stages") && JsonSerializer.Deserialize<TerrorEndTurnOutcome>(json)!.SourceEntityIds is null,
                "Single-source JSON or absent legacy evidence was rewritten as multi-source proof.");
            var multi = Eval(Two()); var ids = new List<int> { 30, 40 }; var order = new List<int> { 40, 30 };
            var stages = new List<TerrorSourceStage> { new(40, 0, 0, true, "AllEligibleAttackersResolved") };
            var frozen = multi with { SourceEntityIds = ids }; var branch = multi.Branches[0] with { SourceOrder = order, Stages = stages };
            ids.Clear(); order.Clear(); stages.Clear(); Check(frozen.SourceEntityIds!.Count == 2 && branch.SourceOrder!.Count == 2 && branch.Stages!.Count == 1, "Evidence aliased caller collections.");
            try { ((IList<int>)multi.SourceEntityIds!).Clear(); throw new Exception("Mutable source IDs."); } catch (NotSupportedException) { }
            try { ((IList<int>)multi.Branches[0].SourceOrder!).Clear(); throw new Exception("Mutable source order."); } catch (NotSupportedException) { }
            try { ((IList<TerrorSourceStage>)multi.Branches[0].Stages!).Clear(); throw new Exception("Mutable stage list."); } catch (NotSupportedException) { }
        });
    }

    private static GameSnapshot Two() => Replace(Frame(), Card(40, "CORE_TTN_866", 1, 2, 10));
    private static GameSnapshot MultiBudgetFrame()
    {
        var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 1, 65), Card(40, "CORE_TTN_866", 1, 2, 48), Card(41, "CORE_TTN_866", 1, 3, 33));
        foreach (var id in new[] { 30, 40, 41 }) frame = Tag(frame, id, "DIVINE_SHIELD", "1");
        return Replace(frame, Enumerable.Range(0, 7).Select(index => Card(21 + index, "CS2_172", 2, 1 << index, 100)).ToArray());
    }
    private static bool ValidStages(TerrorEndTurnBranch branch)
    {
        if (branch.SourceOrder is null || branch.Stages is null || !branch.SourceOrder.SequenceEqual(branch.Stages.Select(stage => stage.SourceEntityId)) ||
            branch.SourceOrder.Distinct().Count() != branch.SourceOrder.Count) return false;
        var next = 0;
        foreach (var stage in branch.Stages)
        {
            if (stage.FirstCombatIndex != next || stage.CombatCount < 0 || stage.FirstCombatIndex + stage.CombatCount > branch.Combats.Count ||
                branch.Combats.Skip(next).Take(stage.CombatCount).Any(combat => combat.TargetEntityId != stage.SourceEntityId)) return false;
            next += stage.CombatCount;
        }
        return next == branch.Combats.Count;
    }
    private static string TerminalKey(TerrorEndTurnBranch branch) => branch.LifestealHealing + ":" + string.Join(';', branch.Entities.OrderBy(row => row.EntityId)
        .Select(row => $"{row.EntityId},{row.RemainingHealth},{row.RemainingArmor},{row.DivineShield}"));
    private sealed record OracleBody(int Id, int Owner, int Attack, int Maximum, long Health, long Armor, bool Shield, bool Life);
    // An independent complete-order oracle: no state merging, stage machine or product evaluator calls.
    private static HashSet<string> Oracle(GameSnapshot frame, int[] sources)
    {
        var initial = frame.Entities.Values.Where(entity => entity.Tags.GetValueOrDefault("CARDTYPE") is "MINION" or "HERO").ToDictionary(entity => entity.Id,
            entity => new OracleBody(entity.Id, entity.ControllerId!.Value, entity.GetIntTag("ATK")!.Value, entity.GetIntTag("HEALTH")!.Value,
                entity.GetIntTag("HEALTH")!.Value - entity.GetIntTag("DAMAGE")!.Value, entity.GetIntTag("ARMOR") ?? 0,
                entity.GetIntTag("DIVINE_SHIELD") == 1, entity.CardId == "CORE_TTN_866"));
        var enemies = initial.Values.Where(body => body.Owner == 2 && body.Id != 11).Select(body => body.Id).ToArray();
        var results = new HashSet<string>();
        foreach (var sourceOrder in Permutations(sources)) Replay(sourceOrder, 0, initial, 0);
        return results;
        void Replay(int[] sourceOrder, int stage, Dictionary<int, OracleBody> state, long healing)
        {
            if (stage == sources.Length)
            { results.Add(healing + ":" + string.Join(';', state.Values.OrderBy(body => body.Id).Select(body => $"{body.Id},{body.Health},{body.Armor},{body.Shield}"))); return; }
            var sourceId = sourceOrder[stage];
            foreach (var attackOrder in Permutations(enemies))
            {
                var next = state.ToDictionary(); var actualHealing = healing;
                foreach (var enemyId in attackOrder)
                {
                    var terror = next[sourceId]; var enemy = next[enemyId];
                    if (terror.Health <= 0) break;
                    if (enemy.Health <= 0) continue;
                    var outgoing = Strike(terror, enemy.Attack); var retaliation = Strike(enemy, terror.Attack);
                    next[sourceId] = outgoing.After; next[enemyId] = retaliation.After;
                    if (terror.Life && retaliation.Dealt > 0)
                    {
                        var hero = next[10]; var after = Math.Min(hero.Maximum, hero.Health + retaliation.Dealt);
                        actualHealing += after - hero.Health; next[10] = hero with { Health = after };
                    }
                }
                Replay(sourceOrder, stage + 1, next, actualHealing);
            }
        }
        static (long Dealt, OracleBody After) Strike(OracleBody recipient, int amount) => amount == 0 ? (0, recipient) :
            recipient.Shield ? (0, recipient with { Shield = false }) :
            (amount, recipient with { Armor = Math.Max(0, recipient.Armor - amount), Health = recipient.Health - Math.Max(0, amount - recipient.Armor) });
    }
    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length == 0) { yield return []; yield break; }
        foreach (var item in items)
            foreach (var tail in Permutations(items.Where(value => value != item).ToArray())) yield return [item, .. tail];
    }
}
