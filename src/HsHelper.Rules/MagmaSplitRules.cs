using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record MagmaNumberRange(long Minimum, long? Maximum);
public sealed record MagmaEntityResult(int EntityId, string Type, long RemainingHealth, long RemainingArmor, bool DivineShield, bool IsAlive);
public sealed record MagmaTargetRange(int EntityId, MagmaNumberRange Damage, MagmaNumberRange RemainingHealth,
    MagmaNumberRange RemainingArmor, bool? Survives);
public sealed record MagmaSplitBranch(string Id, long PacketsApplied, long PacketsRemaining, bool IsComplete,
    string StopReason, IReadOnlyList<MagmaEntityResult> Targets, long? SourceHeroHealthAfter, long LifestealHealing);

/// <summary>Host-validated exchange boundary. Lists contain public characters and their known departed counterparts only.</summary>
public sealed record MagmaSplitState
{
    public string MatchId { get; init; }
    public long StateVersion { get; init; }
    public long EventSequence { get; init; }
    public int SourceEntityId { get; init; }
    public int AttackedEntityId { get; init; }
    private IReadOnlyList<EntitySnapshot> _entities;
    public IReadOnlyList<EntitySnapshot> EntitiesAfterCombat { get => _entities; init => _entities = Freeze(value); }
    public bool PriorResolutionPending { get; init; }
    public MagmaSplitState(string matchId, long stateVersion, long eventSequence, int sourceEntityId,
        int attackedEntityId, IReadOnlyList<EntitySnapshot> entitiesAfterCombat)
    {
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence;
        SourceEntityId = sourceEntityId; AttackedEntityId = attackedEntityId; _entities = Freeze(entitiesAfterCombat);
    }
    private static IReadOnlyList<EntitySnapshot> Freeze(IReadOnlyList<EntitySnapshot> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.Select(entity => entity is null ? null! : entity with
            { Tags = entity.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! }).ToArray());
    }
}

public sealed record MagmaSplitOutcome(string MatchId, long StateVersion, long EventSequence, int SourceEntityId,
    int AttackedEntityId, bool? TriggerActive, bool ResolvesBranches, bool IsEnumerationComplete,
    long? RequestedPackets, long? DamagePerPacket, IReadOnlyList<MagmaSplitBranch> Branches,
    IReadOnlyList<MagmaTargetRange> Targets, bool RequiresObservation, bool PriorResolutionPending,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
    public bool? IsLegal => null;
    public bool SingleHeroStateAudited { get; init; }
}

/// <summary>
/// Fixed 255790 FIR_953/dbf115751, after a validated attack exchange. One packet per current Attack;
/// Goldrinn doubles each packet, not the number of packets. No probability or actual random result is invented.
/// https://hearthstone.blizzard.com/en-gb/cards/115751-magma-hound/ .
/// Direct callers use LineEvaluator's attack adapter. This helper never duplicates attack legality or exchange.
/// </summary>
public static class MagmaSplitRules
{
    public const int MaximumPackets = 16;
    public const int MaximumStates = 4096;
    private static readonly HashSet<string> Passive = new(StringComparer.Ordinal)
        { "TAUNT", "DIVINE_SHIELD", "RUSH", "CHARGE", "WINDFURY", "MEGA_WINDFURY", "LIFESTEAL", "POISONOUS", "STEALTH", "ELUSIVE", "SPELLPOWER" };
    private static readonly string[] Modifiers = ["HEALTH_MINIMUM", "MINIMUM_HEALTH", "DAMAGE_CANT_KILL", "CANT_DAMAGE",
        "CANT_BE_DESTROYED", "HERO_CANT_BE_DESTROYED", "INCOMING_DAMAGE_MULTIPLIER", "INCOMING_DAMAGE_ADJUSTMENT",
        "INCOMING_DAMAGE_CAP", "IGNORE_DAMAGE", "OUTGOING_DAMAGE_MULTIPLIER", "OUTGOING_DAMAGE_ADJUSTMENT",
        "HEALING_DOES_DAMAGE", "HEALING_DOUBLE", "LIFESTEAL_DAMAGES_OPPOSING_HERO", "MULTIPLY_HERO_DAMAGE"];

    public static bool IsAuditedSource(CardRecord? card)
    {
        if (!Audit(card, "FIR_953", 115751, "MINION", "<b>突袭</b>。在本随从攻击随从并存活下来后，&造成等同于本随从攻击力的伤害，随机分配到所有敌人身上。", ["RUSH", "TRIGGER_VISUAL"])) return false;
        try
        {
            using var document = JsonDocument.Parse(card!.RawJson); var raw = document.RootElement;
            return raw.GetProperty("race").GetString() == "BEAST" && raw.GetProperty("races").EnumerateArray().Select(value => value.GetString())
                .ToHashSet(StringComparer.Ordinal).SetEquals(["ELEMENTAL", "BEAST"]);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }

    public static MagmaSplitOutcome Evaluate(GameSnapshot snapshot, int sourceEntityId, int attackedEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, MagmaSplitState? afterCombat = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        var results = new List<MagmaSplitBranch>(); var ranges = new List<MagmaTargetRange>();
        var finals = new List<BranchState>(); var finishedKeys = new HashSet<string>(StringComparer.Ordinal); var limitHit = false;
        bool? active = null; bool resolves = false, complete = false; long? packets = null, perPacket = null;
        var singleHeroAudited = false;
        var pending = afterCombat?.PriorResolutionPending == true;
        if (NativeCardDefinitions.HasInvalidContext(snapshot, cards, sourceEntityId, afterCombat?.EntitiesAfterCombat))
        { unknowns.Add("参与分配伤害的原生来源或公开上下文固定定义不一致，未执行原卡ID效果。"); return Result(); }
        if (snapshot.ClientBuild != "255790" || ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not (1 or 2) ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable ||
            snapshot.Quality.OpenBlockCount != 0 || snapshot.Phase != GamePhase.Main ||
            snapshot.Entities.Any(pair => !Valid(pair.Value) || pair.Key != pair.Value.Id) ||
            !snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => entity.GetIntTag("PLAYER_ID"))
                .Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("需要固定 build 255790、完整稳定且已确认我方的非观战局面。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(sourceEntityId, out var original) || original.CardId != "FIR_953" ||
            original.Kind != EntityKind.Card || original.ControllerId is not (1 or 2) || !Typed(original, "MINION", "4", true) ||
            !Identity(original) || original.Zone != "PLAY" && !(original.Zone == "HAND" && original.ControllerId == snapshot.LocalPlayerId) ||
            AssembledCardText.RequiresCustomText(original.Tags) || !cards.TryGetValue("FIR_953", out var native) || !IsAuditedSource(native) ||
            !snapshot.Entities.TryGetValue(attackedEntityId, out var attacked) || attacked.Kind != EntityKind.Card ||
            attacked.Zone != "PLAY" || attacked.ControllerId is not (1 or 2) || !Identity(attacked))
        { unknowns.Add("原始公开攻击来源、目标、归属、原生文字或固定卡面未确认，不按基础 ID 推导触发。"); return Result(); }
        if (afterCombat is null)
        { unknowns.Add("尚无主机验证的攻击交换后边界；此帮助器不重复基础攻击，也不将当前局面当成交战后的局面。"); return Result(); }
        if (afterCombat.MatchId != snapshot.MatchId || afterCombat.StateVersion != snapshot.StateVersion ||
            afterCombat.EventSequence != snapshot.EventSequence || afterCombat.SourceEntityId != sourceEntityId ||
            afterCombat.AttackedEntityId != attackedEntityId || afterCombat.EntitiesAfterCombat.Any(entity => !Valid(entity)) ||
            afterCombat.EntitiesAfterCombat.Select(entity => entity.Id).Distinct().Count() != afterCombat.EntitiesAfterCombat.Count)
        { unknowns.Add("交战覆盖的对局、状态、事件、实体或集合不一致，未采用旧或未来边界。"); return Result(); }
        var entities = afterCombat.EntitiesAfterCombat.ToDictionary(entity => entity.Id);
        if (entities.Values.Any(entity => !Identity(entity) || entity.Kind != EntityKind.Card ||
            entity.Zone is not ("PLAY" or "GRAVEYARD" or "REMOVEDFROMGAME") ||
            !snapshot.Entities.TryGetValue(entity.Id, out var before) || before.CardId != entity.CardId || before.ControllerId != entity.ControllerId ||
            !RawConsistent(before) || !RawConsistent(entity) ||
            entity.Zone == "PLAY" && !Typed(entity, "MINION", "4", true) && !Typed(entity, "HERO", "3", true) ||
            before.Zone != "PLAY" && !(before.ControllerId == snapshot.LocalPlayerId && before.Zone == "HAND" && before.CardId is not null)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" &&
                (ClaimsCharacter(entity, cards))).Any(entity => !entities.ContainsKey(entity.Id)) ||
            !entities.TryGetValue(sourceEntityId, out var source) || !entities.ContainsKey(attackedEntityId) ||
            !Typed(source, "MINION", "4", true) || AssembledCardText.RequiresCustomText(source.Tags))
        { unknowns.Add("覆盖缺少原公开角色去向、包含新实体或私有区域，或身份/归属/文字有冲突，不能补齐随机目标。"); return Result(); }
        if (pending)
        { unknowns.Add("交战后的亡语、复生或其他前置结算尚未完成，来源攻击力及剩余随机目标仍待观察。"); return Result(); }
        var silence = Flag(source, "SILENCED", "188", false); var dormant = Flag(source, "DORMANT", "1518", false);
        var originalSilence = Flag(original, "SILENCED", "188", false); var originalDormant = Flag(original, "DORMANT", "1518", false);
        if (originalSilence is null || originalDormant is null ||
            new[] { ("POISONOUS", "363"), ("LIFESTEAL", "685"), ("IMMUNE", "240"), ("DIVINE_SHIELD", "194"), ("IMMUNE_WHILE_ATTACKING", "373") }
                .Any(pair => Flag(original, pair.Item1, pair.Item2, false) is null) ||
            new[] { ("ATK", "47"), ("HEALTH", "45"), ("DAMAGE", "44") }.Any(pair =>
                (original.Tags.ContainsKey(pair.Item1) || original.Tags.ContainsKey(pair.Item2)) && Number(original, pair.Item1, pair.Item2) is null))
        { unknowns.Add("原始来源属性存在显式冲突，覆盖不能修复原始反证后启用原生触发。"); return Result(); }
        var sourceHealth = Health(source); var attack = Number(source, "ATK", "47");
        if (source.Zone != "PLAY" || sourceHealth is <= 0 || silence == true || dormant == true || Typed(attacked, "HERO", "3", true))
        {
            active = false; packets = 0; resolves = complete = true;
            results.Add(new("branch-1", 0, 0, true, "NoNativeTrigger", Array.AsReadOnly(Array.Empty<MagmaEntityResult>()), null, 0));
            coverage.Add("FIR_953-no-native-trigger-after-inactive-source-or-non-minion-attack"); return Result();
        }
        if (!Typed(attacked, "MINION", "4", true) || originalSilence == true || originalDormant == true || silence is null || dormant is null || sourceHealth is null || attack is null or < 0 ||
            entities.Values.Any(entity => entity.Zone == "PLAY" && AssembledCardText.RequiresCustomText(entity.Tags)))
        { unknowns.Add("随从攻击条件、当前来源生命/攻击力、沉默/休眠或场上拼装文字未明确，触发保持未知。"); return Result(); }
        active = true; packets = attack;
        if (entities.Values.Any(entity => entity.Zone == "PLAY" && Modifiers.Any(tag => entity.Tags.ContainsKey(tag) && Number(entity, tag) != 0)))
        { unknowns.Add("存在未覆盖的伤害或治疗修正，未套用未修正伤害作为实际分配。"); return Result(); }
        var golds = 0;
        if (entities.Values.Any(entity => entity.Zone == "PLAY" && entity.CardId != "EDR_480" && Flag(entity, "SILENCED", "188", false) != true &&
            Mechanics(entity, cards)?.Contains("AURA") == true))
        { unknowns.Add("存在未审计的持续光环，逐发伤害或目标规则未确认。"); return Result(); }
        foreach (var god in entities.Values.Where(entity => entity.CardId == "EDR_480" && entity.ControllerId == source.ControllerId && entity.Zone == "PLAY"))
        {
            if (!Typed(god, "MINION", "4", true) || Health(god) is null || Flag(god, "SILENCED", "188", false) is null ||
                Flag(god, "DORMANT", "1518", false) is null || !cards.TryGetValue("EDR_480", out var definition) ||
                !Audit(definition, "EDR_480", 114254, "MINION", "<b>突袭</b>。友方野兽造成的伤害翻倍。", ["AURA", "RUSH"]))
            { unknowns.Add("戈德林的固定身份或当前存活、沉默、休眠状态未确认，不能给出确定倍数。"); return Result(); }
            if (Health(god) > 0 && Flag(god, "SILENCED", "188", false) == false && Flag(god, "DORMANT", "1518", false) == false) golds++;
        }
        // A fully audited native FIR_953 has both native races. An explicit current race never falls back.
        var race = !source.Tags.ContainsKey("CARDRACE") && !source.Tags.ContainsKey("200") ? true : Race(source);
        if (golds > 0 && race is null)
        { unknowns.Add("来源当前野兽类别存在缺失或冲突，戈德林是否放大尚未确认。"); return Result(); }
        perPacket = race == false ? 1 : 1L << Math.Min(golds, 30);
        if (golds > 30) { perPacket = null; unknowns.Add("伤害放大来源数量超出审计界限。"); return Result(); }
        var poison = Flag(source, "POISONOUS", "363", false); var life = Flag(source, "LIFESTEAL", "685", false);
        if (poison is null || life is null) { unknowns.Add("来源剧毒或吸血标签冲突，未选择任一份值。"); return Result(); }
        var heroes = entities.Values.Where(entity => entity.Zone == "PLAY" && entity.ControllerId == source.ControllerId && Typed(entity, "HERO", "3", true)).ToArray();
        if (heroes.Length != 1 || Health(heroes[0]) is not { } heroHealth || heroHealth <= 0 || Number(heroes[0], "HEALTH", "45") is not { } heroMax ||
            Flag(heroes[0], "CANT_BE_HEALED", "239", false) is not { } cantHeal)
        { unknowns.Add("来源所属英雄及当前吸血恢复上限未唯一确认。"); return Result(); }
        var targets = new List<Target>();
        foreach (var entity in entities.Values.Where(entity => entity.Zone == "PLAY" && entity.ControllerId != source.ControllerId).OrderBy(entity => entity.Id))
        {
            if (!Typed(entity, "HERO", "3", true) && !Typed(entity, "MINION", "4", true)) continue;
            var dead = Health(entity); var sleep = Flag(entity, "DORMANT", "1518", false); var untouchable = Flag(entity, "UNTOUCHABLE", null, false);
            if (dead <= 0 || sleep == true || untouchable == true) continue;
            var hero = Typed(entity, "HERO", "3", true);
            var shield = Flag(entity, "DIVINE_SHIELD", "194", Printed(entity, cards, "DIVINE_SHIELD"));
            var immune = Flag(entity, "IMMUNE", "240", Printed(entity, cards, "IMMUNE"));
            var deathrattle = Flag(entity, "DEATHRATTLE", "217", Printed(entity, cards, "DEATHRATTLE"));
            var reborn = Flag(entity, "REBORN", "1085", Printed(entity, cards, "REBORN"));
            var armor = Number(entity, "ARMOR", "292") ?? (hero ? null : 0);
            if (dead is null || sleep is null || untouchable is null || shield is null || immune is null || deathrattle is null || reborn is null || armor is null or < 0 ||
                Flag(entity, "SILENCED", "188", false) is null ||
                (entity.Tags.ContainsKey("ARMOR") || entity.Tags.ContainsKey("292")) && Number(entity, "ARMOR", "292") is null)
            { unknowns.Add("敌方当前生命、护甲、盾、免疫或触发标记缺失/冲突，剩余目标不能精确枚举。"); return Result(); }
            targets.Add(new(entity.Id, hero ? "HERO" : "MINION", dead.Value, armor.Value, shield.Value, immune.Value,
                deathrattle.Value || reborn.Value, UnsafeText(entity, cards), 0));
        }
        if (targets.Count == 0 || targets.Count(target => target.Type == "HERO") != 1)
        { unknowns.Add("敌方存活英雄与完整随机角色集合未确认。"); return Result(); }
        var initial = targets.ToArray(); var damageCeiling = checked(packets.Value * perPacket.Value);
        singleHeroAudited = targets.Count == 1 && targets[0].Type == "HERO" && !targets[0].UnknownEffect;
        assumptions.Add("从主机已验证的交战后边界开始；逐发一点基础伤害按当前戈德林数放大，不叠加法术伤害。分支没有概率或实际随机结果。");
        assumptions.Add("各目标区间是相关分支的边际界限，不能独立拼接；任何分支都不是新的权威局面，不能用于接续动作或直接认证斩杀。");
        coverage.AddRange(["FIR_953-build255790-audited-native-after-minion-attack", "one-packet-per-current-attack",
            "goldrinn-per-packet-not-extra-packets", "shield-armor-poison-lifesteal-per-packet", "no-random-probability-or-authoritative-state"]);
        if (packets == 0)
        {
            resolves = complete = true; Finish(new(initial, heroHealth, 0), 0, true, "AllPacketsApplied");
        }
        else if (targets.Any(target => target.Immune))
        {
            unknowns.Add("多目标下免疫角色是否进入随机选择或消耗一发未获固定脚本证实，未虚构精确分配。");
            Finish(new(initial, heroHealth, 0), 0, false, "ImmuneTargetPolicyUnknown");
        }
        else if (packets > MaximumPackets)
        {
            unknowns.Add("当前攻击力超过有限逐发审计上限，未逐次循环或把少量分支当完整空间。");
            Finish(new(initial, heroHealth, 0), 0, false, "PacketLimit");
        }
        else
        {
            var frontier = new List<BranchState> { new(initial, heroHealth, 0) }; var truncated = false; var interrupted = false;
            for (long step = 0; step < packets && frontier.Count > 0; step++)
            {
                var next = new Dictionary<string, BranchState>(StringComparer.Ordinal);
                foreach (var branch in frontier)
                {
                    foreach (var index in Enumerable.Range(0, branch.Targets.Length).Where(index => branch.Targets[index].Health > 0))
                    {
                    var target = branch.Targets[index];
                    if (target.UnknownEffect)
                    { Finish(branch, step, false, "UncoveredTargetEffect"); interrupted = true; continue; }
                    var cloned = branch.Targets.ToArray(); var dealt = target.Shield ? 0 : perPacket.Value;
                    var armorLost = Math.Min(target.Armor, dealt);
                    var healthAfter = target.Health - (dealt - armorLost);
                    if (dealt > 0 && poison == true && target.Type == "MINION") healthAfter = 0;
                    cloned[index] = target with { Shield = false, Health = healthAfter, Armor = target.Armor - armorLost, Damage = target.Damage + dealt };
                    var healed = life == true && !cantHeal ? Math.Max(0, Math.Min(heroMax, branch.HeroHealth + dealt) - branch.HeroHealth) : 0;
                    var value = new BranchState(cloned, branch.HeroHealth + healed, branch.Healing + healed);
                    var applied = step + 1;
                    if (target.Type == "HERO" && healthAfter <= 0) Finish(value, applied, true, "HeroDied");
                    else if (healthAfter <= 0 && target.DeathEffect)
                    { Finish(value, applied, false, "DeathrattleOrReborn"); interrupted = true; }
                    else if (applied == packets) Finish(value, applied, true, "AllPacketsApplied");
                    else next.TryAdd(Key(value), value);
                        if (limitHit || next.Count + results.Count > MaximumStates) { truncated = true; break; }
                    }
                    if (truncated) break;
                }
                if (truncated) break;
                frontier = next.Values.ToList();
            }
            complete = !truncated && !interrupted && !limitHit; resolves = results.Count > 0;
            if (truncated || limitHit) { unknowns.Add("随机分支状态数超过上限，已枚举子集不是完整空间，全局区间已回退到保守宽界。"); }
            if (interrupted) unknowns.Add("部分分支遇未覆盖的受伤触发、亡语或复生，已在该分支停止；不创建新随从或猜测后续目标。");
        }
        resolves |= results.Count > 0;
        var unknownContinuation = !complete && (initial.Any(target => target.UnknownEffect || target.DeathEffect));
        if (unknownContinuation)
            unknowns.Add("未覆盖触发可改变治疗、伤害来源或后续目标；所列仅已计算前缀，不提供可能无效的全局生命或伤害上界。");
        foreach (var target in unknownContinuation ? Array.Empty<Target>() : initial)
        {
            if (complete)
            {
                var states = results.Select(branch => branch.Targets.Single(value => value.EntityId == target.Id)).ToArray();
                // Damage is reconstructed from branch accounting, including armor and overkill, not from capped life loss.
                var damage = finals.Select(value => value.Targets.Single(item => item.Id == target.Id).Damage).ToArray();
                var survive = states.Select(state => state.IsAlive).Distinct().ToArray();
                ranges.Add(new(target.Id, Range(damage), Range(states.Select(value => value.RemainingHealth)),
                    Range(states.Select(value => value.RemainingArmor)), survive.Length == 1 ? survive[0] : null));
            }
            else ranges.Add(new(target.Id, new(0, target.Immune ? 0 : damageCeiling), new(Math.Min(0, target.Health - damageCeiling), target.Health),
                new(0, target.Armor), target.Immune ? true : null));
        }
        return Result();

        void Finish(BranchState branch, long applied, bool finished, string reason)
        {
            var key = Key(branch) + ":" + applied + ":" + reason;
            if (finishedKeys.Contains(key)) return;
            if (results.Count >= MaximumStates) { limitHit = true; return; }
            finishedKeys.Add(key);
            finals.Add(branch);
            results.Add(new("branch-" + (results.Count + 1), applied, packets!.Value - applied, finished, reason,
                Array.AsReadOnly(branch.Targets.Select(target => new MagmaEntityResult(target.Id, target.Type, target.Health,
                    target.Armor, target.Shield, target.Health > 0)).ToArray()), branch.HeroHealth, branch.Healing));
        }
        MagmaSplitOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, sourceEntityId,
            attackedEntityId, active, resolves, complete, packets, perPacket, Array.AsReadOnly(results.ToArray()), Array.AsReadOnly(ranges.ToArray()),
            !complete || results.Count > 1 || packets > 0, pending, Freeze(unknowns), Freeze(assumptions), Freeze(coverage))
            { SingleHeroStateAudited = singleHeroAudited };
    }

    private sealed record Target(int Id, string Type, long Health, long Armor, bool Shield, bool Immune, bool DeathEffect, bool UnknownEffect, long Damage);
    private sealed record BranchState(Target[] Targets, long HeroHealth, long Healing);
    private static string Key(BranchState branch) => branch.HeroHealth + ":" + branch.Healing + ":" +
        string.Join(";", branch.Targets.Select(target => $"{target.Id},{target.Health},{target.Armor},{target.Shield},{target.Damage}"));
    private static MagmaNumberRange Range(IEnumerable<long> values) { var array = values.ToArray(); return new(array.Min(), array.Max()); }
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
    private static bool Valid(EntitySnapshot? entity) => entity is not null && entity.Tags is not null;
    private static bool ClaimsCharacter(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        new[] { "CARDTYPE", "202" }.Any(key => entity.Tags.GetValueOrDefault(key) is "MINION" or "4" or "HERO" or "3") ||
        entity.CardId is { } id && cards.TryGetValue(id, out var card) && card.Type is "MINION" or "HERO";
    private static bool RawConsistent(EntitySnapshot entity) =>
        new[] { ("SILENCED", "188"), ("DORMANT", "1518"), ("IMMUNE", "240"), ("DIVINE_SHIELD", "194"),
            ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("DEATHRATTLE", "217"), ("REBORN", "1085"),
            ("IMMUNE_WHILE_ATTACKING", "373"), ("CANT_BE_HEALED", "239") }.All(pair => Flag(entity, pair.Item1, pair.Item2, false) is not null) &&
        new[] { ("ATK", "47"), ("HEALTH", "45"), ("DAMAGE", "44"), ("ARMOR", "292") }.All(pair =>
            !entity.Tags.ContainsKey(pair.Item1) && !entity.Tags.ContainsKey(pair.Item2) || Number(entity, pair.Item1, pair.Item2) is not null);
    private static bool Typed(EntitySnapshot entity, string type, string code, bool required) =>
        (!required || entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var first) || first == type || first == code) &&
        (!entity.Tags.TryGetValue("202", out var second) || second == type || second == code);
    private static bool Identity(EntitySnapshot entity) => entity.Id > 0 && entity.ControllerId is 1 or 2 &&
        (!entity.Tags.ContainsKey("50") || Number(entity, "CONTROLLER", "50") == entity.ControllerId) &&
        (!entity.Tags.ContainsKey("ENTITY_ID") && !entity.Tags.ContainsKey("53") || Number(entity, "ENTITY_ID", "53") == entity.Id) &&
        (!entity.Tags.TryGetValue("49", out var zone) || zone == entity.Zone || zone == (entity.Zone switch { "PLAY" => "1", "HAND" => "3", "GRAVEYARD" => "4", "REMOVEDFROMGAME" => "5", _ => "?" }));
    private static long? Health(EntitySnapshot entity) => Number(entity, "HEALTH", "45") is >= 0 and var maximum &&
        Number(entity, "DAMAGE", "44") is >= 0 and var damage ? maximum - damage : null;
    private static long? Number(EntitySnapshot entity, string name, string? alias = null)
    {
        var a = entity.Tags.TryGetValue(name, out var first); var b = alias is not null && entity.Tags.TryGetValue(alias, out _);
        long? Parse(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= int.MinValue and <= int.MaxValue ? n : null;
        var x = a ? Parse(first) : null; var y = b ? Parse(entity.Tags[alias!]) : null;
        return a && x is null || b && y is null || a && b && x != y ? null : x ?? y;
    }
    private static bool? Flag(EntitySnapshot entity, string name, string? alias, bool fallback)
    {
        if (!entity.Tags.ContainsKey(name) && (alias is null || !entity.Tags.ContainsKey(alias))) return fallback;
        return Number(entity, name, alias) switch { 0 => false, 1 => true, _ => null };
    }
    private static bool? Race(EntitySnapshot entity)
    {
        int? Parse(string value) => value switch { "BEAST" => 20, "ALL" => 26, _ => int.TryParse(value, out var n) ? n : null };
        var a = entity.Tags.TryGetValue("CARDRACE", out var first); var b = entity.Tags.TryGetValue("200", out var second);
        var x = a ? Parse(first!) : null; var y = b ? Parse(second!) : null;
        if (!a && !b || a && x is null || b && y is null || a && b && x != y) return null;
        return (x ?? y) is 20 or 26;
    }
    private static bool Printed(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards, string keyword) =>
        Flag(entity, "SILENCED", "188", false) == false && Mechanics(entity, cards)?.Contains(keyword) == true;
    private static HashSet<string>? Mechanics(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card) || card is null || card.Id != id) return null;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (raw.GetProperty("id").GetString() != id || raw.GetProperty("dbfId").GetInt32() != card.DbfId || raw.GetProperty("type").GetString() != card.Type) return null;
            return raw.TryGetProperty("mechanics", out var list) ? list.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { return null; }
    }
    private static bool UnsafeText(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (Flag(entity, "SILENCED", "188", false) == true || Typed(entity, "HERO", "3", true) && entity.CardId?.StartsWith("HERO_", StringComparison.Ordinal) == true) return false;
        if (entity.CardId == "FIR_953") return !cards.TryGetValue(entity.CardId, out var hound) || !IsAuditedSource(hound);
        if (entity.CardId == "EDR_480") return !cards.TryGetValue(entity.CardId, out var god) ||
            !Audit(god, "EDR_480", 114254, "MINION", "<b>突袭</b>。友方野兽造成的伤害翻倍。", ["AURA", "RUSH"]);
        if (entity.CardId == "EDR_227" && cards.TryGetValue(entity.CardId, out var bear) &&
            Audit(bear, "EDR_227", 114020, "MINION", "<b>突袭</b>。<b>亡语：</b><b>灌注</b>你的英雄技能。", ["RUSH", "DEATHRATTLE"])) return false;
        if (entity.CardId == "CORE_UNG_912" && cards.TryGetValue(entity.CardId, out var macaw) &&
            Audit(macaw, "CORE_UNG_912", 97376, "MINION", "<b>战吼：</b>&随机将一张野兽牌置入你的手牌。", ["BATTLECRY"])) return false;
        var mechanics = Mechanics(entity, cards);
        if (mechanics is null) return true;
        if (mechanics.Any(mechanic => !Passive.Contains(mechanic) && mechanic is not ("DEATHRATTLE" or "REBORN"))) return true;
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card)) return true;
        var plain = Regex.Replace(card.Text ?? "", "<[^>]+>|\\$|法术伤害\\s*\\+\\d+|Spell Damage\\s*\\+\\d+|[\\s&。；，,.!！:：]|嘲讽|圣盾|冲锋|突袭|风怒|吸血|剧毒|潜行|扰魔|复生|Taunt|Divine Shield|Charge|Rush|Windfury|Lifesteal|Poisonous|Stealth|Elusive|Reborn", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return plain.Length > 0;
    }
    private static bool Audit(CardRecord? card, string id, int dbf, string type, string text, string[] mechanics)
    {
        if (!NativeCardDefinitions.IsAudited(NativeCardDefinitions.Build, id, card)) return false;
        if (card is null || card.Id != id || card.DbfId != dbf || card.Type != type || card.Text != text) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return raw.ValueKind == JsonValueKind.Object && raw.EnumerateObject().Select(property => property.Name).Distinct().Count() == raw.EnumerateObject().Count() &&
                raw.GetProperty("id").GetString() == id && raw.GetProperty("dbfId").GetInt32() == dbf && raw.GetProperty("type").GetString() == type &&
                raw.GetProperty("text").GetString() == text && raw.GetProperty("mechanics").GetArrayLength() == mechanics.Length &&
                raw.GetProperty("mechanics").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal).SetEquals(mechanics);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { return false; }
    }
}
