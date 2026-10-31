using System.Diagnostics;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

namespace HsHelper.Coaching;

public sealed record CoachToolObservation(string Name, long ElapsedMilliseconds, bool Success,
    string? ResultJson, string? ResultHash)
{
    public string? Model { get; init; }
    public string? AnalysisRunId { get; init; }
    public string? RequestId { get; init; }
    public string? ToolCallId { get; init; }
    public long? EventSequence { get; init; }
}

/// <summary>All queries and calculations use one immutable frame and one immutable card build.</summary>
public sealed class SnapshotCoach
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly GameSnapshot _snapshot;
    private readonly JsonObject _context;
    private readonly CardCatalog _catalog;
    private readonly string? _build;
    private readonly Action<string, long, bool>? _toolObserved;
    private readonly Action<CoachToolObservation>? _toolCompleted;
    private readonly CoachLibraryContext? _library;
    private readonly AdviceTaskKind? _taskKind;
    private readonly StartingDeckBinding? _startingDeck;
    private readonly LineEvaluator _rules = new();
    private readonly AsyncLocal<AgentToolInvocation?> _toolSource = new();
    private bool IsDeckPlanning => _taskKind == AdviceTaskKind.DeckPlan || _snapshot.Phase == GamePhase.Unknown;

    public SnapshotCoach(GameSnapshot snapshot, JsonObject context, CardCatalog catalog, string? cardBuild,
        Action<string, long, bool>? toolObserved = null, CoachLibraryContext? library = null,
        Action<CoachToolObservation>? toolCompleted = null, AdviceTaskKind? taskKind = null,
        StartingDeckBinding? startingDeck = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(catalog);
        _snapshot = snapshot;
        _context = (JsonObject)context.DeepClone();
        _catalog = catalog;
        _build = cardBuild is null or "" or "unknown" ? null : cardBuild;
        if (_build is not null && snapshot.ClientBuild is { } observed && _build != observed)
            throw new ArgumentException("局面与卡库版本不一致。");
        _toolObserved = toolObserved;
        _library = library;
        _toolCompleted = toolCompleted;
        _taskKind = taskKind;
        var startingReference = StartingDeckBinding.FreezeForSnapshot(snapshot, _build, startingDeck);
        _startingDeck = library is null || startingReference?.DeckVersionId == library.Binding.DeckVersionId ? startingReference : null;
        if (library is not null && (library.Binding.MatchId != snapshot.MatchId ||
            library.Binding.StateVersion != snapshot.StateVersion || library.Binding.CardBuild != cardBuild ||
            (library.Binding.EventSequence is { } sequence && sequence != snapshot.EventSequence)))
            throw new ArgumentException("个人资料查询必须使用同一局面与卡库版本。");
    }

    public AgentCapabilities Capabilities(AnalysisBudget? budget = null, Action<ModelUsage>? onUsage = null) => new()
    {
        Tools = (IsDeckPlanning ? [] : Definitions)
            .Concat(_library is null ? [] : CoachLibraryContext.Definitions).ToArray(),
        Budget = budget,
        OnUsage = onUsage,
        ValidateActions = IsDeckPlanning ? null : ValidateActionsAsync,
        OnToolInvocation = source =>
        {
            if (source.MatchId != _snapshot.MatchId || source.StateVersion != _snapshot.StateVersion ||
                (source.EventSequence.HasValue && source.EventSequence != _snapshot.EventSequence) ||
                (source.KnowledgeAsOf.HasValue && _library is not null && source.KnowledgeAsOf != _library.Binding.AsOf))
                throw new InvalidOperationException("工具来源不属于当前固定局面。");
            _toolSource.Value = source;
        }
    };

    public async Task<JsonNode> ExecuteAsync(string name, JsonObject arguments, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var source = _toolSource.Value;
        var success = false;
        string? resultJson = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await ExecuteCoreAsync(name, arguments, token);
            resultJson = result.ToJsonString();
            success = true;
            return result;
        }
        finally
        {
            _toolObserved?.Invoke(name, stopwatch.ElapsedMilliseconds, success);
            _toolCompleted?.Invoke(new(name, stopwatch.ElapsedMilliseconds, success, resultJson,
                resultJson is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resultJson))))
                { Model = source?.Model, AnalysisRunId = source?.AnalysisRunId, RequestId = source?.RequestId,
                    ToolCallId = source?.ToolCallId, EventSequence = source?.EventSequence });
            _toolSource.Value = null;
        }
    }

    private async Task<JsonNode> ExecuteCoreAsync(string name, JsonObject args, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (_library is not null && CoachLibraryContext.Definitions.Any(d => d.Name == name))
            return await _library.ExecuteAsync(name, args, token);
        if (IsDeckPlanning && name is not ("get_snapshot" or "get_cards"))
            throw new ArgumentException("赛前计划不开放实时动作、选择或牌库概率计算工具。");
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
            events = _context["recent_events"]?.DeepClone() ?? CompactAdviceContextBuilder.Build(_snapshot, _build)["recent_events"]?.DeepClone(),
            public_plays = _context["public_plays"]?.DeepClone() ?? CompactAdviceContextBuilder.Build(_snapshot, _build)["public_plays"]?.DeepClone(),
            history_truncated_through = _snapshot.EventHistoryTruncatedThrough
        });
        if (name == "validate_choice") return Node(_rules.ValidateChoice(_snapshot,
            args["selected_entity_ids"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray(), args["choice_id"]!.GetValue<int>(), token));
        if (name == "calculate_draw_odds")
        {
            var card = args["card_id"]!.GetValue<string>();
            var draws = args["draws"]!.GetValue<int>();
            var tracking = _snapshot.DeckTracking;
            var exact = _snapshot.Phase != GamePhase.Choice && _snapshot.Quality.IsComplete && _snapshot.Quality.IsStable &&
                ChoiceVisibility.ConfirmedLocalPlayer(_snapshot).HasValue && tracking is { IsExact: true, UnknownCount: 0, TotalCards: >= 0 } &&
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
            return Node(new { build = _build, scope = "固定版本印刷定义；拼装实体当前文字须查询get_entity_effect，不用这里的基础文字覆盖。",
                cards = cards.Select(c => new { c.Id, c.Name, c.Text, c.Cost, c.Type, c.Entourage }),
                missing = ids.Except(cards.Select(c => c.Id), StringComparer.Ordinal).ToArray() });
        }
        if (name == "get_entity_effect")
        {
            var id = args["entity_id"]!.GetValue<int>();
            var entity = SnapshotCardEffects.VisibleCards(_snapshot, includePublicGraveyard: true).SingleOrDefault(entity => entity.Id == id);
            if (entity is null) return new JsonObject { ["error"] = "未确认该实体的当前可见卡牌身份，不能查询其效果。" };
            var cardMap = (await _catalog.GetByIdsAsync([entity.CardId!], _build, cancellationToken: token)).ToDictionary(card => card.Id);
            if (_build == _snapshot.ClientBuild && _build == "255790")
                foreach (var donor in await _catalog.GetByDbfIdsAsync(AssembledCardText.ReferencedDbfIds(entity.Tags), _build, cancellationToken: token)) cardMap.TryAdd(donor.Id, donor);
            var effect = SnapshotCardEffects.Resolve(_snapshot, _build, entity, cardMap);
            return Node(new { _snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, build = _build,
                entity_id = entity.Id, card_id = entity.CardId, name = cardMap.GetValueOrDefault(entity.CardId!)?.Name,
                effect = CompactAdviceContextBuilder.EffectSummary(effect), can_evaluate_followup_actions = false });
        }
        var rulesCards = await RuleCardsAsync(token);
        if (name == "evaluate_draw")
            return Node(DrawResolutionRules.Evaluate(_snapshot, args["source_entity_id"]!.GetValue<int>(), rulesCards,
                ImbueEvidence.Evaluate(_snapshot).AtLeast(2)));
        if (name == "evaluate_generation")
            return Node(GenerationResolutionRules.Evaluate(_snapshot, args["source_entity_id"]!.GetValue<int>(), rulesCards));
        if (name == "evaluate_wolf_blessing")
            return Node(WolfBlessingResolutionRules.Evaluate(_snapshot, args["source_entity_id"]!.GetValue<int>(), rulesCards));
        if (name == "evaluate_explosive_trap")
        {
            var sourceId = args["source_entity_id"]!.GetValue<int>(); var attackerId = args["attacker_entity_id"]!.GetValue<int>();
            if (ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is not { } local || local is not (1 or 2) || _snapshot.ClientBuild != "255790" ||
                _snapshot.Phase != GamePhase.Main || !_snapshot.Quality.HasGameStart || !_snapshot.Quality.IsComplete ||
                !_snapshot.Quality.IsStable || _snapshot.Quality.OpenBlockCount != 0 || _snapshot.Entities.Any(pair => pair.Key != pair.Value.Id) ||
                !_snapshot.Entities.TryGetValue(sourceId, out var source) || source.Id != sourceId || source.Kind != EntityKind.Card ||
                source.CardId is not ("CORE_EX1_610" or "EX1_610") || source.Zone != "SECRET" || !OwnController(source, local) ||
                !ObservedType(source, "SPELL", "5") || AssembledCardText.RequiresCustomText(source.Tags) ||
                source.Tags.TryGetValue("49", out var zone) && zone is not ("SECRET" or "7") ||
                !_snapshot.Entities.TryGetValue(attackerId, out var attacker) || attacker.Id != attackerId || attacker.Kind != EntityKind.Card ||
                attacker.Zone != "PLAY" || attacker.ControllerId is not (1 or 2) || attacker.ControllerId == local ||
                !OwnController(attacker, attacker.ControllerId.Value) ||
                !(ObservedType(attacker, "MINION", "4") || ObservedType(attacker, "HERO", "3")) ||
                attacker.Tags.TryGetValue("49", out var attackerZone) && attackerZone is not ("PLAY" or "1"))
                return new JsonObject { ["error"] = "需要完整稳定局面中已确认的我方已安装爆炸陷阱与敌方公开场上角色；不查询对方奥秘，也不模拟未来回合。" };
            return Node(ExplosiveTrapRules.Evaluate(_snapshot, sourceId, attackerId, rulesCards));
        }
        if (name == "evaluate_magma_split")
        {
            var sourceId = args["source_entity_id"]!.GetValue<int>(); var targetId = args["target_entity_id"]!.GetValue<int>();
            if (ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is not { } local || local is not (1 or 2) || _snapshot.ClientBuild != "255790" ||
                _snapshot.Phase != GamePhase.Main || _snapshot.ActivePlayerId != local || !_snapshot.Quality.HasGameStart ||
                !_snapshot.Quality.IsComplete || !_snapshot.Quality.IsStable || _snapshot.Quality.OpenBlockCount != 0 ||
                _snapshot.Entities.Any(pair => pair.Key != pair.Value.Id) ||
                !_snapshot.Entities.TryGetValue(sourceId, out var source) || source.Id != sourceId || source.Kind != EntityKind.Card ||
                source.CardId != "FIR_953" || source.Zone != "PLAY" || !OwnController(source, local) || !MagmaMinion(source) ||
                AssembledCardText.RequiresCustomText(source.Tags) ||
                source.Tags.TryGetValue("49", out var sourceZone) && sourceZone is not ("PLAY" or "1") ||
                !_snapshot.Entities.TryGetValue(targetId, out var target) || target.Id != targetId || target.Kind != EntityKind.Card ||
                target.ControllerId is not (1 or 2) || target.ControllerId == local || !OwnController(target, target.ControllerId.Value) ||
                target.Zone != "PLAY" || !MagmaMinion(target) || target.Tags.TryGetValue("49", out var targetZone) && targetZone is not ("PLAY" or "1"))
                return new JsonObject { ["error"] = "需要完整稳定我方回合中的原生熔岩猎犬与敌方场上随从；攻击对象不是可指定的随机命中。" };
            return await Task.Run(() => Node(_rules.EvaluateMagmaSplit(_snapshot, sourceId, targetId, rulesCards, token)), token);
        }
        if (name == "evaluate_play_triggers")
        {
            var sourceId = args["source_entity_id"]!.GetValue<int>();
            if (ChoiceVisibility.ConfirmedLocalPlayer(_snapshot) is not { } local ||
                local is not (1 or 2) || _snapshot.Entities.Any(pair => pair.Key != pair.Value.Id) ||
                _snapshot.Phase != GamePhase.Main || _snapshot.ActivePlayerId != local || !_snapshot.Quality.HasGameStart ||
                !_snapshot.Quality.IsComplete || !_snapshot.Quality.IsStable ||
                _snapshot.Quality.OpenBlockCount != 0 || !_snapshot.Entities.TryGetValue(sourceId, out var source) ||
                source.Id != sourceId || source.Kind != EntityKind.Card || string.IsNullOrWhiteSpace(source.CardId) || source.Zone != "HAND" ||
                !OwnController(source, local) || source.Tags.TryGetValue("49", out var rawZone) && rawZone is not ("HAND" or "3"))
                return new JsonObject { ["error"] = "需要稳定完整局面中已确认的我方手牌实体，不能预览对手、隐藏或残留来源。" };
            var frozenSource = source with { Tags = source.Tags.ToFrozenDictionary(StringComparer.Ordinal) };
            var quests = _snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card &&
                entity.CardId == "TLC_EVENT_400" && entity.Zone == "SECRET" && OwnController(entity, local) &&
                (!entity.Tags.TryGetValue("49", out var zone) || zone is "SECRET" or "7"))
                .OrderBy(entity => entity.Id).Select(entity => QuestProgressRules.Evaluate(_snapshot, entity.Id, rulesCards, frozenSource)).ToArray();
            return Node(new { _snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                SourceEntityId = source.Id, SourceCardId = source.CardId, QuestProgress = quests,
                Discovery = DiscoveryPlayRules.SupportsCardId(source.CardId) ? DiscoveryPlayRules.Evaluate(_snapshot, source.Id, rulesCards) : null,
                Raptor = DreamboundRaptorRules.Evaluate(_snapshot, source.Id, rulesCards), IsLegal = (bool?)null,
                CanEvaluateFollowupActions = false, HasAuthoritativeNextState = false,
                Assumptions = new[] { "发现入口与使用后触发分属不同结算边界：前者假设成功离手，后者假设前置效果已完成。各段预览不能拼接为已发生的局面；实际支付和动作顺序另用evaluate_line核验。" } });
        }
        if (name == "evaluate_choice")
            return await Task.Run(() => Node(_rules.EvaluateChoice(_snapshot,
                args["selected_entity_ids"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray(), rulesCards,
                args["choice_id"]!.GetValue<int>(), token, startingDeck: _startingDeck)), token);
        if (name == "evaluate_line")
        {
            var actions = ParseActions(args["actions"]!.AsArray());
            return await Task.Run(() => Node(Project(_rules.EvaluateLine(_snapshot, actions, rulesCards, token))), token);
        }
        if (name == "find_lethal")
        {
            var depth = args["maximum_depth"]!.GetValue<int>();
            var nodes = args["maximum_nodes"]!.GetValue<int>();
            return await Task.Run(() =>
            {
                var result = _rules.FindLethal(_snapshot, rulesCards, depth, nodes, token);
                return Node(result with { Evaluation = result.Evaluation is { } evaluation ? Project(evaluation) : null });
            }, token);
        }
        throw new ArgumentException("未知工具。");
    }

    private async Task<IReadOnlyDictionary<string, CardRecord>> RuleCardsAsync(CancellationToken token)
    {
        var history = _snapshot.ChoiceHistory;
        var ownChoice = ChoiceVisibility.ConfirmedOwn(_snapshot);
        var relevantHistoryIds = ownChoice is not null && history is { IsComplete: true, TruncatedThroughSequence: 0 } &&
            history.MatchId == _snapshot.MatchId && history.StateVersion == _snapshot.StateVersion && history.EventSequence == _snapshot.EventSequence
            ? history.Entries.Where(entry => entry is not null && entry.Options is not null && entry.IsComplete && entry.PlayerId == ownChoice.PlayerId &&
                entry.SourceEntityId == ownChoice.SourceEntityId && entry.ChosenAtEventSequence > 0 &&
                entry.ChosenAtEventSequence < ownChoice.OfferedAtEventSequence)
                .SelectMany(entry => entry.Options.Where(option => option is not null).Select(option => option.CardId)).OfType<string>()
            : [];
        var ids = _snapshot.Entities.Values.Select(e => e.CardId).OfType<string>()
            .Concat(_snapshot.Choice?.Options.Select(o => o.CardId).OfType<string>() ?? [])
            .Concat(relevantHistoryIds)
            .Concat(_snapshot.TurnHistory is { IsComplete: true } turns && turns.MatchId == _snapshot.MatchId &&
                turns.StateVersion == _snapshot.StateVersion && turns.EventSequence == _snapshot.EventSequence && ownChoice is not null
                ? turns.Turns.Where(turn => turn is not null && turn.PlayerId == _snapshot.LocalPlayerId)
                    .SelectMany(turn => turn.Plays.Where(play => play is not null && play.AtPlay is not null).Select(play => play.AtPlay.CardId)).OfType<string>() : [])
            .Concat(ChoiceVisibility.ConfirmedLocalPlayer(_snapshot).HasValue && _snapshot.DeckTracking is { KnownRemaining: not null } tracking &&
                tracking.MatchId == _snapshot.MatchId && tracking.StateVersion == _snapshot.StateVersion && tracking.CardBuild == _build
                ? tracking.KnownRemaining.Keys : [])
            .Concat(LineEvaluator.RequiredRelatedCardIds).Distinct().ToArray();
        var cards = (await _catalog.GetByIdsAsync(ids, _build!, cancellationToken: token)).ToDictionary(c => c.Id);
        if (ChoiceVisibility.ConfirmedLocalPlayer(_snapshot).HasValue && _startingDeck is not null)
            foreach (var batch in _startingDeck.Definition.Sideboards.SelectMany(board => board.Cards)
                .Select(card => card.DbfId).Distinct().Chunk(500))
                foreach (var card in await _catalog.GetByDbfIdsAsync(batch, _build!, cancellationToken: token))
                    cards.TryAdd(card.Id, card);
        var related = cards.Values.SelectMany(c => c.Entourage).Distinct().Where(id => !cards.ContainsKey(id)).Take(200).ToArray();
        foreach (var card in await _catalog.GetByIdsAsync(related, _build!, cancellationToken: token)) cards.TryAdd(card.Id, card);
        return cards;
    }

    private async Task<ActionValidationResult> ValidateActionsAsync(IReadOnlyList<AdviceAction> actions, CancellationToken token)
    {
        if (IsDeckPlanning)
            return UnsupportedActions("赛前计划仅提供套牌背景策略，不验证或提供实时动作。");
        if (actions.Count > 0 && actions.All(a => a.Kind == "choose"))
        {
            if (actions.Any(a => a.SourceEntityId is null || a.TargetEntityId is not null))
                return new(AdviceValidationStatus.Invalid, false, Risks: ["选择动作需要候选实体，不接受额外目标。"]);
            var selected = actions.Select(a => a.SourceEntityId!.Value).ToArray();
            var choiceResult = _rules.ValidateChoice(_snapshot, selected, _snapshot.Choice?.Id, token);
            return new(Enum.Parse<AdviceValidationStatus>(choiceResult.Verification.ToString()), choiceResult.IsLegal,
                false, choiceResult.Errors.Concat(choiceResult.Assumptions).ToArray())
            { Assumptions = Array.AsReadOnly(choiceResult.Assumptions.ToArray()) };
        }
        if (actions.Any(a => a.Kind is not ("play" or "attack" or "hero_power" or "end_turn")))
            return UnsupportedActions("当前不能混合选择和其他动作进行后续模拟。");
        if (_build is null || await _catalog.GetManifestAsync(_build, cancellationToken: token) is null)
            return UnsupportedActions("缺少对应版本卡库，无法验证动作。");
        var planned = actions.Select(a => new PlannedAction(a.Kind switch
        { "play" => ActionKind.PlayCard, "attack" => ActionKind.Attack, "hero_power" => ActionKind.HeroPower, _ => ActionKind.EndTurn }, a.SourceEntityId ?? 0, a.TargetEntityId)).ToArray();
        var cards = await RuleCardsAsync(token);
        var result = await Task.Run(() => _rules.EvaluateLine(_snapshot, planned, cards, token), token);
        return new(Enum.Parse<AdviceValidationStatus>(result.Verification.ToString()), result.IsLegal == true,
            result.IsLethal, result.Errors.Concat(result.Assumptions).Concat(result.UnsupportedMechanics).Distinct().ToArray())
        {
            Assumptions = Array.AsReadOnly(result.Assumptions.ToArray()),
            UnsupportedMechanics = Array.AsReadOnly(result.UnsupportedMechanics.ToArray())
        };
    }

    private static ActionValidationResult UnsupportedActions(string reason) =>
        new(AdviceValidationStatus.Unsupported, false, Risks: [reason])
        { UnsupportedMechanics = Array.AsReadOnly(new[] { reason }) };

    private LineEvaluation Project(LineEvaluation evaluation)
    {
        var local = ChoiceVisibility.ConfirmedLocalPlayer(_snapshot);
        bool Visible(SimulatedEntity entity) => entity.Type is "GAME" or "PLAYER" || entity.Zone == "PLAY" ||
            entity.Type == "WEAPON" && entity.Zone is "GRAVEYARD" or "REMOVEDFROMGAME" &&
            _snapshot.Entities.TryGetValue(entity.EntityId, out var prior) && prior.CardId is not null && prior.CardId == entity.CardId &&
            (prior.Zone == "PLAY" || local.HasValue && prior.ControllerId == local && prior.Zone == "HAND") ||
            local.HasValue && entity.ControllerId == local &&
            entity.Zone is "HAND" or "SECRET";
        static bool SafeFlag(string key) => !key.Contains("NAME", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("ACCOUNT", StringComparison.OrdinalIgnoreCase) && !key.Contains("BATTLETAG", StringComparison.OrdinalIgnoreCase) &&
            !key.Contains("USER_ID", StringComparison.OrdinalIgnoreCase) && !key.Contains("USERID", StringComparison.OrdinalIgnoreCase);
        var entities = evaluation.Result.Entities.Where(Visible).Select(entity => entity with
        {
            CardId = entity.Type is "GAME" or "PLAYER" ? null : entity.CardId,
            Flags = entity.Flags.Where(pair => SafeFlag(pair.Key) &&
                (entity.Type is not ("GAME" or "PLAYER") && !string.IsNullOrWhiteSpace(entity.CardId) ||
                    pair.Key is not ("CRAFTABLE_FIRST_HALF_DBID" or "CRAFTABLE_SECOND_HALF_DBID" or "4572" or "4573")))
                .ToFrozenDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        }).ToArray();
        return evaluation with { Result = evaluation.Result with { Entities = Array.AsReadOnly(entities),
            NextCardCostsOpponentHealth = local.HasValue ? evaluation.Result.NextCardCostsOpponentHealth : null } };
    }

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static bool OwnController(EntitySnapshot entity, int local) => entity.ControllerId == local &&
        (!entity.Tags.TryGetValue("50", out var raw) || int.TryParse(raw, out var owner) && owner == local);
    private static bool MagmaMinion(EntitySnapshot entity) => (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named is "MINION" or "4") &&
        (!entity.Tags.TryGetValue("202", out var numeric) || numeric is "MINION" or "4");
    private static bool ObservedType(EntitySnapshot entity, string type, string code) =>
        (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named == type || named == code) &&
        (!entity.Tags.TryGetValue("202", out var numeric) || numeric == type || numeric == code);
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
            action["kind"]?.ToString() is "play" or "attack" or "hero_power" or "end_turn" && Integer(action["source_entity_id"], 1, int.MaxValue) &&
            (action["target_entity_id"] is null || Integer(action["target_entity_id"], 1, int.MaxValue)));
    }
    private static IReadOnlyList<PlannedAction> ParseActions(JsonArray actions) => actions.Select(item => new PlannedAction(
        item!["kind"]!.GetValue<string>() switch { "play" => ActionKind.PlayCard, "attack" => ActionKind.Attack, "hero_power" => ActionKind.HeroPower, _ => ActionKind.EndTurn },
        item["source_entity_id"]!.GetValue<int>(), item["target_entity_id"]?.GetValue<int>())).ToArray();

    public static IReadOnlyList<AgentToolDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new AgentToolDefinition("get_entity_effect", "查询当前可见实体的基础或已观察拼装文字；按固定版本DBF重建半段。未确认半段/供体不回退成原牌效。候选半段不是成品，不改变费用属性，也不证明可执行动作。", Schema("""{"type":"object","properties":{"entity_id":{"type":"integer","minimum":1}},"required":["entity_id"],"additionalProperties":false}"""),
            args => args.Count == 1 && Integer(args["entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_draw", "预览已覆盖手牌成功使用后直接抽牌、满手损失、牌库减量与疲劳范围。假设本体成功离手；合法出牌另用evaluate_line核验。数量范围不生成新手牌实体或后续动作。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 1 && Integer(args["source_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_generation", "预览已覆盖手牌成功使用后的原生随机生成数量与满手损失；不选择随机牌身份，不移出牌库，也不创建可执行的新手牌实体。合法出牌另用evaluate_line核验。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 1 && Integer(args["source_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_wolf_blessing", "比较巨狼祝福随机命中各野兽手牌的直接攻击/减费分支；不指定实际命中，不替换当前局面，不证明技能使用合法或后续斩杀。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 1 && Integer(args["source_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_explosive_trap", "条件预览当前我方已安装爆炸陷阱在敌方公开角色攻击我方英雄时的直接触发伤害。只接收陷阱来源与来袭者，攻击目标固定我方英雄；不能指定伤害、受伤目标或未来回合。不执行对手动作、不探查对方奥秘，不把直接伤害后的存活当作完整攻击结算，也不提供权威新局面、后续动作或斩杀证明。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1},"attacker_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id","attacker_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["source_entity_id"], 1, int.MaxValue) && Integer(args["attacker_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_magma_split", "以当前我方原生熔岩猎犬及其攻击的敌方随从，预览基础攻击与后置逐包随机分配。target_entity_id仅为攻击对象，不是随机命中；不接受指定伤害、分配顺序、分支或概率。区间相关，截断分支不代表全部可能结果；整体是条件预览，不修改局面、不证明后续动作或斩杀。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1},"target_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id","target_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["source_entity_id"], 1, int.MaxValue) && Integer(args["target_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("evaluate_play_triggers", "核对当前我方手牌的原生发现、拼装、回溯入口，并预览主机自动绑定的围攻城门进度与梦缚迅猛龙直接触发次数。发现入口以成功离手为条件，使用后触发以前置效果完成为条件；各段不是已经发生的完整结算。不接受指定进度、触发来源、候选、目标或随机效果。达到发现、任务奖励或随机边界后等待真实选择和新局面；不创建手牌实体、成品或最终费用，也不证明斩杀，合法出牌另用evaluate_line核验。", Schema("""{"type":"object","properties":{"source_entity_id":{"type":"integer","minimum":1}},"required":["source_entity_id"],"additionalProperties":false}"""),
            args => args.Count == 1 && Integer(args["source_entity_id"], 1, int.MaxValue)),
        new AgentToolDefinition("get_recent_events", "查询固定决策点及以前的匿名事件摘要。", Schema("""{"type":"object","properties":{},"required":[],"additionalProperties":false}"""), Empty),
        new AgentToolDefinition("validate_choice", "只验证当前我方选择的候选范围和数量，不模拟选择后效果，也不能证明斩杀。", Schema("""{"type":"object","properties":{"choice_id":{"type":"integer","minimum":1},"selected_entity_ids":{"type":"array","maxItems":30,"items":{"type":"integer","minimum":1}}},"required":["choice_id","selected_entity_ids"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["choice_id"], 1, int.MaxValue) && args["selected_entity_ids"] is JsonArray { Count: <= 30 } ids && ids.All(id => Integer(id, 1, int.MaxValue))),
        new AgentToolDefinition("evaluate_choice", "核算已确认来源的当前选择直接后果，包括入手、授予减费、直接牌库变化、拼装与回溯阶段。组件和按钮不当作入手卡；不生成随机候选、不恢复未观察的旧局面、不提供后续可执行步骤。", Schema("""{"type":"object","properties":{"choice_id":{"type":"integer","minimum":1},"selected_entity_ids":{"type":"array","maxItems":30,"items":{"type":"integer","minimum":1}}},"required":["choice_id","selected_entity_ids"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["choice_id"], 1, int.MaxValue) && args["selected_entity_ids"] is JsonArray { Count: <= 30 } ids && ids.All(id => Integer(id, 1, int.MaxValue))),
        new AgentToolDefinition("calculate_draw_odds", "仅在牌库组成有完整证据时，计算无放回抽牌概率。未知时返回限制。", Schema("""{"type":"object","properties":{"card_id":{"type":"string"},"draws":{"type":"integer","minimum":1,"maximum":30}},"required":["card_id","draws"],"additionalProperties":false}"""),
            args => args.Count == 2 && args["card_id"] is JsonValue id && id.TryGetValue<string>(out var card) && !string.IsNullOrWhiteSpace(card) && card.Length <= 100 && Integer(args["draws"], 1, 30)),
        new AgentToolDefinition("evaluate_line", "验证候选出牌和攻击顺序；仅Verified代表已覆盖全部相关规则，Conditional和Unsupported不能断言结果。", Schema("""{"type":"object","properties":{"actions":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","properties":{"kind":{"type":"string","enum":["play","attack","hero_power","end_turn"]},"source_entity_id":{"type":"integer","minimum":1},"target_entity_id":{"type":"integer","minimum":1}},"required":["kind","source_entity_id"],"additionalProperties":false}}},"required":["actions"],"additionalProperties":false}"""), ValidActions),
        new AgentToolDefinition("find_lethal", "在有限深度和节点预算内搜索斩杀。未发现不代表没有斩杀；只有VerifiedLethalFound可作确定依据。", Schema("""{"type":"object","properties":{"maximum_depth":{"type":"integer","minimum":1,"maximum":6},"maximum_nodes":{"type":"integer","minimum":1,"maximum":5000}},"required":["maximum_depth","maximum_nodes"],"additionalProperties":false}"""),
            args => args.Count == 2 && Integer(args["maximum_depth"], 1, 6) && Integer(args["maximum_nodes"], 1, 5000))
    });
}
