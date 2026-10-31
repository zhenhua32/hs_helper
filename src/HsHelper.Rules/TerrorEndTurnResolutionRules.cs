using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record TerrorEndTurnState
{
    public string MatchId { get; init; }
    public long StateVersion { get; init; }
    public long EventSequence { get; init; }
    public int TerrorEntityId { get; init; }
    private IReadOnlyList<EntitySnapshot> _entities;
    public IReadOnlyList<EntitySnapshot> EntitiesAtEndTurn { get => _entities; init => _entities = Freeze(value); }
    public bool PriorResolutionPending { get; init; }
    public TerrorEndTurnState(string matchId, long stateVersion, long eventSequence, int terrorEntityId,
        IReadOnlyList<EntitySnapshot> entitiesAtEndTurn)
    {
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence; TerrorEntityId = terrorEntityId;
        _entities = Freeze(entitiesAtEndTurn);
    }
    private static IReadOnlyList<EntitySnapshot> Freeze(IReadOnlyList<EntitySnapshot> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.Select(entity => entity is null ? null! : entity with
            { Tags = entity.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! }).ToArray());
    }
}

public sealed record TerrorForcedCombat(int AttackerEntityId, int TargetEntityId, long DamageToTerror,
    long DamageToAttacker, bool AttackerSurvived, bool TerrorSurvived);
public sealed record TerrorEntityResult(int EntityId, string Type, long RemainingHealth, long RemainingArmor,
    bool DivineShield, bool IsAlive);
public sealed record TerrorEndTurnBranch(string Id, bool IsComplete, string StopReason,
    IReadOnlyList<TerrorForcedCombat> Combats, IReadOnlyList<TerrorEntityResult> Entities,
    long? OwnHeroHealthAfter, long? OpponentHeroHealthAfter, long LifestealHealing);
public sealed record TerrorEndTurnOutcome(string MatchId, long StateVersion, long EventSequence,
    int TerrorEntityId, bool? TriggerActive, bool ResolvesBranches, bool IsEnumerationComplete,
    IReadOnlyList<TerrorEndTurnBranch> Branches, bool RequiresObservation, bool PriorResolutionPending,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
    public bool? IsLegal => null;
}

/// <summary>
/// Build 255790 CORE_TTN_866/dbf129169. Enumerates conditional forced-combat schedules, not a random
/// ordering policy or normal attack readiness. https://hearthstone.blizzard.com/en-us/cards/129169 .
/// Full states and remaining attackers are merged; Combats is one witness for the resulting state.
/// Unknown death/after-attack effects stop at the computed simultaneous exchange, without global bounds.
/// </summary>
public static class TerrorEndTurnResolutionRules
{
    public const int MaximumAttackers = 7;
    public const int MaximumStates = 4096;
    public const int MaximumBranches = 256;
    public static bool IsAuditedSource(CardRecord? card) => AuditedTerror(card);
    // Same fixed type/text/mechanics fingerprints audited for the incoming Trap context.
    private static readonly IReadOnlyDictionary<string, (int Dbf, string Fingerprint)> ActivePowers = new Dictionary<string, (int, string)>
    {
        ["EDR_850p"] = (114052, "ACE0D64FEF32A08AD76D5992D5EB701DBE7F59E57763A50CB209CEE643CBF501"),
        ["HERO_01bp"] = (725, "29DB59083D4002189B0BEFC92D448A0A9102CB6E036776224113EDA8DC90D719"),
        ["HERO_02bp"] = (687, "FA252E610387B747D4F7EC1D853F76FD47F8CEEF35D58729974942479EB4D614"),
        ["HERO_03bp"] = (730, "42F8C5F9045F1BE30A430585C31BABE79378B791A39A003EDA2327F13D9DE18F"),
        ["HERO_04bp"] = (472, "C546EF5FC608321448569418531C8E7C6BF5076CAD176B602F72C1938EE01DBA"),
        ["HERO_05bp"] = (229, "938D049356F13E0A8BC78EB427F106765BAD46FDA5DD01B670AF255E28D473F1"),
        ["HERO_06bp"] = (1123, "073509D747CD04CFD50413805036FFFE5E0A0C7C889EBEA1BF1D95FA976FE353"),
        ["HERO_07bp"] = (300, "FE700C8EE079FE53B3C9201E87B0D2199D101D402BAF2263BD5FC63137090F65"),
        ["HERO_08bp"] = (807, "39C40100227D93D81010A1B54D955AE94D96EB4FABEAF089AB87EEBBAF5F7B84"),
        ["HERO_09bp"] = (479, "2E459FFB7D8C7BFB8A66E464C75D6A5B1955A85B4F14BFDCEB85BC3A8DD0084E"),
        ["HERO_10bp"] = (60224, "55B7F672A61406178A32EF7D1B8D0D1E410C6206DF38ECA113B73F38430777D6"),
        ["HERO_11bp"] = (78162, "84B054DF96F7FE8D2C983A1F74444875849DEA6EB88276265C87BCFDDE54E3AC")
    };
    private static readonly HashSet<string> Passive = ["TAUNT", "DIVINE_SHIELD", "CHARGE", "RUSH", "WINDFURY", "MEGA_WINDFURY", "LIFESTEAL", "POISONOUS", "STEALTH", "ELUSIVE", "SPELLPOWER", "REBORN"];
    private static readonly (string Name, string? Alias)[] Flags = [("SILENCED", "188"), ("DORMANT", "1518"), ("UNTOUCHABLE", "448"), ("STEALTH", "191"), ("CANT_BE_ATTACKED", "245"),
        ("DIVINE_SHIELD", "194"), ("IMMUNE", "240"), ("IMMUNE_WHILE_ATTACKING", "373"), ("LIFESTEAL", "685"),
        ("POISONOUS", "363"), ("DEATHRATTLE", "217"), ("REBORN", "1085"), ("CANT_BE_HEALED", "239")];
    private static readonly (string Name, string? Alias)[] Modifiers = [("HEALTH_MINIMUM", "337"), ("MINIMUM_HEALTH", null), ("DAMAGE_CANT_KILL", null),
        ("CANT_DAMAGE", "222"), ("CANT_BE_DESTROYED", "247"), ("HERO_CANT_BE_DESTROYED", "4439"), ("INCOMING_DAMAGE_MULTIPLIER", "236"),
        ("INCOMING_DAMAGE_ADJUSTMENT", "237"), ("INCOMING_DAMAGE_CAP", "238"), ("IGNORE_DAMAGE", "1"), ("OUTGOING_DAMAGE_MULTIPLIER", "275"),
        ("OUTGOING_DAMAGE_ADJUSTMENT", "274"), ("HEALING_DOES_DAMAGE", "326"), ("HEALING_DOUBLE", "357"), ("SPELL_HEALING_DOUBLE", "357"),
        ("LIFESTEAL_DAMAGES_OPPOSING_HERO", "1675"), ("MULTIPLY_HERO_DAMAGE", "374"), ("EXTRA_END_TURN_EFFECT", null), ("EXTRA_TURN_END_EFFECT", null)];
    private sealed record Body(int Id, int Owner, string Type, long Maximum, long Health, long Armor, long Attack,
        bool Shield, bool Immune, bool WhileAttackingImmune, bool Poison, bool Life, bool CantHeal, bool Death,
        bool AfterAttack, bool Goldrinn, bool? Beast)
    { public bool Alive => Health > 0; }
    private sealed record Node(Dictionary<int, Body> Bodies, int[] Remaining, List<TerrorForcedCombat> Combats, long Healing);

    public static TerrorEndTurnOutcome Evaluate(GameSnapshot snapshot, int terrorId,
        IReadOnlyDictionary<string, CardRecord> cards, TerrorEndTurnState? hostOverlay = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        var branches = new List<TerrorEndTurnBranch>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var terminals = new HashSet<string>(StringComparer.Ordinal); bool? active = null; var limit = false;
        var pending = hostOverlay?.PriorResolutionPending == true;
        if (snapshot.ClientBuild != "255790" || ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not (1 or 2) ||
            snapshot.ActivePlayerId != snapshot.LocalPlayerId || snapshot.Phase != GamePhase.Main ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Entities.Any(pair => !Valid(pair.Value) || pair.Key != pair.Value.Id) ||
            !snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => entity.GetIntTag("PLAYER_ID")).Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("需要固定255790、完整稳定且轮到我方的非观战局面及唯一双方玩家。"); return Result(); }
        var currentPlayers = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("CURRENT_PLAYER") == 1).ToArray();
        if (currentPlayers.Length > 1 || currentPlayers.Length == 1 && currentPlayers[0].GetIntTag("PLAYER_ID") != snapshot.ActivePlayerId)
        { unknowns.Add("当前玩家标签与行动方冲突，不能启动己方回合末。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(terrorId, out var original) || original.CardId != "CORE_TTN_866" ||
            original.Kind != EntityKind.Card || original.ControllerId != snapshot.LocalPlayerId || !Identity(original) || !Typed(original, "MINION", "4") ||
            original.Zone is not ("PLAY" or "HAND" or "GRAVEYARD" or "REMOVEDFROMGAME") ||
            !AuditedTerror(cards.GetValueOrDefault("CORE_TTN_866")) ||
            AssembledCardText.RequiresCustomText(original.Tags) || !RawConsistent(original))
        { unknowns.Add("神秘恐魔的原始身份、归属、区域、固定原生卡面或当前标签有冲突，未按基础ID复用效果。"); return Result(); }
        IReadOnlyDictionary<int, EntitySnapshot> entities = snapshot.Entities;
        if (hostOverlay is not null)
        {
            if (hostOverlay.MatchId != snapshot.MatchId || hostOverlay.StateVersion != snapshot.StateVersion || hostOverlay.EventSequence != snapshot.EventSequence ||
                hostOverlay.TerrorEntityId != terrorId || hostOverlay.EntitiesAtEndTurn.Any(entity => !Valid(entity)) ||
                hostOverlay.EntitiesAtEndTurn.Select(entity => entity.Id).Distinct().Count() != hostOverlay.EntitiesAtEndTurn.Count ||
                !hostOverlay.EntitiesAtEndTurn.Select(entity => entity.Id).ToHashSet().SetEquals(snapshot.Entities.Keys))
            { unknowns.Add("回合末覆盖未绑定同局、状态、事件和完整实体集合，未采用漏项、重复、旧或未来覆盖。"); return Result(); }
            entities = hostOverlay.EntitiesAtEndTurn.ToDictionary(entity => entity.Id);
            foreach (var entity in entities.Values)
            {
                var before = snapshot.Entities[entity.Id];
                var hasAttachment = before.Tags.ContainsKey("ATTACHED") || before.Tags.ContainsKey("40");
                var attachment = Number(before, "ATTACHED", "40");
                var activeAttachment = hasAttachment && (attachment is null ||
                    entities.TryGetValue((int)attachment, out var attached) && attached.Zone == "PLAY" ||
                    snapshot.Entities.TryGetValue((int)attachment, out var originalAttached) && originalAttached.Zone == "PLAY");
                if (entity.CardId != before.CardId || entity.Kind != before.Kind || entity.ControllerId != before.ControllerId ||
                    before.Zone != entity.Zone && !(before.Zone == "PLAY" && entity.Zone is "GRAVEYARD" or "REMOVEDFROMGAME" ||
                        before.Zone == "HAND" && before.ControllerId == snapshot.LocalPlayerId && entity.Zone is "PLAY" or "SECRET" or "GRAVEYARD") ||
                    ClaimsCharacter(before, cards) && (!RawConsistent(before) || !RawConsistent(entity) ||
                        Flag(before, "SILENCED", "188") != Flag(entity, "SILENCED", "188") || Flag(before, "DORMANT", "1518") != Flag(entity, "DORMANT", "1518") ||
                        before.Tags.GetValueOrDefault("CARDRACE") != entity.Tags.GetValueOrDefault("CARDRACE") || before.Tags.GetValueOrDefault("200") != entity.Tags.GetValueOrDefault("200")) ||
                    before.Zone == "PLAY" && Typed(before, "HERO", "3") && Health(before) <= 0 && Health(entity) > 0 ||
                    (before.Zone == "PLAY" || before.Kind == EntityKind.Player || activeAttachment) && HasModifier(before) ||
                    hasAttachment && (before.Tags.GetValueOrDefault("ATTACHED") != entity.Tags.GetValueOrDefault("ATTACHED") || before.Tags.GetValueOrDefault("40") != entity.Tags.GetValueOrDefault("40")) ||
                    before.Zone == "PLAY" && ClaimsCharacter(before, cards) && (Health(before) is null ||
                        Typed(before, "MINION", "4") && Number(before, "ATK", "47") is null || Typed(before, "HERO", "3") && Number(before, "ARMOR", "292") is null) ||
                    before.Zone == "HAND" && entity.Zone == "PLAY" && (Number(before, "ATK", "47") is null || Number(before, "HEALTH", "45") is null))
                { unknowns.Add("覆盖改变原始身份/归属、修复标签反证或用印刷值补齐入场属性，未执行强制交换。"); return Result(); }
            }
        }
        var terror = entities[terrorId];
        if (!Identity(terror) || !Typed(terror, "MINION", "4") || !RawConsistent(terror) || AssembledCardText.RequiresCustomText(terror.Tags))
        { unknowns.Add("当前恐魔身份、类别、归属或原生文字不一致。"); return Result(); }
        if (terror.Zone != "PLAY" || Health(terror) is <= 0 || Flag(terror, "SILENCED", "188") == true || Flag(terror, "DORMANT", "1518") == true)
        {
            if (pending) { unknowns.Add("恐魔目前不活动，但前序结算仍待观察，不能将当前去向当作最终回合末状态。"); return Result(); }
            active = false; branches.Add(new("branch-1", true, "InactiveSource", [], [], null, null, 0)); return Result();
        }
        if (Health(terror) is null || Number(terror, "ATK", "47") is null or < 0 || Flag(terror, "UNTOUCHABLE", "448") == true)
        { unknowns.Add("恐魔当前攻击/生命未知或不可交互，未展开强制战斗。"); return Result(); }
        active = true;
        if (pending) { unknowns.Add("前序死亡、抽牌、随机或回合末结算尚未完成，不用覆盖代替新观察。"); return Result(); }
        var local = snapshot.LocalPlayerId!.Value; var opponent = local == 1 ? 2 : 1;
        var characters = entities.Values.Where(entity => entity.Zone == "PLAY" && ClaimsCharacter(entity, cards)).ToArray();
        if (characters.Any(entity => !Identity(entity) || !Typed(entity, "MINION", "4") && !Typed(entity, "HERO", "3") ||
            !RawConsistent(entity) || AssembledCardText.RequiresCustomText(entity.Tags)))
        { unknowns.Add("公开角色集合存在未知类别/归属、属性冲突或自定义文字，不能遗漏潜在攻击者或修正来源。"); return Result(); }
        var heroes = characters.Where(entity => Typed(entity, "HERO", "3")).ToArray();
        if (heroes.Length != 2 || heroes.Count(entity => entity.ControllerId == local) != 1 || heroes.Count(entity => entity.ControllerId == opponent) != 1 ||
            heroes.Any(entity => entity.CardId?.StartsWith("HERO_", StringComparison.Ordinal) != true))
        { unknowns.Add("双方当前原生英雄未唯一确认，不能精确核算吸血上限。"); return Result(); }
        var ownHeroId = heroes.Single(entity => entity.ControllerId == local).Id;
        var enemyHeroId = heroes.Single(entity => entity.ControllerId == opponent).Id;
        if (entities.Values.Where(entity => entity.Kind == EntityKind.Player).Any(player =>
            player.GetIntTag("HERO_ENTITY") is { } id && id != (player.GetIntTag("PLAYER_ID") == local ? ownHeroId : enemyHeroId)))
        { unknowns.Add("玩家链接的英雄与公开英雄集合不一致。"); return Result(); }
        foreach (var entity in entities.Values)
        {
            var hasAttachment = entity.Tags.ContainsKey("ATTACHED") || entity.Tags.ContainsKey("40");
            var attachedToLiveBoard = hasAttachment && (Number(entity, "ATTACHED", "40") is not { } attachedId ||
                entities.TryGetValue((int)attachedId, out var attachedTarget) && attachedTarget.Zone == "PLAY");
            if ((entity.Zone is "PLAY" or "SECRET" || entity.Kind == EntityKind.Player || attachedToLiveBoard) && HasModifier(entity))
            { unknowns.Add("存在未覆盖或非法的伤害、治疗或回合末次数修正，未先给出未修正数值。"); return Result(); }
            if (entity.Zone == "SECRET")
            {
                if (entity.ControllerId == local && KnownOwnSecret(entity, cards)) continue;
                unknowns.Add("存在可能改变战斗或死亡的奥秘区效果，未读取私有身份或假定不触发。"); return Result();
            }
            if (entity.Zone == "HAND" && entity.ControllerId == local && (entity.CardId is null || !cards.TryGetValue(entity.CardId, out var handCard) || handCard.Text.Contains("回合结束", StringComparison.Ordinal)))
            { unknowns.Add("我方手牌存在未确认的回合末效果，不能确定它与强制战斗的顺序。"); return Result(); }
            if (entity.Zone != "PLAY" && entity.Kind != EntityKind.Player && !attachedToLiveBoard) continue;
            if (entity.Kind == EntityKind.Player || entity.Kind == EntityKind.Game || ClaimsCharacter(entity, cards)) continue;
            if ((entity.Zone == "PLAY" || attachedToLiveBoard) && entity.Kind == EntityKind.Card)
            {
                if (Typed(entity, "HERO_POWER", "10") && Identity(entity) && !AssembledCardText.RequiresCustomText(entity.Tags) &&
                    entity.CardId is { } id && cards.TryGetValue(id, out var definition) && definition.Type == "HERO_POWER" &&
                    ActivePowers.TryGetValue(id, out var power) && definition.DbfId == power.Dbf && Mechanics(entity, cards) is { } mechanics &&
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.Type + "\n" + definition.Text + "\n" + string.Join("|", mechanics.Order(StringComparer.Ordinal))))) == power.Fingerprint) continue;
                if (KnownStatEnchantment(entity, entities, cards)) continue;
                if (KnownPlainWeapon(entity, cards)) continue;
                unknowns.Add("存在未审计的场上附魔、武器或其它监听来源，未假定它不改变交换。"); return Result();
            }
        }
        var activeMinions = characters.Where(entity => Typed(entity, "MINION", "4") && Health(entity) > 0 && Flag(entity, "DORMANT", "1518") != true).ToArray();
        if (activeMinions.Count(entity => entity.ControllerId == local) > 7 || activeMinions.Count(entity => entity.ControllerId == opponent) > MaximumAttackers ||
            activeMinions.Count(entity => entity.ControllerId == local && entity.CardId == "CORE_TTN_866" && Flag(entity, "SILENCED", "188") != true) != 1)
        { unknowns.Add("仅枚举一个活动恐魔与至多七个敌方随从，不猜测多个回合末来源顺序。"); return Result(); }
        var attackers = activeMinions.Where(entity => entity.ControllerId == opponent).Select(entity => entity.Id).Order().ToArray();
        var bodies = new Dictionary<int, Body>();
        foreach (var entity in characters)
        {
            if (Health(entity) is null || Number(entity, "HEALTH", "45") is not { } maximum || maximum <= 0 ||
                !Typed(entity, "HERO", "3") && Number(entity, "ATK", "47") is null or < 0 ||
                Typed(entity, "HERO", "3") && Number(entity, "ARMOR", "292") is null ||
                (entity.Tags.ContainsKey("ARMOR") || entity.Tags.ContainsKey("292")) && Number(entity, "ARMOR", "292") is not >= 0)
            { unknowns.Add("参与者当前生命/伤害、攻击或护甲不完整，未补默认伤害零或印刷属性。"); return Result(); }
            if (Typed(entity, "MINION", "4") && Health(entity) > 0 && Flag(entity, "DORMANT", "1518") != true &&
                !SafeText(entity, cards))
            { unknowns.Add("存在未审计的光环、受伤/攻击监听或原生文字，未先把普通交换当成完整前缀。"); return Result(); }
            if (Typed(entity, "MINION", "4") && Health(entity) <= 0 &&
                (Keyword(entity, cards, "DEATHRATTLE", "217") || Keyword(entity, cards, "REBORN", "1085")))
            { pending = true; unknowns.Add("当前仍在PLAY的致死随从已有待结算亡语或复生，不能跳过它开始回合末交换。"); return Result(); }
            if (entity.Id == terrorId && (Keyword(entity, cards, "IMMUNE", "240") || Keyword(entity, cards, "STEALTH", "191") || Keyword(entity, cards, "CANT_BE_ATTACKED", "245")) ||
                attackers.Contains(entity.Id) && Flag(entity, "UNTOUCHABLE", "448") == true)
            { unknowns.Add("强制攻击包含尚未核验的不可选中或不可交互组合。"); return Result(); }
            var isMinion = Typed(entity, "MINION", "4");
            bodies[entity.Id] = new(entity.Id, entity.ControllerId!.Value, isMinion ? "MINION" : "HERO", maximum, Health(entity)!.Value,
                Number(entity, "ARMOR", "292") ?? 0, Number(entity, "ATK", "47") ?? 0,
                Keyword(entity, cards, "DIVINE_SHIELD", "194"), Keyword(entity, cards, "IMMUNE", "240"), Keyword(entity, cards, "IMMUNE_WHILE_ATTACKING", "373"),
                Keyword(entity, cards, "POISONOUS", "363"), Keyword(entity, cards, "LIFESTEAL", "685"), Keyword(entity, cards, "CANT_BE_HEALED", "239"),
                Keyword(entity, cards, "DEATHRATTLE", "217") || Keyword(entity, cards, "REBORN", "1085"),
                entity.CardId == "FIR_953" && Flag(entity, "SILENCED", "188") == false,
                entity.CardId == "EDR_480" && Flag(entity, "SILENCED", "188") == false && Flag(entity, "DORMANT", "1518") == false, Beast(entity, cards));
        }
        if (bodies.Values.Any(body => body.Goldrinn) && bodies.Values.Any(body => body.Type == "MINION" && body.Beast is null))
        { unknowns.Add("当前种族标签冲突，不能确认戈德林是否放大伤害。"); return Result(); }
        assumptions.Add("枚举所有未排除的攻击先后条件，未主张客户端实际采用任意一种顺序、均匀随机或对应概率；同完整状态合并，Combats只保留一条见证路径。");
        assumptions.Add("强制攻击以原生效果可发动为条件；冻结、疲劳、普通攻击次数与入场回合字段保留最后观察值，不投影普通就绪状态，也不进入对手回合。");
        coverage.AddRange(["CORE_TTN_866-build255790-forced-end-turn-combat-schedules", "correlated-terminal-states-and-witness-paths", "simultaneous-combat-shield-armor-poison-lifesteal", "goldrinn-current-beast-damage", "no-authoritative-next-state-or-probability"]);
        Walk(new(bodies, attackers, [], 0));
        if (limit) unknowns.Add("有限状态或终局上限已达到；结果仅为已算相关前缀/终局，不提供完整全局范围。");
        return Result();

        void Walk(Node node)
        {
            if (limit) return;
            var key = Key(node);
            if (seen.Contains(key)) return;
            if (seen.Count >= MaximumStates) { limit = true; return; }
            seen.Add(key);
            if (!node.Bodies[ownHeroId].Alive || !node.Bodies[enemyHeroId].Alive) { Finish(node, true, "HeroDied"); return; }
            if (!node.Bodies[terrorId].Alive) { Finish(node, true, "TerrorDied"); return; }
            if (node.Remaining.Length == 0) { Finish(node, true, "AllEligibleAttackersResolved"); return; }
            foreach (var id in node.Remaining)
            {
                if (limit) break;
                var next = node.Bodies.ToDictionary(); var source = next[id]; var target = next[terrorId];
                var remaining = node.Remaining.Where(value => value != id).ToArray();
                if (!source.Alive) { Walk(node with { Remaining = remaining }); continue; }
                var damageToTerror = Damage(source, target, Amount(source, node.Bodies), false, out var damagedTerror);
                var damageToAttacker = Damage(target, source, Amount(target, node.Bodies), source.WhileAttackingImmune, out var damagedAttacker);
                next[terrorId] = damagedTerror; next[id] = damagedAttacker;
                long healing = 0;
                Heal(source, damageToTerror); Heal(target, damageToAttacker);
                var path = new List<TerrorForcedCombat>(node.Combats) { new(id, terrorId, damageToTerror, damageToAttacker, next[id].Alive, next[terrorId].Alive) };
                var state = new Node(next, remaining, path, checked(node.Healing + healing));
                if ((!next[id].Alive && source.Death) || (!next[terrorId].Alive && target.Death))
                { unknowns.Add("交换后的亡语或复生尚未展开，当前分支停在直接交换前缀，不伪造后续全局界。"); Finish(state, false, "DeathOrRebornRequiresObservation"); continue; }
                if (next[id].Alive && source.AfterAttack)
                { unknowns.Add("敌方熔岩猎犬存活后的分配伤害尚待结算，当前分支停在直接攻击交换。"); Finish(state, false, "AfterAttackRequiresObservation"); continue; }
                Walk(state);
                void Heal(Body dealer, long dealt)
                {
                    if (!dealer.Life || dealt == 0) return;
                    var heroId = dealer.Owner == local ? ownHeroId : enemyHeroId; var hero = next[heroId];
                    if (hero.CantHeal) return;
                    var after = Math.Min(hero.Maximum, checked(hero.Health + dealt));
                    healing = checked(healing + after - hero.Health); next[heroId] = hero with { Health = after };
                }
            }
        }
        void Finish(Node node, bool complete, string reason)
        {
            var key = reason + ":" + Key(node);
            if (!terminals.Add(key)) return;
            if (branches.Count >= MaximumBranches) { limit = true; return; }
            branches.Add(new("branch-" + (branches.Count + 1), complete, reason, Array.AsReadOnly(node.Combats.ToArray()),
                Array.AsReadOnly(node.Bodies.Values.OrderBy(body => body.Id).Select(body => new TerrorEntityResult(body.Id, body.Type, body.Health, body.Armor, body.Shield, body.Alive)).ToArray()),
                node.Bodies[ownHeroId].Health, node.Bodies[enemyHeroId].Health, node.Healing));
        }
        TerrorEndTurnOutcome Result()
        {
            var complete = !limit && branches.Count > 0 && branches.All(branch => branch.IsComplete);
            return new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, terrorId, active, complete, complete,
                Array.AsReadOnly(branches.ToArray()), active != false || !complete, pending, Freeze(unknowns), Freeze(assumptions), Freeze(coverage));
        }
    }

    private static long Damage(Body dealer, Body recipient, long amount, bool attackingImmune, out Body after)
    {
        after = recipient;
        if (amount <= 0 || recipient.Immune || attackingImmune) return 0;
        if (recipient.Shield) { after = recipient with { Shield = false }; return 0; }
        var absorbed = Math.Min(amount, recipient.Armor);
        after = recipient with { Armor = recipient.Armor - absorbed, Health = dealer.Poison && recipient.Type == "MINION" ? 0 : recipient.Health - (amount - absorbed) };
        return amount;
    }
    private static long Amount(Body source, IReadOnlyDictionary<int, Body> bodies) => checked(source.Attack *
        (source.Beast == true ? 1L << bodies.Values.Count(body => body.Owner == source.Owner && body.Goldrinn && body.Alive) : 1L));
    private static string Key(Node node) => node.Healing + ":" + string.Join(',', node.Remaining) + ":" +
        string.Join(';', node.Bodies.Values.OrderBy(body => body.Id).Select(body => $"{body.Id},{body.Health},{body.Armor},{body.Shield}"));
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
    private static bool Valid(EntitySnapshot? entity) => entity is not null && entity.Tags is not null;
    private static bool ClaimsCharacter(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        new[] { "CARDTYPE", "202" }.Any(key => entity.Tags.GetValueOrDefault(key) is "MINION" or "4" or "HERO" or "3") ||
        entity.CardId is { } id && cards.TryGetValue(id, out var card) && card.Type is "MINION" or "HERO";
    private static bool Typed(EntitySnapshot entity, string type, string number) => (entity.Tags.ContainsKey("CARDTYPE") || entity.Tags.ContainsKey("202")) &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var first) || first == type || first == number) && (!entity.Tags.TryGetValue("202", out var second) || second == type || second == number);
    private static bool Identity(EntitySnapshot entity) => entity.Id > 0 && entity.Kind == EntityKind.Card && entity.ControllerId is 1 or 2 &&
        (!entity.Tags.ContainsKey("50") || Number(entity, "CONTROLLER", "50") == entity.ControllerId) &&
        (!entity.Tags.ContainsKey("ENTITY_ID") && !entity.Tags.ContainsKey("53") || Number(entity, "ENTITY_ID", "53") == entity.Id) &&
        (!entity.Tags.TryGetValue("49", out var zone) || zone == entity.Zone || zone == (entity.Zone switch { "PLAY" => "1", "HAND" => "3", "GRAVEYARD" => "4", "REMOVEDFROMGAME" => "5", "SETASIDE" => "6", "SECRET" => "7", "DECK" => "2", _ => "?" }));
    private static bool RawConsistent(EntitySnapshot entity) => Flags.All(pair => Flag(entity, pair.Name, pair.Alias) is not null) && RaceConsistent(entity) &&
        new[] { ("ATK", "47"), ("HEALTH", "45"), ("DAMAGE", "44"), ("ARMOR", "292") }.All(pair =>
            !entity.Tags.ContainsKey(pair.Item1) && !entity.Tags.ContainsKey(pair.Item2) || Number(entity, pair.Item1, pair.Item2) is >= 0);
    private static bool HasModifier(EntitySnapshot entity) => Modifiers.Any(pair =>
        (entity.Tags.ContainsKey(pair.Name) || pair.Alias is not null && entity.Tags.ContainsKey(pair.Alias)) && Number(entity, pair.Name, pair.Alias) != 0) ||
        entity.Tags.Any(tag => (tag.Key.Contains("EXTRA_END_TURN", StringComparison.Ordinal) || tag.Key.Contains("EXTRA_TURN_END", StringComparison.Ordinal)) && Number(entity, tag.Key) != 0);
    private static bool KnownOwnSecret(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (!Identity(entity) || !Typed(entity, "SPELL", "5") || AssembledCardText.RequiresCustomText(entity.Tags) ||
            entity.CardId is not { } id || !cards.TryGetValue(id, out var card)) return false;
        if (id is "CORE_EX1_610" or "EX1_610") return ExplosiveTrapRules.IsAuditedSource(card);
        return id == "TLC_EVENT_400" && card.DbfId == 120648 && card.Type == "SPELL" &&
            card.Text == "<b>支线任务：</b>使用亡灵或野兽牌，总计3张。<b>奖励：</b>制造一个自定义的僵尸兽，其法力值消耗减少（3）点。" &&
            Mechanics(entity, cards)?.SetEquals(["SIDE_QUEST"]) == true;
    }
    private static bool KnownStatEnchantment(EntitySnapshot entity, IReadOnlyDictionary<int, EntitySnapshot> entities,
        IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (!Identity(entity) || !Typed(entity, "ENCHANTMENT", "6") || AssembledCardText.RequiresCustomText(entity.Tags) ||
            Number(entity, "ATTACHED", "40") is not { } attached || !entities.TryGetValue((int)attached, out var target) ||
            !Identity(target) || !Typed(target, "MINION", "4") || target.ControllerId != entity.ControllerId || target.Zone is not ("PLAY" or "HAND" or "DECK") ||
            entity.CardId is not { } id || !cards.TryGetValue(id, out var card) || card.Type != "ENCHANTMENT" || Mechanics(entity, cards)?.Count != 0) return false;
        return id switch
        {
            "DINO_421e" => card.DbfId == 118618 && card.Text == "+3/+3。" && Number(target, "ATK", "47") is not null && Number(target, "HEALTH", "45") is not null,
            "EDR_850pe1" => card.DbfId == 114619 && card.Text == "+0攻击力。" && Number(target, "ATK", "47") is not null,
            "EDR_850pe5" => card.DbfId == 114959 && card.Text == "法力值消耗减少。" && Number(target, "COST", "48") is not null,
            _ => false
        };
    }
    private static bool KnownPlainWeapon(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (!Identity(entity) || !Typed(entity, "WEAPON", "7") || !RawConsistent(entity) || AssembledCardText.RequiresCustomText(entity.Tags) ||
            entity.CardId is not ("CS2_106" or "CORE_CS2_106") || !cards.TryGetValue(entity.CardId, out var card) || card.Type != "WEAPON" ||
            card.DbfId != (entity.CardId == "CS2_106" ? 401 : 69534) || card.Text.Length != 0 || Mechanics(entity, cards)?.Count != 0 ||
            Number(entity, "ATK", "47") is not >= 0 || WeaponDurabilityEvidence.Resolve("255790", entity).Remaining is not > 0 ||
            new[] { ("DEATHRATTLE", "217"), ("AURA", "362"), ("TRIGGER_VISUAL", "32"), ("REBORN", "1085") }.Any(pair => Flag(entity, pair.Item1, pair.Item2) != false) ||
            new[] { "CANT_LOSE_DURABILITY", "DURABILITY_LOSS_DISABLED" }.Any(tag => entity.Tags.ContainsKey(tag) && Number(entity, tag) != 0)) return false;
        return true;
    }
    private static int? RaceValue(string? value) => value switch { "BEAST" => 20, "ALL" => 26, "DEMON" => 15, _ => int.TryParse(value, out var race) && race >= 0 ? race : null };
    private static bool RaceConsistent(EntitySnapshot entity)
    {
        var a = entity.Tags.TryGetValue("CARDRACE", out var first); var b = entity.Tags.TryGetValue("200", out var second);
        var x = a ? RaceValue(first) : null; var y = b ? RaceValue(second) : null;
        return (!a || x.HasValue) && (!b || y.HasValue) && (!a || !b || x == y);
    }
    private static long? Number(EntitySnapshot entity, string name, string? alias = null)
    {
        var a = entity.Tags.TryGetValue(name, out var first); var b = alias is not null && entity.Tags.TryGetValue(alias, out _);
        long? Parse(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= int.MinValue and <= int.MaxValue ? number : null;
        var x = a ? Parse(first) : null; var y = b ? Parse(entity.Tags[alias!]) : null;
        return a && x is null || b && y is null || a && b && x != y ? null : x ?? y;
    }
    private static bool? Flag(EntitySnapshot entity, string name, string? alias) =>
        !entity.Tags.ContainsKey(name) && (alias is null || !entity.Tags.ContainsKey(alias)) ? false : Number(entity, name, alias) switch { 0 => false, 1 => true, _ => null };
    private static long? Health(EntitySnapshot entity) => Number(entity, "HEALTH", "45") is >= 0 and var maximum && Number(entity, "DAMAGE", "44") is >= 0 and var damage ? maximum - damage : null;
    private static bool Keyword(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards, string keyword, string? alias)
    {
        if (entity.Tags.ContainsKey(keyword) || alias is not null && entity.Tags.ContainsKey(alias)) return Flag(entity, keyword, alias) == true;
        return Flag(entity, "SILENCED", "188") == false && Mechanics(entity, cards)?.Contains(keyword) == true;
    }
    private static bool? Beast(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var a = entity.Tags.TryGetValue("CARDRACE", out var first); var b = entity.Tags.TryGetValue("200", out var second);
        var x = a ? RaceValue(first) : null; var y = b ? RaceValue(second) : null;
        if (a && x is null || b && y is null || a && b && x != y) return null;
        // A positively observed Beast/All type is sufficient even if a silenced body's printed identity is unavailable.
        if ((x ?? y) is 20 or 26) return true;
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card) || Mechanics(entity, cards) is null) return null;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (a || b)
            {
                return id == "CORE_TTN_866" && AuditedTerror(card) && (x ?? y) == 15 && raw.GetProperty("race").GetString() == "DEMON" &&
                    raw.GetProperty("races").EnumerateArray().Select(value => value.GetString()).ToHashSet().SetEquals(["DEMON", "BEAST"]);
            }
            return raw.TryGetProperty("races", out var list) && list.EnumerateArray().Any(value => value.GetString() is "BEAST" or "ALL") ||
                raw.TryGetProperty("race", out var native) && native.GetString() is "BEAST" or "ALL";
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    private static HashSet<string>? Mechanics(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card) || card.Id != id) return null;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            if (raw.GetProperty("id").GetString() != id || raw.GetProperty("dbfId").GetInt32() != card.DbfId || raw.GetProperty("type").GetString() != card.Type ||
                (raw.TryGetProperty("text", out var text) ? text.GetString() : "") != card.Text || raw.EnumerateObject().Select(property => property.Name).Distinct().Count() != raw.EnumerateObject().Count()) return null;
            return raw.TryGetProperty("mechanics", out var list) ? list.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
    private static bool Audit(CardRecord? card, string id, int dbf, string text, string[] mechanics) =>
        card is { Type: "MINION" } && card.Id == id && card.DbfId == dbf && card.Text == text &&
        Mechanics(new(1, id, EntityKind.Card, FrozenDictionary<string, string>.Empty), new Dictionary<string, CardRecord> { [id] = card })?.SetEquals(mechanics) == true;
    private static bool AuditedTerror(CardRecord? card)
    {
        if (!Audit(card, "CORE_TTN_866", 129169, "<b>吸血</b>。在你的回合结束时，&迫使所有敌方随从攻击本随从。", ["LIFESTEAL", "TRIGGER_VISUAL"])) return false;
        try
        {
            using var document = JsonDocument.Parse(card!.RawJson); var raw = document.RootElement;
            return raw.GetProperty("race").GetString() == "DEMON" && raw.GetProperty("races").EnumerateArray().Select(value => value.GetString()).ToHashSet().SetEquals(["DEMON", "BEAST"]);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
    private static bool SafeText(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (Flag(entity, "SILENCED", "188") == true) return true;
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out var card)) return false;
        if (id == "CORE_TTN_866") return AuditedTerror(card);
        if (id == "EDR_480") return Audit(card, id, 114254, "<b>突袭</b>。友方野兽造成的伤害翻倍。", ["AURA", "RUSH"]);
        if (id == "FIR_953") return MagmaSplitRules.IsAuditedSource(card);
        if (id == "CORE_UNG_912") return Audit(card, id, 97376, "<b>战吼：</b>&随机将一张野兽牌置入你的手牌。", ["BATTLECRY"]);
        if (id == "EDR_849") return Audit(card, id, 114933, "在你使用一张随从牌后，随机使其获得一项<b>额外效果</b>。", ["TRIGGER_VISUAL"]);
        var battlecry = id switch
        {
            "EDR_800" => (114006, "<b><b>嘲讽</b>。圣盾</b>\n<b>战吼：</b><b>灌注</b>你的英雄技能。", new[] { "BATTLECRY", "DIVINE_SHIELD", "TAUNT" }),
            "EDR_852" => (114935, "<b>战吼：</b><b>灌注</b>你的英雄技能。", new[] { "BATTLECRY" }),
            "EDR_226" => (114018, "<b>战吼：</b>\n抽一张野兽牌。<b>灌注</b>你的英雄技能。", new[] { "BATTLECRY" }),
            "FIR_921" => (115648, "<b>战吼：</b>如果你已<b>灌注</b>过你的英雄技能两次，抽两张牌。", new[] { "BATTLECRY" }),
            "EDR_489" => (115203, "<b>战吼：</b>你使用的下一张牌会消耗<b>对手</b>的生命值而非法力值<i>（最高不超过10点）</i>。", new[] { "BATTLECRY" }),
            "EDR_888" => (115186, "<b>战吼：</b><b>发现</b>一张<b>传说</b>荒野之神。如果你已<b>灌注</b>过你的英雄技能4次，则将发现的荒野之神的法力值消耗变为（1）点。", new[] { "BATTLECRY", "DISCOVER" }),
            "BE_044" => (128892, "<b>战吼：</b><b>拼装</b>一张法力值消耗为（4）的随从牌。", new[] { "BATTLECRY" }),
            "JAIL_831" => (125998, "构筑套牌时，选择三张私藏的野兽牌。<b>战吼：</b>从中<b>发现</b>一张，其法力值消耗减少（3）点。", new[] { "BATTLECRY", "DISCOVER" }),
            "CORE_EDR_004" or "CORE_EDR_004_2026" => (id == "CORE_EDR_004" ? 119798 : 131356, "<b>回溯</b>。<b>战吼：</b><b>发现</b>一张具有<b>黑暗之赐</b>的野兽牌。<b>延系：</b>其法力值消耗减少（1）点。", new[] { "BATTLECRY", "DISCOVER" }),
            _ => (0, "", Array.Empty<string>())
        };
        if (battlecry.Item1 != 0) return Audit(card, id, battlecry.Item1, battlecry.Item2, battlecry.Item3);
        var mechanics = Mechanics(entity, cards); if (mechanics is null || mechanics.Any(value => !Passive.Contains(value) && value != "DEATHRATTLE")) return false;
        var text = card.Text; var death = text.IndexOf("<b>亡语", StringComparison.Ordinal);
        if (death >= 0 && mechanics.Contains("DEATHRATTLE")) text = text[..death];
        return Regex.Replace(text, "<[^>]+>|\\$|法术伤害\\s*\\+\\d+|Spell Damage\\s*\\+\\d+|[\\s&。；，,.!！:：]|嘲讽|圣盾|冲锋|突袭|风怒|吸血|剧毒|潜行|扰魔|复生|Taunt|Divine Shield|Charge|Rush|Windfury|Lifesteal|Poisonous|Stealth|Elusive|Reborn", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Length == 0;
    }
}
