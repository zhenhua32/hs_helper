using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

namespace HsHelper.Coaching;

/// <summary>All queries and calculations use one immutable frame and one immutable card build.</summary>
public sealed class SnapshotCoach
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly GameSnapshot _snapshot;
    private readonly JsonObject _context;
    private readonly CardCatalog _catalog;
    private readonly string? _build;
    private readonly Action<string, long, bool>? _toolObserved;
    private readonly LineEvaluator _rules = new();

    public SnapshotCoach(GameSnapshot snapshot, JsonObject context, CardCatalog catalog, string? cardBuild,
        Action<string, long, bool>? toolObserved = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(catalog);
        _snapshot = snapshot;
        _context = (JsonObject)context.DeepClone();
        _catalog = catalog;
        _build = cardBuild is null or "" or "unknown" ? null : cardBuild;
        if (_build is not null && snapshot.ClientBuild is { } observed && _build != observed)
            throw new ArgumentException("局面与卡库版本不一致。");
        _toolObserved = toolObserved;
    }

    public AgentCapabilities Capabilities(AnalysisBudget? budget = null, Action<ModelUsage>? onUsage = null) => new()
    {
        Tools = Definitions,
        Budget = budget,
        OnUsage = onUsage,
        ValidateActions = ValidateActionsAsync
    };

    public async Task<JsonNode> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await ExecuteCoreAsync(name, arguments, token);
            success = true;
            return result;
        }
        finally { _toolObserved?.Invoke(name, stopwatch.ElapsedMilliseconds, success); }
    }

    private async Task<JsonNode> ExecuteCoreAsync(string name, JsonObject args, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(args);
        var valid = name switch
        {
            "get_snapshot" => Empty(args),
            "get_cards" => ValidCardIds(args),
            _ => Definitions.FirstOrDefault(d => d.Name == name)?.ValidateArguments(args) == true
        };
        if (!valid) throw new ArgumentException("未知工具或无效参数。");
        if (name == "get_snapshot") return _context.DeepClone();
        if (name == "get_recent_events") return Node(new
        {
            match_id = _snapshot.MatchId, state_version = _snapshot.StateVersion,
            events = _snapshot.RecentEvents.Where(e => e.Sequence <= _snapshot.EventSequence).ToArray(),
            public_plays = _snapshot.PublicPlays, history_truncated_through = _snapshot.EventHistoryTruncatedThrough
        });
        if (name == "calculate_draw_odds")
        {
            var card = args["card_id"]!.GetValue<string>();
            var draws = args["draws"]!.GetValue<int>();
            var tracking = _snapshot.DeckTracking;
            var exact = _snapshot.Phase != GamePhase.Choice && _snapshot.Quality.IsComplete && _snapshot.Quality.IsStable &&
                _snapshot.Quality.HasLocalPerspective && tracking is { IsExact: true, UnknownCount: 0, TotalCards: >= 0 } &&
                tracking.MatchId == _snapshot.MatchId && tracking.StateVersion == _snapshot.StateVersion && tracking.CardBuild == _build &&
                tracking.KnownRemaining.Values.All(copies => copies >= 0) && tracking.KnownRemaining.Values.Sum(copies => (long)copies) == tracking.TotalCards;
            var population = exact ? tracking!.TotalCards : null;
            var copies = exact ? tracking!.KnownRemaining.GetValueOrDefault(card) : (int?)null;
            var canCompute = exact && population > 0 && draws <= population;
            return Node(new { _snapshot.MatchId, _snapshot.StateVersion, card_id = card, draws,
                is_exact = canCompute, population, target_copies = copies,
                probability = canCompute ? DrawProbability.AtLeastOne(population!.Value, copies!.Value, draws) : (double?)null,
                reason = canCompute ? "从当前牌库无放回抽取，不包含发现、定向抽牌或后续洗牌。" : "牌库组成或抽取条件不足，概率保持未知。" });
        }
        if (_build is null || await _catalog.GetManifestAsync(_build, cancellationToken: token) is null)
            return new JsonObject { ["error"] = "没有与局面对应的卡库版本，无法验证牌效。" };
        if (name == "get_cards")
        {
            var ids = args["card_ids"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            var cards = await _catalog.GetByIdsAsync(ids, _build, cancellationToken: token);
            return Node(new { build = _build, cards = cards.Select(c => new { c.Id, c.Name, c.Text, c.Cost, c.Type, c.Entourage }),
                missing = ids.Except(cards.Select(c => c.Id), StringComparer.Ordinal).ToArray() });
        }
        var rulesCards = await RuleCardsAsync(token);
        if (name == "evaluate_line")
        {
            var actions = ParseActions(args["actions"]!.AsArray());
            return await Task.Run(() => Node(_rules.EvaluateLine(_snapshot, actions, rulesCards, token)), token);
        }
        if (name == "find_lethal")
        {
            var depth = args["maximum_depth"]!.GetValue<int>();
            var nodes = args["maximum_nodes"]!.GetValue<int>();
            return await Task.Run(() => Node(_rules.FindLethal(_snapshot, rulesCards, depth, nodes, token)), token);
        }
        throw new ArgumentException("未知工具。");
    }

    private async Task<IReadOnlyDictionary<string, CardRecord>> RuleCardsAsync(CancellationToken token)
    {
        var ids = _snapshot.Entities.Values.Select(e => e.CardId).OfType<string>().Distinct().ToArray();
        return (await _catalog.GetByIdsAsync(ids, _build!, cancellationToken: token)).ToDictionary(c => c.Id);
    }

    private async Task<ActionValidationResult> ValidateActionsAsync(IReadOnlyList<AdviceAction> actions, CancellationToken token)
    {
        if (actions.Any(a => a.Kind is not ("play" or "attack" or "hero_power")))
            return new(AdviceValidationStatus.Unsupported, false, Risks: ["选择与结束回合暂未提供动作模拟，只显示策略说明。"]);
        if (_build is null || await _catalog.GetManifestAsync(_build, cancellationToken: token) is null)
            return new(AdviceValidationStatus.Unsupported, false, Risks: ["缺少对应版本卡库，无法验证动作。"]);
        var planned = actions.Select(a => new PlannedAction(a.Kind switch
        { "play" => ActionKind.PlayCard, "attack" => ActionKind.Attack, _ => ActionKind.HeroPower }, a.SourceEntityId ?? 0, a.TargetEntityId)).ToArray();
        var cards = await RuleCardsAsync(token);
        var result = await Task.Run(() => _rules.EvaluateLine(_snapshot, planned, cards, token), token);
        return new(Enum.Parse<AdviceValidationStatus>(result.Verification.ToString()), result.IsLegal == true,
            result.IsLethal, result.Errors.Concat(result.Assumptions).Concat(result.UnsupportedMechanics).Distinct().ToArray());
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();
    private static bool Empty(JsonObject args) => args.Count == 0;
    private static bool ValidCardIds(JsonObject args) => args.Count == 1 && args["card_ids"] is JsonArray { Count: <= 30 } ids &&
        ids.All(node => node is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) && id.Length <= 200);
    private static bool Integer(JsonNode? node, int min, int max) => node is JsonValue value && value.TryGetValue<int>(out var n) && n >= min && n <= max;
    private static bool ValidActions(JsonObject args)
    {
        if (args.Count != 1 || args["actions"] is not JsonArray { Count: > 0 and <= 16 } actions) return false;
        return actions.All(item => item is JsonObject action && action.Count is 2 or 3 &&
            action.All(p => p.Key is "kind" or "source_entity_id" or "target_entity_id") &&
            action["kind"]?.ToString() is "play" or "attack" or "hero_power" && Integer(action["source_entity_id"], 1, int.MaxValue) &&
            (action["target_entity_id"] is null || Integer(action["target_entity_id"], 1, int.MaxValue)));
    }
    private static IReadOnlyList<PlannedAction> ParseActions(JsonArray actions) => actions.Select(item => new PlannedAction(
        item!["kind"]!.GetValue<string>() switch { "play" => ActionKind.PlayCard, "attack" => ActionKind.Attack, _ => ActionKind.HeroPower },
        item["source_entity_id"]!.GetValue<int>(), item["target_entity_id"]?.GetValue<int>())).ToArray();

    public static IReadOnlyList<AgentToolDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new AgentToolDefinition("get_recent_events", "查询固定决策点及以前的匿名事件摘要。", Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""), Empty),
        new AgentToolDefinition("calculate_draw_odds", "仅在牌库组成有完整证据时，计算无放回抽牌概率。未知时返回限制。", Schema("""{"type":"object","properties":{"card_id":{"type":"string"},"draws":{"type":"integer","minimum":1,"maximum":30}},"required":["card_id","draws"],"additionalProperties":false}"""),
            args => args.Count == 2 && args["card_id"] is JsonValue id && id.TryGetValue<string>(out var card) && !string.IsNullOrWhiteSpace(card) && card.Length <= 100 && Integer(args["draws"], 1, 30)),
        new AgentToolDefinition("evaluate_line", "验证候选出牌和攻击顺序；仅Verified代表已覆盖全部相关规则，Conditional和Unsupported不能断言结果。", Schema("""{"type":"object","properties":{"actions":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","properties":{"kind":{"type":"string","enum":["play","attack","hero_power"]},"source_entity_id":{"type":"integer","minimum":1},"target_entity_id":{"type":"integer","minimum":1}},"required":["kind","source_entity_id"],"additionalProperties":false}}},"required":["actions"],"additionalProperties":false}"""), ValidActions),
        new AgentToolDefinition("find_lethal", "在有限深度和节点预算内搜索斩杀。未发现不代表没有斩杀；只有VerifiedLethalFound可作确定依据。", Schema("""{"type":"object","properties":{"maximum_depth":{"type":"integer","minimum":1,"maximum":6},"maximum_nodes":{"type":"integer","minimum":1,"maximum":5000}},"required":["maximum_depth","maximum_nodes"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["maximum_depth"], 1, 6) && Integer(args["maximum_nodes"], 1, 5000))
    });
}
