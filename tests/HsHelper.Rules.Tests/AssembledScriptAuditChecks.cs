using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Rules;

internal static class AssembledScriptAuditChecks
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Fixed = new(ReadCards);

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("assembled script audit pins every observed base and exact trigger effect role", CompleteFamily);
        yield return ("assembled script audit rejects changed whole definitions and record disagreement in every role", DefinitionTrust);
        yield return ("assembled script audit never promotes native text offers missing halves or unsupported compositions", Boundaries);
    }

    private static void CompleteFamily()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var bases = Ids(root, "observedMinionBaseIds"); var triggers = Ids(root, "pureBattlecryDonorIds");
        var effects = Ids(root, "handMinionBuffEffectDonorIds");
        Check(Fixed.Value.Count == 306 && bases.Length == 301 && triggers.Length == 62 && effects.Length == 5 &&
            root.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            root.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8",
            "Complete source definitions or roles changed without a new audit.");
        foreach (var id in bases) AssertRecipe(id, "EDR_102", "TIME_100", false, false);
        foreach (var trigger in triggers)
        foreach (var effect in effects) AssertRecipe("EDR_102", trigger, effect, false, false);
        foreach (var id in bases.Intersect(triggers)) AssertRecipe(id, id, "TIME_100", true, false);
        foreach (var id in effects) AssertRecipe(id, "EDR_102", id, false, true);
        Check(bases.Intersect(triggers).Count() == 57 && triggers.Count(id => Fixed.Value[id].Type == "WEAPON") == 3 &&
            triggers.Count(id => Fixed.Value[id].Type == "HERO") == 2, "Donor type or explicit zero role was silently narrowed.");

        var original = Fixed.Value["TIME_100"];
        var reordered = new JsonObject();
        foreach (var pair in JsonNode.Parse(original.RawJson)!.AsObject().Reverse()) reordered.Add(pair.Key, pair.Value?.DeepClone());
        var cards = Fixed.Value.ToDictionary(); cards[original.Id] = original with { RawJson = reordered.ToJsonString() };
        Check(Resolve("EDR_102", Tags("0", original.DbfId!.Value.ToString()), cards) is not null,
            "Harmless property ordering was mistaken for new script metadata.");
    }

    private static void AssertRecipe(string body, string trigger, string effect, bool retainTrigger, bool retainEffect)
    {
        var first = retainTrigger ? 0 : Fixed.Value[trigger].DbfId!.Value;
        var second = retainEffect ? 0 : Fixed.Value[effect].DbfId!.Value;
        var result = Resolve(body, Tags(first.ToString(), second.ToString()));
        Check(result is not null && result.BaseCardId == body && result.TriggerCardId == trigger && result.EffectCardId == effect &&
            result.FirstHalfDbfId == first && result.SecondHalfDbfId == second &&
            result.ScriptKey == AssembledScriptAudit.HandBuffScriptKey && result.EffectiveText.StartsWith("<b>战吼：</b>", StringComparison.Ordinal) &&
            result.BaseDefinitionFingerprint.Length == 64 && result.TriggerDefinitionFingerprint.Length == 64 && result.EffectDefinitionFingerprint.Length == 64,
            "An exact observed semantic recipe was rejected or changed roles: " + body + "/" + trigger + "/" + effect);
    }

    private static void DefinitionTrust()
    {
        using var fixture = Fixture();
        var bases = Ids(fixture.RootElement, "observedMinionBaseIds").ToHashSet();
        foreach (var original in Fixed.Value.Values)
        {
            // Exercise each complete source as a body when possible and every non-minion as a trigger.
            var body = bases.Contains(original.Id) ? original.Id : "EDR_102";
            var first = bases.Contains(original.Id) ? "113822" : original.DbfId!.Value.ToString();
            var tags = Tags(first, "121051");
            Check(Resolve(body, tags) is not null, "Mutation control was not a supported recipe: " + original.Id);
            var changed = JsonNode.Parse(original.RawJson)!.AsObject(); changed["unreviewedScriptField"] = true;
            foreach (var bad in new[] { original with { RawJson = changed.ToJsonString() }, original with { Name = "different" },
                original with { Text = original.Text + "different" }, original with { Cost = 99 }, original with { DbfId = 1 },
                original with { Entourage = ["unknown"] }, original with { RawJson = "{}" }, original with { RawJson = "broken" } })
            {
                var cards = Fixed.Value.ToDictionary(); cards[original.Id] = bad;
                Check(Resolve(body, tags, cards) is null, "Changed whole source definition inherited a script: " + original.Id);
            }
        }
        foreach (var id in Ids(fixture.RootElement, "pureBattlecryDonorIds").Concat(Ids(fixture.RootElement, "handMinionBuffEffectDonorIds")).Distinct())
        {
            var original = Fixed.Value[id]; var raw = JsonNode.Parse(original.RawJson)!.AsObject(); raw["text"] = "<b>战吼：</b>&让你的手牌中所有随从获得+1/+1。";
            raw["attack"] = 999;
            var cards = Fixed.Value.ToDictionary(); cards[id] = ReadCard(JsonSerializer.SerializeToElement(raw));
            var trigger = Ids(fixture.RootElement, "pureBattlecryDonorIds").Contains(id);
            var tags = trigger ? Tags(original.DbfId!.Value.ToString(), "121051") : Tags("113822", original.DbfId!.Value.ToString());
            Check(Resolve("EDR_102", tags, cards) is null, "Coherent donor mutation borrowed semantic text: " + id);
        }
    }

    private static void Boundaries()
    {
        var valid = Tags("0", "121051");
        foreach (var build in new string?[] { null, "255791", "latest" })
            Check(AssembledScriptAudit.Resolve(build, "EDR_102", valid, Fixed.Value) is null, "Unbound build borrowed the audit.");
        foreach (var tags in new[] { Tags("0", "0"), Tags("1", "121051"), Tags("0", "1"), Tags("-1", "121051"),
            Tags("invalid", "121051"), Tags("2147483648", "121051"), Tags("0", "9999999"),
            new Dictionary<string, string> { [Second] = "121051" }, new Dictionary<string, string> { [First] = "113822" },
            new Dictionary<string, string>(valid) { ["4572"] = "1" }, new Dictionary<string, string>(valid) { ["4573"] = "66858" } })
            Check(Resolve("EDR_102", tags) is null, "Native/offered/malformed/unbound recipe became executable.");
        Check(Resolve("TIME_100", Tags("0", "121051")) is null && Resolve("EDR_102", Tags("113822", "0")) is null,
            "An explicit zero retained a different trigger/effect family.");
        Check(Resolve("EDR_102", Tags("129169", "121051")) is null,
            "A non-Battlecry first half was interpreted by finding a keyword in full text.");
        var duplicate = Fixed.Value.ToDictionary(); duplicate["UNREGISTERED_DUPLICATE"] = Fixed.Value["TIME_100"] with { Id = "UNREGISTERED_DUPLICATE" };
        Check(Resolve("EDR_102", valid, duplicate) is null, "Ambiguous donor DBF used a convenient first record.");
        var missing = Fixed.Value.Where(pair => pair.Key != "TIME_100").ToDictionary();
        Check(Resolve("EDR_102", valid, missing) is null, "Missing effect donor restored base Discover.");
        Check(Resolve("UNREGISTERED", Tags("113822", "121051")) is null, "Unreviewed base gained trust from known donors.");
    }

    private static Dictionary<string, string> Tags(string first, string second) => new() { [First] = first, [Second] = second };
    private static AssembledScriptDefinition? Resolve(string body, IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, CardRecord>? cards = null) => AssembledScriptAudit.Resolve("255790", body, tags, cards ?? Fixed.Value);
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assembled-scripts-255790.json")));
    private static string[] Ids(JsonElement root, string key) => root.GetProperty(key).EnumerateArray().Select(x => x.GetString()!).ToArray();
    private static IReadOnlyDictionary<string, CardRecord> ReadCards()
    { using var fixture = Fixture(); return fixture.RootElement.GetProperty("cards").EnumerateArray().Select(ReadCard).ToDictionary(c => c.Id); }
    private static CardRecord ReadCard(JsonElement raw)
    {
        string Text(string key) => raw.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        string[] List(string key) => raw.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        return new(Text("id"), raw.GetProperty("dbfId").GetInt32(), Text("name"), Text("text"), Text("cardClass"), Text("set"), Text("type"),
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collect) && collect.GetBoolean(), List("classes"), List("entourage"), raw.GetRawText());
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
