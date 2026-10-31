using System.Text.Json.Nodes;

namespace HsHelper.Agent;

/// <summary>Fixed final-answer contracts; provider formatting never replaces local action validation.</summary>
internal static class ModelOutputSchema
{
    private static readonly string[] StrategyFields =
        ["summary", "reasons", "alternative", "risks", "assumptions", "unknowns", "unsupported_mechanics"];
    private static readonly string[] ActionFields = ["kind", "source_entity_id", "target_entity_id"];
    private static readonly string[] ActionKinds = ["play", "attack", "hero_power", "end_turn", "choose"];
    private const string Error = "模型建议未符合所选严格输出结构。";

    internal static JsonObject? CreateResponseFormat(ModelResponseFormat format, bool allowsActions) => format switch
    {
        ModelResponseFormat.Omitted => null,
        ModelResponseFormat.JsonObject => new() { ["type"] = "json_object" },
        ModelResponseFormat.JsonSchema => new()
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = allowsActions ? "hs_helper_actions_v1" : "hs_helper_strategy_v1",
                ["strict"] = true,
                ["schema"] = CreateSchema(allowsActions)
            }
        },
        _ => throw new ArgumentException("模型输出模式无效。")
    };

    private static JsonObject StringSchema() => new() { ["type"] = "string", ["maxLength"] = 2000 };
    private static JsonObject TextArraySchema() => new()
    { ["type"] = "array", ["items"] = StringSchema(), ["maxItems"] = 12 };
    private static JsonArray Names(IEnumerable<string> fields) => new(fields.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    private static JsonObject CreateSchema(bool allowsActions)
    {
        var fields = allowsActions ? StrategyFields.Append("actions").ToArray() : StrategyFields;
        var properties = new JsonObject
        {
            ["summary"] = StringSchema(),
            ["reasons"] = TextArraySchema(),
            ["alternative"] = new JsonObject { ["type"] = Names(["string", "null"]), ["maxLength"] = 2000 },
            ["risks"] = TextArraySchema(),
            ["assumptions"] = TextArraySchema(),
            ["unknowns"] = TextArraySchema(),
            ["unsupported_mechanics"] = TextArraySchema()
        };
        if (allowsActions)
            properties["actions"] = new JsonObject
            {
                ["type"] = "array", ["maxItems"] = 30,
                ["items"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = Names(ActionFields),
                    ["properties"] = new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = Names(ActionKinds) },
                        ["source_entity_id"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["target_entity_id"] = new JsonObject { ["type"] = Names(["integer", "null"]), ["minimum"] = 1 }
                    }
                }
            };
        return new() { ["type"] = "object", ["properties"] = properties, ["required"] = Names(fields), ["additionalProperties"] = false };
    }

    // A fixed validator keeps strict-mode responses honest even when a compatible provider
    // ignores response_format. Legacy prompt-only and json_object parsing remain unchanged.
    internal static void ValidateResponse(JsonObject advice, bool allowsActions)
    {
        try { ValidateResponseCore(advice, allowsActions); }
        // JsonObject lazily rejects duplicate keys while inspecting fields. Do not echo
        // an untrusted duplicate property name from ArgumentException into diagnostics.
        catch (ArgumentException) { throw new InvalidDataException(Error); }
    }

    private static void ValidateResponseCore(JsonObject advice, bool allowsActions)
    {
        var fields = allowsActions ? StrategyFields.Append("actions").ToArray() : StrategyFields;
        RequireFields(advice, fields);
        RequireString(advice["summary"]);
        if (advice["alternative"] is { } alternative) RequireString(alternative);
        foreach (var name in StrategyFields.Where(x => x is not ("summary" or "alternative")))
        {
            if (advice[name] is not JsonArray { Count: <= 12 } array) throw new InvalidDataException(Error);
            foreach (var entry in array) RequireString(entry);
        }
        if (!allowsActions) return;
        if (advice["actions"] is not JsonArray { Count: <= 30 } actions) throw new InvalidDataException(Error);
        foreach (var node in actions)
        {
            if (node is not JsonObject action) throw new InvalidDataException(Error);
            RequireFields(action, ActionFields);
            if (action["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var text) ||
                !ActionKinds.Contains(text, StringComparer.Ordinal)) throw new InvalidDataException(Error);
            RequireEntity(action["source_entity_id"]);
            if (action["target_entity_id"] is { } target) RequireEntity(target);
        }
    }

    private static void RequireFields(JsonObject value, IReadOnlyList<string> fields)
    {
        if (value.Count != fields.Count || fields.Any(x => !value.ContainsKey(x))) throw new InvalidDataException(Error);
    }
    private static void RequireString(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null || text.Length > 2000)
            throw new InvalidDataException(Error);
    }
    private static void RequireEntity(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<int>(out var id) || id <= 0) throw new InvalidDataException(Error);
    }
}
