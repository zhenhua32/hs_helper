using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

/// <summary>
/// Deterministic, deliberately bounded rules. Card data must be pinned to the supplied snapshot's build.
/// Unknown effects lower verification; they are never silently accepted as a complete simulation.
/// </summary>
public sealed partial class LineEvaluator
{
    // Card text/stats audited against https://api.hearthstonejson.com/v1/255790/zhCN/cards.json.
    // State tag names: https://github.com/HearthSim/python-hearthstone/blob/master/hearthstone/enums.py.
    public const string AuditedBuild = "255790";
    public static IReadOnlyList<string> RequiredRelatedCardIds { get; } = Array.AsReadOnly(new[]
        { "EDR_850p", "EDR_850pe1", "EDR_850pe5", "DINO_421e", "EDR_489e1", "EDR_489e2" });
    public static IReadOnlyList<string> CoverageSummary { get; } = Array.AsReadOnly(new[]
    {
        "基础攻击、嘲讽、圣盾、免疫、冻结、风怒、冲锋、突袭、剧毒、吸血、护甲和观察到的当前费用",
        "build255790：基础稳固射击、硬币、奥术射击、杀戮命令、猎人印记、精灵弓箭手、安装爆炸陷阱、戈德林伤害翻倍",
        "build255790：苦花骑士、振翅守卫与幽爪熊灌注；单一野兽的巨狼祝福及多候选互斥分支；无法确认当前参数时保留未知",
        "build255790：宝石鹦鹉原生生成的直接数量与满手损失；生成及随机命中后等待新局面，不推测牌身份或选择结果",
        "build255790：围攻城门安装及已观察进度延续，奖励选择前停止；梦缚迅猛龙入场与来源/目标绑定的使用后触发次数，随机额外效果待观察",
        "build255790：熔岩猎犬入场、突袭及唯一敌方英雄的确定分配伤害；多目标列有限逐包分支与范围，实际命中前等待新局面",
        "build255790：阿迦玛甘入场与下一张牌的对手生命支付，当前费用上限10；未知剩余效力不由本体推断",
        "build255790：神秘恐魔入场及己方回合结束；唯一敌方随从展开强制战斗，多目标/多触发顺序与未实证计数字段保留条件",
        "未知奥秘、未覆盖光环/触发、亡语与复生会降低为有条件结果；发现与随机选项不自行生成，选择范围可单独核验",
        "斩杀搜索有限深度，未找到结果不代表不存在；只有完整已覆盖序列可标记已验证斩杀"
    });
    private static readonly HashSet<string> ArcaneShot = ["DS1_185", "CORE_DS1_185"];
    private static readonly HashSet<string> KillCommand = ["EX1_539", "CORE_EX1_539"];
    private static readonly HashSet<string> HuntersMark = ["CS2_084"];
    private static readonly HashSet<string> SteadyShot = ["HERO_05bp"];
    private static readonly HashSet<string> ElvenArcher = ["CS2_189", "CORE_CS2_189"];
    private static readonly HashSet<string> ExplosiveTrap = ["EX1_610", "CORE_EX1_610"];
    private static readonly HashSet<string> StaticKeywords = ["TAUNT", "DIVINE_SHIELD", "CHARGE", "RUSH", "WINDFURY", "MEGA_WINDFURY", "LIFESTEAL", "POISONOUS", "STEALTH", "ELUSIVE", "CANT_ATTACK", "REBORN", "IMMUNE", "SPELLPOWER"];
    // Known battlecries from the supplied build-255790 deck do not trigger again while attacking.
    private static readonly HashSet<string> BattlecryOnlyBodies = ["BE_044", "CORE_UNG_912", "EDR_226", "EDR_800", "EDR_852", "EDR_888", "FIR_921", "JAIL_831", "EDR_489", "CORE_EDR_004_2026", "CORE_EDR_004"];

    public LineEvaluation EvaluateLine(GameSnapshot snapshot, IReadOnlyList<PlannedAction> actions,
        IReadOnlyDictionary<string, CardRecord> cards, CancellationToken cancellationToken = default)
        => EvaluateLineCore(snapshot, actions, cards, cancellationToken, previewMagmaOnly: false);

    private LineEvaluation EvaluateLineCore(GameSnapshot snapshot, IReadOnlyList<PlannedAction> actions,
        IReadOnlyDictionary<string, CardRecord> cards, CancellationToken cancellationToken, bool previewMagmaOnly)
        => EvaluateLineRuntime(snapshot, actions, cards, cancellationToken, previewMagmaOnly).Build();

    // Search reads the same current keyword/profile semantics as a manually evaluated line.
    // Keeping this runtime private avoids reconstructing flags from a projected result or printed data.
    private Runtime EvaluateLineRuntime(GameSnapshot snapshot, IReadOnlyList<PlannedAction> actions,
        IReadOnlyDictionary<string, CardRecord> cards, CancellationToken cancellationToken, bool previewMagmaOnly)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(actions); ArgumentNullException.ThrowIfNull(cards);
        cancellationToken.ThrowIfCancellationRequested();
        var state = new Runtime(snapshot, cards, previewMagmaOnly);
        if (actions.Count is 0 or > 16) state.Invalid("动作序列需要包含1至16步。");
        else if (!snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.IsSpectating)
            state.Unsupported("INCOMPLETE_SNAPSHOT", "局面不完整、未稳定、我方视角尚未确认或正在观战。");
        else if (snapshot.Phase != GamePhase.Main) state.Invalid("当前不是可评估主要动作的回合阶段。");
        else if (snapshot.LocalPlayerId is null || snapshot.ActivePlayerId is null)
            state.Unsupported("UNKNOWN_ACTIVE_PLAYER", "行动方未知。");
        else if (snapshot.ActivePlayerId != snapshot.LocalPlayerId) state.Invalid("当前不是我方回合。");
        else if (!state.HasBothHeroes) state.Unsupported("UNKNOWN_HERO", "双方英雄或其生命值尚未可靠识别。");
        else if (!state.ManaKnown) state.Unsupported("UNKNOWN_MANA", "当前法力资源未知。");
        if (!state.Stopped)
        {
            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.DrawBoundaryReached) { state.Unsupported("AWAIT_DRAW_SNAPSHOT", "抽牌直接后果已预览，但新手牌实体、牌库顺序及后续触发尚未观察；请等待新局面后继续动作。"); break; }
                if (state.GenerationBoundaryReached) { state.Unsupported("AWAIT_GENERATION_SNAPSHOT", "随机生成数量已预览，新牌身份、实体与后续触发尚未观察；请等待新局面后继续动作。"); break; }
                if (state.WolfBlessingBoundaryReached) { state.Unsupported("AWAIT_WOLF_BLESSING_SNAPSHOT", "巨狼祝福的实际随机命中尚未观察；请等待新局面后继续动作，不能指定任一分支为真实结果。"); break; }
                if (state.QuestRewardBoundaryReached) { state.Unsupported("AWAIT_QUEST_REWARD_SNAPSHOT", "任务计数或僵尸兽奖励选择尚待确认，不能跨越该边界继续动作。"); break; }
                if (state.RaptorBoundaryReached) { state.Unsupported("AWAIT_RAPTOR_SNAPSHOT", "梦缚迅猛龙的随机额外效果尚未观察，不能假定关键词后继续动作。"); break; }
                if (state.DiscoveryBoundaryReached) { state.Unsupported("AWAIT_DISCOVERY_SNAPSHOT", "原生发现、拼装或回溯选择尚未观察；请在新选择局面中继续，不能提前使用未生成的候选或恢复旧局面。"); break; }
                if (state.MapFollowupBoundaryReached) { state.Unsupported("AWAIT_MAP_FOLLOWUP_SNAPSHOT", "地图首选实体已在本地顺序中使用，追加选择尚未观察；请等待真实剩余选项，不得跨过该边界继续动作。"); break; }
                if (state.MagmaSplitBoundaryReached) { state.Unsupported("AWAIT_MAGMA_SPLIT_SNAPSHOT", "熔岩猎犬的实际分配伤害尚未观察；分支不能作为已发生结果，请等待新局面后继续动作。"); break; }
                if (state.WeaponResolutionBoundaryReached) { state.Unsupported("AWAIT_WEAPON_SNAPSHOT", "武器损毁或替换的后续结算未观察，不能继续使用该投影安排动作。"); break; }
                if (state.StopForExistingQuestReward()) break;
                if (state.TurnEnded) { state.Invalid("己方回合已经结束，不能在未观察下一决策点前继续安排动作。"); break; }
                if (state.EnemyHero!.Health <= 0 || state.OwnHero!.Health <= 0)
                { state.Invalid("英雄已达到零生命，不能继续执行后续动作。"); break; }
                if (!Enum.IsDefined(action.Kind)) { state.Invalid("未知动作类型。"); break; }
                state.InspectInteractions(action);
                if (state.Stopped) break;
                try
                {
                    switch (action.Kind)
                    {
                        case ActionKind.Attack: Attack(state, action); break;
                        case ActionKind.PlayCard:
                            var playFrame = state.CapturePlayTriggers(action);
                            Play(state, action);
                            if (!state.Stopped) state.ResolvePlayTriggers(playFrame);
                            break;
                        case ActionKind.HeroPower: UseHeroPower(state, action); break;
                        case ActionKind.EndTurn: EndTurn(state, action); break;
                    }
                }
                catch (Exception error) when (error is OverflowException or RegexMatchTimeoutException)
                { state.Unsupported("EVALUATION_LIMIT", "数值或牌面信息超出当前计算范围。"); }
                if (state.Stopped) break;
                state.Applied++;
            }
        }
        return state;
    }

    public LethalSearchResult FindLethal(GameSnapshot snapshot, IReadOnlyDictionary<string, CardRecord> cards,
        int maximumDepth = 4, int maximumNodes = 2000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        if (maximumDepth is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (maximumNodes is < 1 or > 20000) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        var paths = new Queue<PlannedAction[]>();
        paths.Enqueue([]);
        var nodes = 0;
        while (paths.TryDequeue(out var path) && nodes < maximumNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Runtime runtime;
            SimulatedState state;
            if (path.Length == 0)
            { runtime = new Runtime(snapshot, cards); state = runtime.Build().Result; }
            else
            {
                runtime = EvaluateLineRuntime(snapshot, path, cards, cancellationToken, previewMagmaOnly: false);
                var evaluation = runtime.Build(); nodes++;
                if (evaluation.IsLethal) return new(true, Array.AsReadOnly(path), evaluation, nodes, false, SearchCoverage);
                if (evaluation.Verification != LineVerification.Verified) continue;
                state = evaluation.Result;
            }
            if (path.Length >= maximumDepth) continue;
            foreach (var action in SearchCandidates(state, snapshot.LocalPlayerId, cards, runtime))
            {
                paths.Enqueue([.. path, action]);
                if (paths.Count >= maximumNodes) break;
            }
        }
        return new(false, [], null, nodes, false, SearchCoverage + " 未找到已验证方案不表示不存在斩杀。");
    }
    private const string SearchCoverage = "有限深度搜索，枚举攻击、稳固射击、硬币衔接已覆盖动作、已支持直伤、猎人印记、简单武器装备及已覆盖随从入场；包括当前可靠冲锋的简单或已支持原生随从、阿迦玛甘生命支付组合与神秘恐魔结束回合。每条路线仍须完整验证，不跨随机、发现、抽牌等观察边界，也不穷尽完整召唤组合。";

    private static IEnumerable<PlannedAction> SearchCandidates(SimulatedState state, int? local,
        IReadOnlyDictionary<string, CardRecord> cards, Runtime runtime)
    {
        if (local is null || state.TurnEnded) yield break;
        var targets = state.Entities.Where(e => e.IsAlive && e.Zone == "PLAY" && e.ControllerId != local && e.Type is "HERO" or "MINION").ToArray();
        foreach (var source in state.Entities.Where(e => e.IsAlive && e.ControllerId == local))
        {
            if (source.Zone == "PLAY" && source.Type is "HERO" or "MINION" && source.Attack > 0)
                foreach (var target in targets) yield return new(ActionKind.Attack, source.EntityId, target.EntityId);
            if (source.Type == "HERO_POWER" && source.Zone == "PLAY" && source.CardId is { } hp && SteadyShot.Contains(hp) && source.Cost <= state.AvailableMana)
                yield return new(ActionKind.HeroPower, source.EntityId);
            if (source.Zone != "HAND" || (state.NextCardCostsOpponentHealth != true && source.Cost > state.AvailableMana) ||
                source.CardId is not { } id || !cards.ContainsKey(id)) continue;
            if (ArcaneShot.Contains(id) || KillCommand.Contains(id) || HuntersMark.Contains(id))
                foreach (var target in targets.Where(e => !HuntersMark.Contains(id) || e.Type == "MINION"))
                    yield return new(ActionKind.PlayCard, source.EntityId, target.EntityId);
            var chargeBody = runtime.SearchableChargeBody(source.EntityId);
            if (chargeBody && ElvenArcher.Contains(id))
            {
                foreach (var target in state.Entities.Where(e => e.IsAlive && e.Zone == "PLAY" && e.Type is "HERO" or "MINION"))
                    yield return new(ActionKind.PlayCard, source.EntityId, target.EntityId);
            }
            else if (id == "GAME_005" || chargeBody || source.Type == "WEAPON" || SearchableRushBodies.Contains(id) || id is "EDR_489" or "CORE_TTN_866" ||
                (state.NextCardCostsOpponentHealth == true && (AuditedHunterBodies.Contains(id) || DiscoveryPlayRules.SupportsCardId(id))))
                yield return new(ActionKind.PlayCard, source.EntityId);
        }
        if (state.Entities.Any(e => e.IsAlive && e.Zone == "PLAY" && e.ControllerId == local && e.CardId == "CORE_TTN_866") &&
            state.Entities.FirstOrDefault(e => e.Type == "PLAYER" && e.Flags.GetValueOrDefault("PLAYER_ID") == local) is { } player)
            yield return new(ActionKind.EndTurn, player.EntityId);
    }

    private static void Attack(Runtime state, PlannedAction action)
    {
        var source = state.Source(action.SourceEntityId, "PLAY"); if (source is null) return;
        var target = state.Target(action.TargetEntityId, requireEnemy: true); if (target is null) return;
        if (source.Type is not ("MINION" or "HERO")) { state.Invalid("该实体不能进行攻击。"); return; }
        Entity? weapon = null;
        if (source.Type == "HERO" && !state.ConfirmAttackWeapon(source, out weapon)) return;
        if (weapon is not null && !state.ConfirmWeaponTarget(target)) return;
        if (!KnownStats(state, source) || !KnownStats(state, target, target.Type == "MINION")) return;
        if (source.Attack <= 0) { state.Invalid("攻击力为零的角色不能攻击。"); return; }
        if (state.Has(source, "EXHAUSTED") || state.Has(source, "FROZEN") || state.Has(source, "CANT_ATTACK") || state.Has(source, "DORMANT") || state.Has(source, "UNTOUCHABLE"))
        { state.Invalid("攻击者已疲劳、冻结、休眠或受到不能攻击限制。"); return; }
        if (state.Has(target, "IMMUNE") || state.Has(target, "STEALTH") || state.Has(target, "DORMANT") || state.Has(target, "UNTOUCHABLE") || state.Has(target, "CANT_BE_ATTACKED"))
        { state.Invalid("目标当前不能被攻击选中。"); return; }
        if ((target.Type == "HERO" && (state.Has(source, "CANT_ATTACK_HEROES") || state.Has(source, "CANNOT_ATTACK_HEROES"))) ||
            (target.Type == "MINION" && state.Has(source, "CANT_ATTACK_MINIONS")))
        { state.Invalid("攻击者受到不能攻击该类型目标的限制。"); return; }
        var maximum = source.Int("MAX_ATTACKS_THIS_TURN") ?? checked((state.CombatHas(source, "MEGA_WINDFURY") ? 4 : state.CombatHas(source, "WINDFURY") ? 2 : 1) + (source.Int("EXTRA_ATTACKS_THIS_TURN") ?? 0));
        if (source.Attacks >= maximum) { state.Invalid("本回合攻击次数已经用尽。"); return; }
        var newMinion = source.Type == "MINION" && source.Int("NUM_TURNS_IN_PLAY") == 0;
        if (source.Type == "MINION" && !source.Flags.ContainsKey("EXHAUSTED") && !source.Flags.ContainsKey("NUM_TURNS_IN_PLAY") &&
            !state.Has(source, "CHARGE") && !state.Has(source, "RUSH"))
        { state.Unsupported("UNKNOWN_ATTACK_READINESS", "缺少疲劳或入场回合信息，无法确认该随从可以攻击。"); return; }
        if (newMinion && !state.Has(source, "CHARGE") && !state.Has(source, "RUSH"))
        { state.Invalid("新入场且没有冲锋或突袭的随从不能攻击。"); return; }
        if (newMinion && state.Has(source, "RUSH") && !state.Has(source, "CHARGE") && target.Type == "HERO")
        { state.Invalid("突袭随从入场回合不能攻击英雄。"); return; }
        if (source.Type == "MINION" && source.Int("NUM_TURNS_IN_PLAY") is null && state.Has(source, "RUSH") && !state.Has(source, "CHARGE") && target.Type == "HERO")
        { state.Unsupported("UNKNOWN_RUSH_READINESS", "突袭随从缺少入场回合信息，不能确认它可以攻击英雄。"); return; }
        var taunts = state.Entities.Values.Where(e => e.Alive && e.Zone == "PLAY" && e.Type == "MINION" && e.Controller != state.Local &&
            state.Has(e, "TAUNT") && !state.Has(e, "IMMUNE") && !state.Has(e, "STEALTH") && !state.Has(e, "DORMANT") && !state.Has(e, "UNTOUCHABLE"));
        if (!state.Has(source, "IGNORE_TAUNT") && taunts.Any() && !taunts.Any(e => e.Id == target.Id)) { state.Invalid("必须先攻击可被选中的嘲讽随从。"); return; }
        var attack = source.Attack!.Value;
        var retaliation = target.Type == "MINION" ? target.Attack!.Value : 0;
        source.Attacks++;
        source.Flags["NUM_ATTACKS_THIS_TURN"] = source.Attacks;
        if (source.Attacks >= maximum) source.Flags["EXHAUSTED"] = 1;
        source.Flags["STEALTH"] = 0;
        var outgoing = state.Damage(source, target, attack, combat: true);
        var incoming = state.CombatHas(source, "IMMUNE_WHILE_ATTACKING")
            ? 0 : state.Damage(target, source, retaliation, combat: true);
        state.HealLifesteal(source, outgoing);
        state.HealLifesteal(target, incoming);
        if (weapon is not null) state.SpendWeaponDurability(source, weapon);
        state.ResolveDeaths();
        state.ResolveMagmaHoundAfterAttack(source, target);
        state.Coverage.UnionWith(["attack-targets", "taunt", "attack-count", "rush-charge", "simultaneous-combat", "armor", "divine-shield", "poisonous", "lifesteal"]);
    }

    private static void UseHeroPower(Runtime state, PlannedAction action)
    {
        var source = state.Source(action.SourceEntityId, "PLAY"); if (source is null) return;
        if (source.Type != "HERO_POWER") { state.Invalid("来源不是英雄技能。"); return; }
        if (state.Has(source, "HERO_POWER_DISABLED") || state.Player?.Int("HERO_POWER_DISABLED") is > 0)
        { state.Invalid("英雄技能当前被禁用。"); return; }
        if (state.Has(source, "EXHAUSTED") || source.Flags.GetValueOrDefault("RULES_USED") != 0)
        { state.Invalid("英雄技能本回合已使用或不可用。"); return; }
        if (source.Int("RULES_HERO_POWER_READINESS_UNKNOWN") is > 0)
        { state.Unsupported("UNKNOWN_REPLACED_POWER_READINESS", "英雄技能替换后的可用状态尚未确认，请等待新的客户端局面。"); return; }
        if (source.CardId == "EDR_850p") { UseWolfBlessing(state, source, action); return; }
        if (source.CardId is null || !SteadyShot.Contains(source.CardId))
        { state.Unsupported("HERO_POWER_EFFECT", "当前只覆盖基础稳固射击，灌注和其他英雄技能需要对应规则。"); return; }
        if (!state.RequireAuditedBuild()) return;
        if (action.TargetEntityId is not null && action.TargetEntityId != state.EnemyHero!.Id)
        { state.Invalid("基础稳固射击只能对敌方英雄造成伤害。"); return; }
        if (!state.Pay(source)) return;
        source.Flags["RULES_USED"] = 1; source.Flags["EXHAUSTED"] = 1;
        if (state.Player!.Int("HEROPOWER_DAMAGE") is > 0 || state.Player.Int("HEROPOWER_CAN_TARGET_MINIONS") is > 0)
            state.Conditional("英雄技能额外伤害或目标改动尚未覆盖，此处仅计算基础效果。", "HERO_POWER_MODIFIER");
        var dealt = state.Damage(source, state.EnemyHero!, 2, combat: false);
        state.HealLifesteal(source, dealt);
        state.ResolveDeaths(); state.Coverage.Add("steady-shot-base-2");
    }

    private static void Play(Runtime state, PlannedAction action)
    {
        var source = state.Source(action.SourceEntityId, "HAND"); if (source is null) return;
        if (state.HasCustomAssembledText(source))
        { state.Unsupported("CUSTOM_ASSEMBLED_SOURCE", "拼装牌的自定义文字尚未模拟，不能按原卡ID结算。"); return; }
        if (state.Has(source, "CANT_PLAY")) { state.Invalid("该手牌当前不能使用。"); return; }
        var profile = state.Profile(source);
        if (profile is null) { state.Unsupported("UNKNOWN_CARD_EFFECT", "当前卡库缺少这张手牌的效果。"); return; }
        if (!state.ObservedCardType(source, profile.Type))
        { state.Unsupported("CONFLICTING_PLAY_CARD_TYPE", "当前卡牌类别为空、非法或与固定身份冲突，未支付或使用。"); return; }
        if (!state.ObservedPlayIdentity(source))
        { state.Unsupported("CONFLICTING_PLAY_IDENTITY", "使用来源的区域、归属或实体标记存在冲突，未支付或使用。"); return; }
        if (!state.CanAffordCard(source)) return;
        var id = source.CardId!;
        // A fixed native identity must be audited before any type-specific handler can
        // reinterpret a corrupted same-ID definition as a minion or weapon.
        if (id == "GAME_005" && (!state.RequireAuditedBuild() || !state.AuditedCoinSource(source))) return;
        if (TryPlayNativeDiscovery(state, action, source, profile)) return;
        if (TryPlayMythicalTerror(state, action, source, profile)) return;
        if (TryPlayBeastHunter(state, action, source, profile)) return;
        if (TryEquipWeapon(state, action, source, profile)) return;
        if ((ArcaneShot.Contains(id) || KillCommand.Contains(id) || HuntersMark.Contains(id) ||
            ExplosiveTrap.Contains(id) || ElvenArcher.Contains(id)) && !state.RequireAuditedBuild()) return;
        if (profile.Type == "MINION")
        {
            if (state.Entities.Values.Count(e => e.Alive && e.Type == "MINION" && e.Zone == "PLAY" && e.Controller == state.Local) >= 7)
            { state.Invalid("我方随从区已满。"); return; }
            if (profile.Mechanics.Contains("BATTLECRY") && !ElvenArcher.Contains(id))
            { state.Unsupported("UNSUPPORTED_BATTLECRY", "该战吼包含发现、随机、灌注或尚未实现的效果，未进行完整结算。"); return; }
            if (!ElvenArcher.Contains(id) && !state.SimpleProfile(profile))
            { state.Unsupported("UNSUPPORTED_MINION_EFFECT", "该随从的入场或持续效果尚未实现。"); return; }
            Entity? target = null;
            if (ElvenArcher.Contains(id))
            { target = state.Target(action.TargetEntityId); if (target is null || !state.CanTarget(target, "BATTLECRY")) return; }
            else if (action.TargetEntityId is not null) { state.Invalid("该随从不需要目标。"); return; }
            if (!state.Summon(source, profile)) return;
            if (state.OpponentDefeatedByPayment) return;
            if (target is not null) state.Damage(source, target, 1, false);
            state.ResolveDeaths(); state.Coverage.Add(ElvenArcher.Contains(id) ? "elven-archer-battlecry-1" : "simple-minion-summon");
            return;
        }
        if (profile.Type != "SPELL") { state.Unsupported("CARD_TYPE", "当前尚未模拟该类型牌的使用。"); return; }
        if (id == "TLC_EVENT_400")
        {
            if (!state.RequireAuditedBuild()) return;
            if (action.TargetEntityId is not null) { state.Invalid("围攻城门不需要指定目标。"); return; }
            state.InstallSidequest(source); return;
        }
        if (id == "GAME_005")
        {
            if (action.TargetEntityId is not null) { state.Invalid("硬币不需要目标。"); return; }
            if (!state.Pay(source)) return;
            if (state.OpponentDefeatedByPayment) return;
            source.Zone = "GRAVEYARD";
            var gain = Math.Max(0, Math.Min(1, state.MaximumMana - state.Mana));
            state.Mana += gain;
            state.Player!.Flags["TEMP_RESOURCES"] = (state.Player.Int("TEMP_RESOURCES") ?? 0) + gain;
            state.Coverage.Add("coin-temporary-mana"); return;
        }
        if (ExplosiveTrap.Contains(id))
        {
            if (action.TargetEntityId is not null) { state.Invalid("爆炸陷阱不需要目标。"); return; }
            if (!state.AuditedExplosiveTrapSource(source)) return;
            if (!state.CanInstallInSecretZone(source)) return;
            if (!state.Pay(source)) return;
            if (state.OpponentDefeatedByPayment) return;
            source.Zone = "SECRET"; state.Coverage.Add("explosive-trap-install-only"); return;
        }
        if (!ArcaneShot.Contains(id) && !KillCommand.Contains(id) && !HuntersMark.Contains(id))
        { state.Unsupported("SPELL_EFFECT", "该法术的发现、任务或其他效果尚未实现。"); return; }
        var targetEntity = state.Target(action.TargetEntityId); if (targetEntity is null || !state.CanTarget(targetEntity, "SPELL")) return;
        if (HuntersMark.Contains(id) && targetEntity.Type != "MINION") { state.Invalid("猎人印记需要随从目标。"); return; }
        if (!state.Pay(source)) return;
        if (state.OpponentDefeatedByPayment) return;
        source.Zone = "GRAVEYARD";
        if (HuntersMark.Contains(id))
        { targetEntity.MaximumHealth = 1; targetEntity.Health = 1; state.Coverage.Add("hunters-mark-set-health-1"); return; }
        var damage = ArcaneShot.Contains(id) ? 2 : state.Entities.Values.Any(e => e.Alive && e.Zone == "PLAY" && e.Type == "MINION" && e.Controller == state.Local && state.IsBeast(e)) ? 5 : 3;
        var dealt = state.Damage(source, targetEntity, damage + state.SpellDamage(), false);
        state.HealLifesteal(source, dealt); state.ResolveDeaths();
        state.Coverage.Add(ArcaneShot.Contains(id) ? "arcane-shot" : "kill-command-beast-condition");
    }

    private static bool KnownStats(Runtime state, Entity entity, bool requireAttack = true)
    {
        if (entity.Health is not null && (!requireAttack || entity.Attack is not null)) return true;
        state.Unsupported("UNKNOWN_COMBAT_STATS", "当前攻击力或生命值未知，不能确认交换结果。"); return false;
    }

    private sealed class Profile(string type, int? attack, int? health, HashSet<string> mechanics, string text, HashSet<string> races, int spellDamage, string? nativeRace)
    {
        public string Type { get; } = type; public int? Attack { get; } = attack; public int? Health { get; } = health;
        public HashSet<string> Mechanics { get; } = mechanics; public string Text { get; } = text;
        public string? NativeRace { get; } = nativeRace;
        public HashSet<string> Races { get; } = races; public int SpellDamage { get; } = spellDamage;
    }
    private sealed class Entity
    {
        public int Id; public string? CardId; public string Type = ""; public int? Controller;
        public string Zone = ""; public int? Attack; public int? MaximumHealth; public int? Health; public int Armor; public int? Cost;
        public int Attacks; public Dictionary<string, int> Flags = []; public bool Alive = true;
        public WeaponDurabilityResolution? WeaponDurability;
        public int? Int(string name) => Flags.TryGetValue(name, out var value) ? value : null;
    }

    private sealed partial class Runtime
    {
        private readonly GameSnapshot _snapshot;
        private readonly IReadOnlyDictionary<string, CardRecord> _cards;
        private readonly Dictionary<string, Profile?> _profiles = new(StringComparer.Ordinal);
        public Dictionary<int, Entity> Entities { get; } = [];
        public HashSet<string> Coverage { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _assumptions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unsupported = new(StringComparer.Ordinal);
        private readonly List<string> _errors = [];
        public int? Local => _snapshot.LocalPlayerId;
        public Entity? Player { get; }
        public Entity? OwnHero { get; }
        public Entity? EnemyHero { get; }
        public int Mana { get; set; }
        public int MaximumMana { get; }
        public bool ManaKnown { get; }
        public bool HasBothHeroes => OwnHero?.Health is not null && EnemyHero?.Health is not null;
        public bool Stopped { get; private set; }
        private bool _invalid;
        private bool _invalidAfterUncertainty;
        private bool _unresolved;
        public int Applied;

        public Runtime(GameSnapshot snapshot, IReadOnlyDictionary<string, CardRecord> cards, bool previewMagmaOnly = false)
        {
            _snapshot = snapshot; _cards = cards; _previewMagmaOnly = previewMagmaOnly;
            foreach (var source in snapshot.Entities.Values)
            {
                var values = source.Tags.Where(t => int.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .ToDictionary(t => t.Key, t => int.Parse(t.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
                var entity = new Entity { Id = source.Id, CardId = source.CardId, Controller = source.ControllerId, Zone = source.Zone ?? "",
                    Attack = source.Attack, MaximumHealth = source.GetIntTag("HEALTH"), Armor = source.GetIntTag("ARMOR") ?? 0,
                    Health = source.GetIntTag("HEALTH") is { } health ? health - (source.GetIntTag("DAMAGE") ?? 0) : null,
                    Cost = source.Cost, Attacks = source.GetIntTag("NUM_ATTACKS_THIS_TURN") ?? 0, Flags = values };
                entity.Type = source.Tags.GetValueOrDefault("CARDTYPE") switch
                { "1" => "GAME", "2" => "PLAYER", "3" => "HERO", "4" => "MINION", "5" => "SPELL", "6" => "ENCHANTMENT", "7" => "WEAPON", "10" => "HERO_POWER", var type => type ?? "" };
                if (source.Kind == EntityKind.Game) entity.Type = "GAME";
                if (source.Kind == EntityKind.Player) entity.Type = "PLAYER";
                if (string.IsNullOrEmpty(entity.Type)) entity.Type = Profile(entity)?.Type ?? (source.Kind == EntityKind.Player ? "PLAYER" : source.Kind == EntityKind.Game ? "GAME" : "");
                if (entity.Type == "WEAPON")
                {
                    entity.WeaponDurability = WeaponDurabilityEvidence.Resolve(snapshot.ClientBuild, source);
                    entity.MaximumHealth = entity.WeaponDurability.MaximumHealth;
                    entity.Health = entity.WeaponDurability.Remaining;
                }
                entity.Alive = entity.Health is null || entity.Health > 0;
                Entities[entity.Id] = entity;
            }
            Player = Entities.Values.FirstOrDefault(e => e.Type == "PLAYER" && e.Int("PLAYER_ID") == Local);
            OwnHero = Hero(Local);
            var enemyCandidates = Entities.Values.Where(e => e.Type == "HERO" && e.Controller is not null && e.Controller != Local && e.Zone == "PLAY").Take(2).ToArray();
            EnemyHero = enemyCandidates.Length == 1 ? enemyCandidates[0] : null;
            if (EnemyHero is null)
            {
                var other = Entities.Values.Where(e => e.Type == "PLAYER" && e.Int("PLAYER_ID") != Local).FirstOrDefault();
                EnemyHero = Hero(other?.Int("PLAYER_ID"));
            }
            ManaKnown = Player?.Int("RESOURCES") is not null;
            Mana = Math.Max(0, (Player?.Int("RESOURCES") ?? 0) - (Player?.Int("RESOURCES_USED") ?? 0) - (Player?.Int("OVERLOAD_LOCKED") ?? 0) + (Player?.Int("TEMP_RESOURCES") ?? 0));
            MaximumMana = Player?.Int("MAXRESOURCES") ?? 10;
            InitializeOpponentHealthPayment();
            InitializeImbueEvidence();
            Coverage.Add("complete-state-default-zero-flags");
        }

        private Entity? Hero(int? playerId)
        {
            if (playerId is null) return null;
            var player = Entities.Values.FirstOrDefault(e => e.Type == "PLAYER" && e.Int("PLAYER_ID") == playerId);
            if (player?.Int("HERO_ENTITY") is { } id && Entities.TryGetValue(id, out var hero)) return hero;
            var candidates = Entities.Values.Where(e => e.Type == "HERO" && e.Controller == playerId && e.Zone == "PLAY").Take(2).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        public Entity? Weapon(int? controller) => Entities.Values.Where(e => e.Alive && e.Type == "WEAPON" && e.Controller == controller && e.Zone == "PLAY").Take(2).ToArray() is [var only] ? only : null;
        public Profile? Profile(Entity entity)
        {
            if (entity.CardId is null) return null;
            if (_profiles.TryGetValue(entity.CardId, out var cached)) return cached;
            if (!_cards.TryGetValue(entity.CardId, out var card)) return _profiles[entity.CardId] = null;
            try
            {
                using var raw = JsonDocument.Parse(card.RawJson);
                int? Number(string key) => raw.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
                var mechanics = raw.RootElement.TryGetProperty("mechanics", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
                var races = new HashSet<string>(StringComparer.Ordinal);
                string? nativeRace = null;
                if (raw.RootElement.TryGetProperty("race", out var race) && race.ValueKind == JsonValueKind.String)
                { nativeRace = race.GetString(); races.Add(nativeRace!); }
                if (raw.RootElement.TryGetProperty("races", out var raceList) && raceList.ValueKind == JsonValueKind.Array)
                    foreach (var item in raceList.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)) races.Add(item.GetString()!);
                return _profiles[entity.CardId] = new(card.Type, Number("attack"), Number("health"), mechanics, card.Text, races, Number("spellDamage") ?? 0, nativeRace);
            }
            catch (JsonException) { return _profiles[entity.CardId] = null; }
        }
        public bool Silenced(Entity entity) => entity.Int("SILENCED") == 1;
        public bool Has(Entity entity, string keyword)
        {
            if (entity.Flags.TryGetValue(keyword, out var value)) return value > 0;
            if (Silenced(entity)) return false;
            return Profile(entity)?.Mechanics.Contains(keyword) == true;
        }
        public bool SearchableChargeBody(int id)
        {
            if (!Entities.TryGetValue(id, out var source) || source.Type != "MINION" ||
                !_snapshot.Entities.TryGetValue(id, out var observed) || observed.Kind != EntityKind.Card || observed.Id != id ||
                observed.CardId != source.CardId || observed.Tags is null ||
                source.Attack is null or < 0 || source.MaximumHealth is null or <= 0 || source.Health is null or <= 0 || source.Cost is null or < 0 ||
                !RawNumber(observed, "ATK", "47", true, out _) || !RawNumber(observed, "HEALTH", "45", true, out _) ||
                !RawNumber(observed, "DAMAGE", "44", true, out var damage) || damage != 0 || !RawNumber(observed, "COST", "48", true, out _) ||
                !RawNumber(observed, "SILENCED", "188", false, out var silenced) || silenced is not (null or 0 or 1) ||
                !RawNumber(observed, "FROZEN", "260", false, out var frozen) || frozen is not (null or 0) ||
                !RawNumber(observed, "DORMANT", "1518", false, out var dormant) || dormant is not (null or 0)) return false;
            // Has deliberately falls back to native mechanics when no current flag was observed.
            // Malformed/contradictory observations must not disappear during integer materialization
            // and accidentally restore that printed keyword. A genuine gift still needs its own audit.
            if (!RawNumber(observed, "CHARGE", "197", false, out var flag) || flag is not (null or 1)) return false;
            try
            {
                if (!Has(source, "CHARGE") || Profile(source) is not { Type: "MINION" } profile) return false;
                return source.CardId is { } cardId && (AuditedHunterBodies.Contains(cardId) || ElvenArcher.Contains(cardId) ||
                    DiscoveryPlayRules.SupportsCardId(cardId) || cardId == "CORE_TTN_866" || SimpleProfile(profile));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or RegexMatchTimeoutException)
            { return false; }
        }
        public bool CombatHas(Entity entity, string keyword) => Has(entity, keyword) ||
            (entity.Type == "HERO" && Weapon(entity.Controller) is { } weapon && WeaponHas(weapon, keyword));
        public bool IsBeast(Entity entity)
        {
            var profile = Profile(entity);
            if (entity.Int("CARDRACE") is not { } race) return profile?.Races.Overlaps(["BEAST", "ALL"]) == true;
            if (race is 20 or 26) return true;
            // Only this pinned dual-race card preserves BEAST alongside its observed native DEMON tag.
            // A different observed primary race never regains BEAST from printed metadata.
            return _snapshot.ClientBuild == AuditedBuild && entity.CardId == "CORE_TTN_866" && race == 15 &&
                profile?.NativeRace == "DEMON" && profile.Races.SetEquals(["DEMON", "BEAST"]);
        }
        public bool SimpleProfile(Profile profile) => profile.Mechanics.All(StaticKeywords.Contains) &&
            string.IsNullOrWhiteSpace(Regex.Replace(profile.Text, "<[^>]+>|\\$|法术伤害\\s*\\+\\d+|Spell Damage\\s*\\+\\d+|[\\s。；，,.!！:：]|嘲讽|圣盾|冲锋|突袭|风怒|吸血|剧毒|潜行|扰魔|复生|Taunt|Divine Shield|Charge|Rush|Windfury|Lifesteal|Poisonous|Stealth|Elusive|Reborn", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));

        public bool RequireAuditedBuild()
        {
            if (_snapshot.ClientBuild == "255790") { Coverage.Add("named-card-handlers-build-255790"); return true; }
            Unsupported("UNAUDITED_CARD_BUILD", "具名牌效只审计了build 255790；其他版本需要重新核对。"); return false;
        }

        public Entity? Source(int id, string zone)
        {
            if (!Entities.TryGetValue(id, out var source) || !source.Alive) { Invalid("动作来源不存在或已经离场。"); return null; }
            if (source.Controller != Local || source.Zone != zone) { Invalid("动作来源不属于我方或不在正确区域。"); return null; }
            return source;
        }
        public Entity? Target(int? id, bool requireEnemy = false)
        {
            if (id is null || !Entities.TryGetValue(id.Value, out var target) || !target.Alive || target.Zone != "PLAY" || target.Type is not ("HERO" or "MINION"))
            { Invalid("目标不存在、已离场或不是有效角色。"); return null; }
            if (requireEnemy && target.Controller == Local) { Invalid("不能攻击友方角色。"); return null; }
            if (target.Controller is null) { Unsupported("UNKNOWN_CONTROLLER", "目标控制者未知。"); return null; }
            return target;
        }
        public bool CanTarget(Entity target, string effect)
        {
            if (Has(target, "DORMANT") || Has(target, "UNTOUCHABLE") || Has(target, "CANT_BE_TARGETED") ||
                (target.Controller != Local && (Has(target, "STEALTH") || Has(target, "IMMUNE") || Has(target, "CANT_BE_TARGETED_BY_OPPONENTS"))))
            { Invalid("目标当前不能被该效果选中。"); return false; }
            if (effect == "SPELL" && (Has(target, "ELUSIVE") || Has(target, "CANT_BE_TARGETED_BY_SPELLS") || Has(target, "CANT_BE_TARGETED_BY_ABILITIES")))
            { Invalid("目标不能被法术选中。"); return false; }
            if (effect == "BATTLECRY" && Has(target, "CANT_BE_TARGETED_BY_BATTLECRIES"))
            { Invalid("目标不能被战吼选中。"); return false; }
            if (target.Health is null) { Unsupported("UNKNOWN_TARGET_HEALTH", "目标生命值未知。"); return false; }
            return true;
        }
        public bool Pay(Entity source)
        {
            if (source.Cost is null) { Unsupported("UNKNOWN_CURRENT_COST", "当前费用未知。"); return false; }
            if (PayWithOpponentHealth(source) is { } alternate) return alternate;
            if (source.Cost < 0 || source.Cost > Mana) { Invalid("当前法力不足或费用无效。"); return false; }
            Mana -= source.Cost.Value;
            Player!.Flags["RESOURCES_USED"] = (Player.Int("RESOURCES_USED") ?? 0) + source.Cost.Value;
            Coverage.Add("current-cost-and-mana"); return true;
        }
        public int SpellDamage() => Player?.Int("CURRENT_SPELLPOWER") ?? Entities.Values
            .Where(e => e.Alive && e.Type == "MINION" && e.Controller == Local && e.Zone == "PLAY")
            .Sum(e => e.Int("SPELLPOWER") ?? (Silenced(e) ? 0 : Profile(e)?.SpellDamage ?? 0));
        public int Damage(Entity source, Entity target, int amount, bool combat)
        {
            if (amount <= 0 || Has(target, "IMMUNE")) return 0;
            if (target.Health is null) { Unsupported("UNKNOWN_DAMAGE_TARGET", "受伤角色生命值未知。"); return 0; }
            amount = ModifyBeastDamage(source, amount);
            if (Has(target, "DIVINE_SHIELD")) { target.Flags["DIVINE_SHIELD"] = 0; return 0; }
            var armor = Math.Min(target.Armor, amount); target.Armor -= armor;
            target.Health -= amount - armor;
            var poisonous = Has(source, "POISONOUS") || (source.Type == "HERO" && Weapon(source.Controller) is { } weapon && WeaponHas(weapon, "POISONOUS"));
            if (poisonous && target.Type == "MINION") target.Health = 0;
            return amount;
        }
        private int ModifyBeastDamage(Entity source, int amount)
        {
            if (IsBeast(source))
            {
                var gods = Entities.Values.Count(e => e.Alive && e.Zone == "PLAY" && e.Controller == source.Controller && e.CardId == "EDR_480" && !Silenced(e) && !Has(e, "DORMANT"));
                if (gods > 0 && _snapshot.ClientBuild != "255790")
                    Conditional("当前build的戈德林牌效尚未审计，仅保留未计算光环的条件结果。", "UNAUDITED_GOLDRINN_BUILD");
                else for (var i = 0; i < gods; i++) amount = checked(amount * 2);
                if (gods > 0) Coverage.Add("goldrinn-friendly-beast-double-damage");
            }
            return amount;
        }
        public void HealLifesteal(Entity source, int dealt)
        {
            var life = Has(source, "LIFESTEAL") || (source.Type == "HERO" && Weapon(source.Controller) is { } weapon && WeaponHas(weapon, "LIFESTEAL"));
            if (!life || dealt <= 0) return;
            var hero = Hero(source.Controller);
            if (hero?.Health is null || hero.MaximumHealth is null) { Conditional("吸血恢复目标的生命上限未知。", "LIFESTEAL_TARGET"); return; }
            if (Has(hero, "CANT_BE_HEALED")) return;
            hero.Health = (int)Math.Min(hero.MaximumHealth.Value, (long)hero.Health.Value + dealt);
        }
        public void ResolveDeaths()
        {
            foreach (var entity in Entities.Values.Where(e => e.Alive && e.Health <= 0).ToArray()) Remove(entity);
        }
        public void Remove(Entity entity)
        {
            var resolvedNative = TryResolveBeastHunterDeath(entity);
            if ((Has(entity, "DEATHRATTLE") && !resolvedNative) || Has(entity, "REBORN"))
                Conditional("本次死亡后的亡语或复生尚未展开；后续结果假设这些效果不改变所列动作。", "DEATHRATTLE_OR_REBORN");
            entity.Alive = false; entity.Zone = "GRAVEYARD";
            if (entity.Type == "WEAPON") _removedWeaponIds.Add(entity.Id);
            if (entity.Type == "WEAPON" && (Has(entity, "DEATHRATTLE") || Has(entity, "REBORN"))) MarkWeaponBoundary();
        }
        public void InspectInteractions(PlannedAction action)
        {
            if (Entities.TryGetValue(action.SourceEntityId, out var selectedSource) && HasCustomAssembledText(selectedSource))
            { Unsupported("CUSTOM_ASSEMBLED_SOURCE", "拼装牌的自定义文字尚未模拟，不能按原卡ID结算。"); return; }
            foreach (var entity in Entities.Values.Where(e => e.Alive && (e.Zone is "PLAY" or "HAND" || e.Type == "PLAYER" ||
                (e.Type == "ENCHANTMENT" && e.Int("ATTACHED") is > 0 && e.Zone != "GRAVEYARD"))))
            {
                if (HasCustomAssembledText(entity))
                {
                    if (entity.Zone == "PLAY")
                    { Unsupported("CUSTOM_ASSEMBLED_ACTIVE", "场上拼装文字可能改变光环或触发，不能按原牌效验证动作。"); return; }
                    Conditional("存在未覆盖的拼装文字，手牌或附加触发可能改变后果。", "CUSTOM_ASSEMBLED_HAND");
                    continue;
                }
                if (entity.Flags.Any(flag => flag.Value != 0 && flag.Key.Length > 0 && flag.Key.All(char.IsDigit) &&
                    !IsKnownImbueCounter(entity, flag.Key) &&
                    !IsObservedStaticDarkGiftNumericTag(entity, flag.Key) &&
                    !((_snapshot.ClientBuild == AuditedBuild && entity.CardId == "EDR_850p" || IsObservedStatEnchantment(entity)) && flag.Key is "2" or "3")))
                    Conditional("存在尚未解释的数字状态标签，不能确认已覆盖全部动作限制。", "UNMAPPED_NUMERIC_TAG");
                if (new[] { "HEALTH_MINIMUM", "MINIMUM_HEALTH", "DAMAGE_CANT_KILL", "INCOMING_DAMAGE_MULTIPLIER", "SPELL_DAMAGE_DOUBLE", "SPELLPOWER_DOUBLE", "SPELL_HEALING_DOUBLE", "HEALING_DOES_DAMAGE", "HEALING_DOUBLE", "MULTIPLY_HERO_DAMAGE", "RECEIVES_DOUBLE_SPELLDAMAGE_BONUS", "LIFESTEAL_DAMAGES_OPPOSING_HERO", "CANT_HEAL", "CANT_DAMAGE", "CANT_BE_DESTROYED", "HERO_CANT_BE_DESTROYED", "ALL_TARGETS_RANDOM", "FORGETFUL", "HEROPOWER_ADDITIONAL_ACTIVATIONS", "HEROPOWER_UNLIMITED_USES", "HEROPOWER_ACTIVATIONS_THIS_TURN", "CURRENT_HEROPOWER_DAMAGE_BONUS" }
                    .Any(tag => entity.Int(tag) is > 0))
                    Conditional("存在尚未覆盖的伤害或治疗修正，结果按未应用该修正的条件展示。", "DAMAGE_OR_HEALING_MODIFIER");
                if ((action.Kind == ActionKind.PlayCard && new[] { "SPELLS_COST_HEALTH", "NEXT_CARD_COSTS_HEALTH", "COSTS_HEALTH", "HEALTH_COST" }.Any(tag => entity.Int(tag) is > 0)) ||
                    (action.Kind == ActionKind.HeroPower && entity.Int("HEROPOWER_COSTS_HEALTH") is > 0))
                { Unsupported("ALTERNATE_RESOURCE_PAYMENT", "当前存在生命等特殊支付方式，不能使用普通法力支付规则。"); return; }
                if (new[] { "EXTRA_MINION_DEATHRATTLES_BASE", "EXTRA_DEATHRATTLES_BASE", "EXTRA_DEATHRATTLES_ADDITIONAL", "EXTRA_BATTLECRIES_BASE", "EXTRA_MINION_BATTLECRIES_BASE", "EXTRA_BATTLECRIES_ADDITIONAL", "CANT_TRIGGER_DEATHRATTLE" }.Any(tag => entity.Int(tag) is > 0))
                    Conditional("存在重复或禁用战吼/亡语的规则修正，此处未完整展开触发次数。", "BATTLECRY_OR_DEATHRATTLE_MODIFIER");
            }
            foreach (var entity in Entities.Values.Where(e => e.Alive))
            {
                if (IsStaticDarkGiftCandidate(entity) && entity.Zone != "GRAVEYARD")
                {
                    if (TryObservedStaticDarkGift(entity, out var darkGiftCoverage)) Coverage.Add(darkGiftCoverage);
                    else Conditional("黑暗之赐附魔的当前挂载、固定定义、归属或目标观察属性未通过审计，未按静态效果放行。", "UNMAPPED_ATTACHED_ENCHANTMENT");
                    continue;
                }
                if (entity.Type == "ENCHANTMENT" && entity.Int("ATTACHED") is > 0 && entity.Zone != "GRAVEYARD")
                {
                    if (IsObservedOpponentHealthEnchantment(entity)) Coverage.Add("EDR_489-observed-player-or-hero-enchantment");
                    else if (IsObservedStatEnchantment(entity)) Coverage.Add("observed-stat-only-enchantment:" + entity.CardId);
                    else Conditional("存在尚未单独审计的附魔，假设它不额外改变伤害、目标、支付或触发顺序。", "UNMAPPED_ATTACHED_ENCHANTMENT");
                    continue;
                }
                if (entity.Zone == "SECRET" && entity.Controller != Local)
                { Conditional("假设对手奥秘不触发或不改变本序列，不能据此声称已验证斩杀。", "OPPONENT_SECRET"); continue; }
                if (entity.Zone == "SECRET" && entity.Controller == Local)
                {
                    if (_snapshot.ClientBuild == "255790" && entity.CardId is { } secret && ExplosiveTrap.Contains(secret) &&
                        ExplosiveTrapRules.IsAuditedSource(_cards.GetValueOrDefault(secret))) continue;
                    if (IsNativeSidequest(entity)) continue;
                    Conditional("假设我方尚未覆盖的奥秘或任务不在本序列触发，奖励与触发效果未展开。", "FRIENDLY_SECRET_OR_QUEST"); continue;
                }
                if (entity.Zone != "PLAY" || entity.Type is "GAME" or "PLAYER" or "HERO_POWER") continue;
                if (entity.Type == "HERO" && entity.CardId?.StartsWith("HERO_", StringComparison.Ordinal) == true) continue;
                if (Silenced(entity) || Has(entity, "DORMANT") || Has(entity, "UNTOUCHABLE")) continue;
                var profile = Profile(entity);
                if (profile is null) { Conditional("场上存在缺少卡牌效果资料的实体，可能影响动作。", "UNKNOWN_BOARD_CARD"); continue; }
                if (entity.Type == "WEAPON" && SupportedWeaponAttackProfile(entity)) { Coverage.Add("weapon-passive-attack-profile"); continue; }
                if (profile.Mechanics.Contains("AURA") && entity.CardId != "EDR_480")
                    Conditional("假设未覆盖的持续光环不改变费用、伤害或目标。", "UNKNOWN_AURA");
                if (_snapshot.ClientBuild == "255790" && entity.CardId == "CORE_TTN_866")
                { Coverage.Add(action.Kind == ActionKind.EndTurn ? "CORE_TTN_866-end-turn-trigger-inspected" : "CORE_TTN_866-end-turn-trigger-not-entered"); continue; }
                if (_snapshot.ClientBuild == "255790" && entity.CardId == "EDR_849") continue;
                if (_snapshot.ClientBuild == "255790" && entity.CardId == "FIR_953") continue;
                if (profile.Mechanics.Contains("TRIGGER_VISUAL")) Conditional("场上触发效果尚未完整模拟，结果带条件。", "UNKNOWN_TRIGGER");
                if (_snapshot.ClientBuild == "255790" && entity.CardId is { } id && (BattlecryOnlyBodies.Contains(id) || ElvenArcher.Contains(id)))
                {
                    continue;
                }
                if (_snapshot.ClientBuild == "255790" && entity.CardId is "EDR_480" or "EDR_227" or "DINO_421") continue;
                if (!SimpleProfile(profile) && !profile.Mechanics.Contains("TRIGGER_VISUAL"))
                    Conditional("假设未覆盖的场上卡牌文字不改变本序列。", "UNMAPPED_BOARD_TEXT");
            }
        }
        public void Invalid(string message)
        {
            _errors.Add(message); _invalid = true; Stopped = true;
            if (Applied > 0 && _assumptions.Count > 0)
            {
                _invalidAfterUncertainty = true;
                _unsupported.Add("CONDITIONAL_STATE_VALIDITY");
                _assumptions.Add("未覆盖的前序效果可能改变后续动作是否合法，不能把条件状态下的失败视为确定非法。");
            }
        }
        // Summon replaces runtime flags; the observed half evidence remains on the original immutable frame.
        public bool HasCustomAssembledText(Entity entity) =>
            _snapshot.Entities.TryGetValue(entity.Id, out var observed) && AssembledCardText.RequiresCustomText(observed.Tags);
        public void Unsupported(string kind, string message) { _unsupported.Add(kind); _assumptions.Add(message); _unresolved = true; Stopped = true; }
        public void Conditional(string assumption, string kind) { _assumptions.Add(assumption); _unsupported.Add(kind); }
        public LineEvaluation Build()
        {
            var verification = _invalidAfterUncertainty || _unresolved ? LineVerification.Unsupported : _invalid ? LineVerification.Invalid : _assumptions.Count > 0 ? LineVerification.Conditional : LineVerification.Verified;
            var potential = EnemyHero?.Health <= 0 && OwnHero?.Health > 0;
            var entities = Entities.Values.OrderBy(e => e.Id).Select(e => new SimulatedEntity(e.Id, e.CardId, e.Type, e.Controller,
                e.Zone, e.Attack, e.MaximumHealth, e.Health, e.Armor, e.Cost, e.Attacks, e.Alive, ResultFlags(e)) { WeaponDurability = e.WeaponDurability }).ToArray();
            return new(_snapshot.MatchId, _snapshot.StateVersion, verification, verification == LineVerification.Invalid ? false : verification == LineVerification.Verified ? true : null,
                verification == LineVerification.Verified && potential, potential, Applied, _errors.AsReadOnly(),
                _assumptions.OrderBy(x => x, StringComparer.Ordinal).ToArray(), _unsupported.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                Coverage.OrderBy(x => x, StringComparer.Ordinal).ToArray(), new(Mana, Array.AsReadOnly(entities))
                { HeroPowerChanges = Array.AsReadOnly(_heroPowerChanges.ToArray()),
                  ImbueCountBefore = _initialImbues, ImbueCountAfter = _currentImbues,
                  DrawBoundaryReached = DrawBoundaryReached, DrawPreviews = Array.AsReadOnly(_drawPreviews.ToArray()),
                  GenerationBoundaryReached = GenerationBoundaryReached, GenerationPreviews = Array.AsReadOnly(_generationPreviews.ToArray()),
                  WolfBlessingBoundaryReached = WolfBlessingBoundaryReached, WolfBlessingPreviews = Array.AsReadOnly(_wolfBlessingPreviews.ToArray()),
                  QuestRewardBoundaryReached = QuestRewardBoundaryReached, QuestProgressPreviews = Array.AsReadOnly(_questProgressPreviews.ToArray()),
                  RaptorBoundaryReached = RaptorBoundaryReached, RaptorPreviews = Array.AsReadOnly(_raptorPreviews.ToArray()),
                  DiscoveryBoundaryReached = DiscoveryBoundaryReached, DiscoveryPreviews = Array.AsReadOnly(_discoveryPreviews.ToArray()),
                  MapFollowupBoundaryReached = MapFollowupBoundaryReached, MapPlayPreviews = Array.AsReadOnly(_mapPlayPreviews.ToArray()),
                  MagmaSplitBoundaryReached = MagmaSplitBoundaryReached, MagmaSplitPreviews = Array.AsReadOnly(_magmaSplitPreviews.ToArray()),
                  WeaponResolutionBoundaryReached = WeaponResolutionBoundaryReached,
                    NextCardCostsOpponentHealth = _nextCardCostsOpponentHealth,
                    OpponentDefeatedByPayment = OpponentDefeatedByPayment,
                    OpponentHealthPayments = Array.AsReadOnly(_opponentHealthPayments.ToArray()),
                    TurnEnded = TurnEnded, EndTurnTriggersResolved = EndTurnTriggersResolved,
                    ForcedAttacks = Array.AsReadOnly(_forcedAttacks.ToArray()),
                    PendingForcedAttacks = Array.AsReadOnly(_pendingForcedAttacks.ToArray()),
                    TerrorEndTurnPreviews = Array.AsReadOnly(_terrorEndTurnPreviews.ToArray()),
                    UnresolvedEndTurnFields = Array.AsReadOnly(_unresolvedEndTurnFields.Order(StringComparer.Ordinal).ToArray()) }) { CardBuild = _snapshot.ClientBuild };
        }

        private static FrozenDictionary<string, int> ResultFlags(Entity entity)
        {
            var flags = new Dictionary<string, int>(entity.Flags, StringComparer.Ordinal) { ["ARMOR"] = entity.Armor, ["NUM_ATTACKS_THIS_TURN"] = entity.Attacks };
            if (entity.Attack is { } attack) flags["ATK"] = attack;
            if (entity.MaximumHealth is { } maximum) flags["HEALTH"] = maximum;
            if (entity.Health is { } health && entity.MaximumHealth is { } max) flags["DAMAGE"] = max - health;
            if (entity.Cost is { } cost) flags["COST"] = cost;
            return flags.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }
}
