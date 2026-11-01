using System.Collections.Frozen;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class DeathrattleDrawPolicyChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("deathrattle draw schema accepts only a current source entity and rejects invented trigger state", () => Run(Schema));
        yield return ("turn and choice policies distinguish a single deathrattle preview from hand-play draw and executable actions", () => Run(Policies));
        yield return ("deck planning cannot register or directly invoke the deathrattle draw tool", Planning);
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static void Schema()
    {
        var definition = SnapshotCoach.Definitions.Single(tool => tool.Name == "evaluate_deathrattle_draw");
        Check(definition.Parameters["additionalProperties"]?.GetValue<bool>() == false &&
            definition.Parameters["properties"] is JsonObject { Count: 1 } properties && properties.ContainsKey("source_entity_id") &&
            definition.Parameters["required"] is JsonArray { Count: 1 } required && required[0]?.ToString() == "source_entity_id" &&
            definition.ValidateArguments(new() { ["source_entity_id"] = 30 }), "The direct tool lost its single host-bound entity contract.");
        foreach (var value in new JsonNode?[] { null, JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create("30"), JsonValue.Create(true), JsonValue.Create(30.5) })
            Check(!definition.ValidateArguments(new() { ["source_entity_id"] = value }), "An invalid source identifier entered the direct preview.");
        foreach (var key in new[] { "enchantment_entity_id", "trigger_count", "hand_count", "deck_count", "fatigue", "assume_death", "source_card_id" })
            Check(!definition.ValidateArguments(new() { ["source_entity_id"] = 30, [key] = 2 }), "Model arguments supplied trigger or count evidence: " + key);
    }

    private static void Policies()
    {
        foreach (var kind in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
        {
            var policy = AdviceTaskTemplates.Get(kind);
            Check(policy.Version == "hunter-v19/" + kind.ToString().ToLowerInvariant() &&
                policy.Instructions.Contains("evaluate_deathrattle_draw", StringComparison.Ordinal) &&
                policy.Instructions.Contains("EDR_100t4e", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不证明死亡已经发生", StringComparison.Ordinal) &&
                policy.Instructions.Contains("腾出手牌位置", StringComparison.Ordinal) &&
                policy.Instructions.Contains("DrawBoundaryReached", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不能据此认证完整结算或已验证斩杀", StringComparison.Ordinal),
                "A task presented a conditional deathrattle count as an observed death, empty hand slot or executable followup.");
        }
        Check(AdviceTaskTemplates.Version == "hunter-v5" && new[] { AdviceTaskKind.Mulligan, AdviceTaskKind.Review, AdviceTaskKind.DeckPlan }
            .All(kind => AdviceTaskTemplates.Get(kind).Version == "hunter-v5/" + kind.ToString().ToLowerInvariant()),
            "An unrelated task's policy version changed.");
    }

    private static async Task Planning()
    {
        var snapshot = new GameSnapshot("deathrattle-planning-policy", 1, 1, 1, 1, 1, GamePhase.Main,
            FrozenDictionary<int, EntitySnapshot>.Empty, new(true, true, true, true, 0, []));
        var context = CompactAdviceContextBuilder.Create(snapshot, "255790", AdviceTaskKind.DeckPlan);
        var catalog = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-deathrattle-policy", Guid.NewGuid().ToString("N"), "unopened.db"));
        var coach = new SnapshotCoach(snapshot, context.Snapshot, catalog, "255790", taskKind: AdviceTaskKind.DeckPlan);
        Check(coach.Capabilities().Tools.Count == 0 && coach.Capabilities().ValidateActions is null, "Planning registered a live deathrattle capability.");
        try { await coach.ExecuteAsync("evaluate_deathrattle_draw", new() { ["source_entity_id"] = 30 }, CancellationToken.None); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Direct invocation bypassed the planning gate.");
    }
}
