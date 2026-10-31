using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record ExplosiveTrapDamageResult(int EntityId, string Type, long Damage, long HealthBefore,
    long HealthAfter, long ArmorBefore, long ArmorAfter, bool DivineShieldBefore, bool DivineShieldAfter, bool IsAliveAfter);

public sealed record ExplosiveTrapState
{
    public string MatchId { get; init; }
    public long StateVersion { get; init; }
    public long EventSequence { get; init; }
    public int SecretEntityId { get; init; }
    public int AttackerEntityId { get; init; }
    private IReadOnlyList<EntitySnapshot> _participants;
    public IReadOnlyList<EntitySnapshot> ParticipantsBeforeTrigger { get => _participants; init => _participants = Freeze(value); }
    public bool PriorResolutionPending { get; init; }
    public ExplosiveTrapState(string matchId, long stateVersion, long eventSequence, int secretEntityId, int attackerEntityId,
        IReadOnlyList<EntitySnapshot> participantsBeforeTrigger)
    {
        MatchId = matchId; StateVersion = stateVersion; EventSequence = eventSequence; SecretEntityId = secretEntityId;
        AttackerEntityId = attackerEntityId; _participants = Freeze(participantsBeforeTrigger);
    }
    private static IReadOnlyList<EntitySnapshot> Freeze(IReadOnlyList<EntitySnapshot> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Array.AsReadOnly(values.Select(entity => entity is null ? null! : entity with
            { Tags = entity.Tags?.ToFrozenDictionary(StringComparer.Ordinal)! }).ToArray());
    }
}

/// <summary>One hypothetical current incoming attack's secret damage, stopping before attack damage and unmodeled triggers.</summary>
public sealed record ExplosiveTrapOutcome(string MatchId, long StateVersion, long EventSequence, int SecretEntityId,
    string? SecretCardId, int AttackerEntityId, int? DefendingHeroEntityId, bool? TriggerActive, bool ResolvesDirectDamage,
    bool? TrapConsumed, long? SpellDamage, long? DamagePerEnemy, IReadOnlyList<ExplosiveTrapDamageResult> Targets,
    bool? AttackerSurvivesDirectDamage, bool? AttackStoppedByDirectDamage, bool RequiresObservation, bool PriorResolutionPending,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
    public bool? IsLegal => null;
    public bool IsConditionalPreview => true;
}

/// <summary>
/// Fixed 255790 CORE_EX1_610/69603 and EX1_610/585. This does not execute an opponent action.
/// Blizzard 1.0.0.4944: secrets can activate only on the opponent's turn:
/// https://news.blizzard.com/en-gb/article/13154924/hearthstone-patch-notes-1-0-0-4944-all-that-glitters .
/// Blizzard 21.6: temporary spell damage is spent when a secret is cast, not applied when it later triggers:
/// https://hearthstone.blizzard.com/en-us/news/23739023/21-6-patch-notes .
/// The all-enemy direct damage batch is isolated from attack continuation, damaged-state effects and death aftermath.
/// </summary>
public static class ExplosiveTrapRules
{
    // Fixed-255790 type + complete native text + sorted mechanics fingerprints, not card-ID-only exemptions.
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
    private const string Text = "<b>奥秘：</b>当你的英雄受到攻击，对所有敌人造成$2点伤害。";
    private static readonly HashSet<string> Passive = new(StringComparer.Ordinal)
        { "TAUNT", "DIVINE_SHIELD", "RUSH", "CHARGE", "WINDFURY", "MEGA_WINDFURY", "LIFESTEAL", "POISONOUS", "STEALTH", "ELUSIVE", "SPELLPOWER" };
    private static readonly (string, string?)[] DamageModifiers = [("HEALTH_MINIMUM", "337"), ("MINIMUM_HEALTH", null),
        ("DAMAGE_CANT_KILL", null), ("CANT_DAMAGE", "222"), ("CANT_BE_DESTROYED", "247"), ("HERO_CANT_BE_DESTROYED", "4439"),
        ("INCOMING_DAMAGE_MULTIPLIER", "236"), ("INCOMING_DAMAGE_ADJUSTMENT", "237"), ("INCOMING_DAMAGE_CAP", "238"),
        ("IGNORE_DAMAGE", "1"), ("OUTGOING_DAMAGE_MULTIPLIER", "275"), ("OUTGOING_DAMAGE_ADJUSTMENT", "274"),
        ("SPELLPOWER_DOUBLE", "356"), ("SPELL_HEALING_DOUBLE", "357"), ("SPELL_DAMAGE_DOUBLE", null),
        ("MULTIPLY_HERO_DAMAGE", "374"), ("LIFESTEAL_DAMAGES_OPPOSING_HERO", "1675"), ("HAUNTED_SECRET", "2634"),
        ("SECRET_LOCKED", "2676"), ("EXTRA_SECRET_TRIGGERS", null), ("SECRETS_TRIGGER_TWICE", null), ("SPELL_REFLECTION", null)];
    private static readonly (string, string)[] SchoolPower = [("CURRENT_SPELLPOWER_FIRE", "1937"), ("SPELLPOWER_FIRE", "1946"),
        ("CURRENT_NEGATIVE_SPELLPOWER", "651"), ("CURRENT_TEMP_SPELLPOWER_FIRE", "2252")];

    public static bool IsAuditedSource(CardRecord? card)
    {
        var dbf = card?.Id switch { "CORE_EX1_610" => 69603, "EX1_610" => 585, _ => 0 };
        if (dbf == 0 || card is null || card.DbfId != dbf || card.Type != "SPELL" || card.Text != Text) return false;
        try
        {
            using var document = JsonDocument.Parse(card.RawJson); var raw = document.RootElement;
            return NativeIdentity(card, raw) && raw.GetProperty("spellSchool").GetString() == "FIRE" &&
                !raw.TryGetProperty("race", out _) && !raw.TryGetProperty("races", out _) &&
                raw.GetProperty("mechanics").GetArrayLength() == 1 && raw.GetProperty("mechanics")[0].GetString() == "SECRET";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { return false; }
    }

    public static ExplosiveTrapOutcome Evaluate(GameSnapshot snapshot, int secretEntityId, int attackerEntityId,
        IReadOnlyDictionary<string, CardRecord> cards, ExplosiveTrapState? beforeTrigger = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(cards);
        var unknowns = new List<string>(); var assumptions = new List<string>(); var coverage = new List<string>();
        var targets = new List<ExplosiveTrapDamageResult>(); string? sourceId = null; int? defender = null;
        bool? active = null, consumed = null, survives = null, stopped = null; long? spellPower = null, damage = null;
        var resolves = false; var pending = beforeTrigger?.PriorResolutionPending == true;
        if (snapshot.ClientBuild != "255790" || ChoiceVisibility.ConfirmedLocalPlayer(snapshot) is not (1 or 2) ||
            !snapshot.Quality.HasGameStart || !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0 ||
            snapshot.Phase != GamePhase.Main || snapshot.ActivePlayerId is not (1 or 2) ||
            snapshot.Entities.Any(pair => !Valid(pair.Value) || pair.Key != pair.Value.Id) ||
            !snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).Select(entity => entity.GetIntTag("PLAYER_ID"))
                .Order().SequenceEqual(new int?[] { 1, 2 }))
        { unknowns.Add("需要固定 build 255790、完整稳定且已确认我方的非观战主要阶段，不能预览隐藏或未来攻击。"); return Result(); }
        var local = snapshot.LocalPlayerId!.Value; var enemy = local == 1 ? 2 : 1;
        var originalPlayers = snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player).ToArray();
        if (originalPlayers.Any(player => !TurnPlayerMetadata(player, snapshot.ActivePlayerId!.Value)))
        { unknowns.Add("玩家当前行动标记或其别名与当前回合字段冲突，不能把未知行动方当作对手回合。"); return Result(); }
        if (!snapshot.Entities.TryGetValue(secretEntityId, out var originalSecret) || originalSecret.Kind != EntityKind.Card ||
            originalSecret.ControllerId != local || originalSecret.Zone != "SECRET" || !Identity(originalSecret) ||
            !Typed(originalSecret, "SPELL", "5") || originalSecret.CardId is not { } secretId ||
            !cards.TryGetValue(secretId, out var card) || !IsAuditedSource(card) || AssembledCardText.RequiresCustomText(originalSecret.Tags) ||
            !SourceTags(originalSecret) || !snapshot.Entities.TryGetValue(attackerEntityId, out var originalAttacker) ||
            originalAttacker.Kind != EntityKind.Card || originalAttacker.ControllerId != enemy || originalAttacker.Zone != "PLAY" ||
            !Identity(originalAttacker) || (!Typed(originalAttacker, "MINION", "4") && !Typed(originalAttacker, "HERO", "3")))
        { unknowns.Add("需要当前我方原生爆炸陷阱奥秘与敌方公开场上攻击者，不能借用手牌、牌库、对方奥秘或冲突身份。"); return Result(); }
        sourceId = secretId;
        if (beforeTrigger is not null && (beforeTrigger.MatchId != snapshot.MatchId || beforeTrigger.StateVersion != snapshot.StateVersion ||
            beforeTrigger.EventSequence != snapshot.EventSequence || beforeTrigger.SecretEntityId != secretEntityId ||
            beforeTrigger.AttackerEntityId != attackerEntityId || beforeTrigger.ParticipantsBeforeTrigger.Any(entity => !Valid(entity)) ||
            beforeTrigger.ParticipantsBeforeTrigger.Select(entity => entity.Id).Distinct().Count() != beforeTrigger.ParticipantsBeforeTrigger.Count))
        { unknowns.Add("触发前覆盖的对局、版本、事件或实体集合未绑定，未采用旧/未来攻击意图。"); return Result(); }
        var participants = beforeTrigger?.ParticipantsBeforeTrigger ?? Array.AsReadOnly(snapshot.Entities.Values.Where(entity =>
            entity.Kind == EntityKind.Player || entity.Kind == EntityKind.Card &&
            (entity.Zone == "PLAY" ||
                entity.Zone == "SECRET" && entity.ControllerId == local)).ToArray());
        if (participants.Any(entity => !Valid(entity) || !snapshot.Entities.TryGetValue(entity.Id, out var original) ||
            entity.CardId != original.CardId || entity.Kind != original.Kind || entity.ControllerId != original.ControllerId || !RawConsistent(entity) || !RawConsistent(original) ||
            entity.Kind == EntityKind.Player && (Number(entity, "PLAYER_ID", "30") != Number(original, "PLAYER_ID", "30") ||
                !TurnPlayerMetadata(entity, snapshot.ActivePlayerId!.Value) ||
                Present(original, "CURRENT_PLAYER", "23") && Flag(entity, "CURRENT_PLAYER", "23") != Flag(original, "CURRENT_PLAYER", "23")) ||
            entity.Kind == EntityKind.Card && entity.Zone == "PLAY" && ClaimsCharacter(entity, cards) &&
                !Typed(entity, "MINION", "4") && !Typed(entity, "HERO", "3") ||
            entity.Kind == EntityKind.Card && (!Identity(entity) || entity.Zone is not ("PLAY" or "SECRET" or "GRAVEYARD" or "REMOVEDFROMGAME") ||
                entity.Zone == "SECRET" && entity.ControllerId != local)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" && ClaimsCharacter(entity, cards))
                .Any(entity => !participants.Any(current => current.Id == entity.Id)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.ControllerId == local && entity.Zone == "PLAY")
                .Any(entity => !participants.Any(current => current.Id == entity.Id)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" && Typed(entity, "WEAPON", "7"))
                .Any(entity => !participants.Any(current => current.Id == entity.Id)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY")
                .Any(entity => !participants.Any(current => current.Id == entity.Id)) ||
            snapshot.Entities.Values.Where(entity => entity.Kind == EntityKind.Player || entity.Kind == EntityKind.Card && entity.ControllerId == local && entity.Zone == "SECRET")
                .Any(entity => !participants.Any(current => current.Id == entity.Id)) ||
            participants.Select(entity => entity.Id).Distinct().Count() != participants.Count)
        { unknowns.Add("触发前角色集合缺少公开目标、包含新身份或私有区域，或原始/当前标签有反证，不能补齐群伤。"); return Result(); }
        var byId = participants.ToDictionary(entity => entity.Id);
        if (!byId.TryGetValue(secretEntityId, out var source) || source.Zone != "SECRET" || !SourceTags(source) ||
            AssembledCardText.RequiresCustomText(source.Tags) || !byId.TryGetValue(attackerEntityId, out var attacker) || attacker.Zone != "PLAY" ||
            Health(attacker) is not > 0 || Flag(attacker, "DORMANT", "1518") != false || Flag(attacker, "UNTOUCHABLE") != false)
        { unknowns.Add("本次奥秘与攻击者在触发前的活动区域或当前存活状态未明确，未重复触发已消耗来源。"); return Result(); }
        var ownHeroes = participants.Where(entity => entity.Zone == "PLAY" && entity.ControllerId == local && Typed(entity, "HERO", "3")).ToArray();
        if (ownHeroes.Length != 1 || Health(ownHeroes[0]) is not > 0)
        { unknowns.Add("当前受攻击的我方存活英雄未唯一确认，工具不接受模型另外指定防守目标。"); return Result(); }
        defender = ownHeroes[0].Id;
        assumptions.Add("仅假设给定敌方公开角色正在攻击我方英雄，不执行对手动作，不证明该攻击合法或必然发生，也不处理原攻击伤害。");
        if (snapshot.ActivePlayerId == local)
        {
            active = false; consumed = false; resolves = true; spellPower = null; damage = 0; survives = true; stopped = false;
            coverage.Add("secrets-do-not-activate-on-their-owners-current-turn");
            unknowns.Add("当前是我方回合，爆炸陷阱不会因本次假设攻击触发；未来对手回合须用新的实际局面重新预览。"); return Result();
        }
        if (pending)
        { unknowns.Add("之前的奥秘或攻击前结算仍待观察，不能将其后的攻击者、法强与目标集当作已确认。"); return Result(); }
        if (snapshot.Entities.Values.Any(entity => entity.Kind == EntityKind.Card && entity.ControllerId == enemy && entity.Zone == "SECRET" &&
            (Flag(entity, "SECRET", "219") != true || Flag(entity, "QUEST", "462") != false || Flag(entity, "SIDE_QUEST", "1192") != false)))
        { unknowns.Add("敌方该区域有任务或未分类占位，不能将其当成当前回合不会触发的普通奥秘；未读取或推定其隐藏身份。"); return Result(); }
        if (participants.Any(entity => entity.Id != source.Id && entity.ControllerId == local && entity.Zone == "SECRET" &&
            !KnownNonSecretQuest(entity, cards)))
        { unknowns.Add("还有我方奥秘或未分类占位，攻击前触发顺序与是否仍会到达本陷阱尚未确认，不按区域位置猜先后。"); return Result(); }
        if (participants.Any(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" && AssembledCardText.RequiresCustomText(entity.Tags)) ||
            participants.Any(entity => DamageModifiers.Any(pair => Present(entity, pair.Item1, pair.Item2) && Number(entity, pair.Item1, pair.Item2) != 0)) ||
            Flag(source, "POISONOUS", "363") != false || Flag(source, "LIFESTEAL", "685") != false)
        { unknowns.Add("存在自定义文字、额外伤害修正或未覆盖的法术剧毒/吸血，本次不套用未修正的两点伤害。"); return Result(); }
        if (participants.Any(entity => entity.Zone == "PLAY" &&
            !(Typed(entity, "WEAPON", "7") && NativeLongbow(entity, cards)) &&
            (!Typed(entity, "HERO", "3") || entity.CardId?.StartsWith("HERO_", StringComparison.Ordinal) != true) && !SafeOwnerContext(entity, cards)))
        { unknowns.Add("存在未审计的常驻、伤害替代或触发文字，不能把忽略这些效果的群伤数值当作确定的直接前缀。"); return Result(); }
        var longbowImmune = Typed(attacker, "HERO", "3") && participants.Any(entity => entity.Zone == "PLAY" && entity.ControllerId == enemy &&
            Typed(entity, "WEAPON", "7") && NativeLongbow(entity, cards));
        if (Typed(attacker, "HERO", "3") && participants.Any(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" &&
            !longbowImmune &&
            entity.ControllerId == enemy && (Flag(entity, "IMMUNE_WHILE_ATTACKING", "373") != false ||
                entity.CardId is { } id && cards.TryGetValue(id, out var definition) && Mechanics(definition).Contains("IMMUNE_WHILE_ATTACKING") ||
                Typed(entity, "WEAPON", "7") && UnsafeAfterDamage(entity, cards))))
        { unknowns.Add("来袭英雄或武器有攻击时免疫，尚未审计其与奥秘时点的具体交互，不按普通可受伤英雄处理。"); return Result(); }
        if (snapshot.Entities.Values.Any(entity => entity.Kind == EntityKind.Card && entity.ControllerId == local &&
            entity.Zone is not ("GRAVEYARD" or "REMOVEDFROMGAME") && Number(entity, "ATTACHED") is > 0 &&
            !participants.Any(current => current.Id == entity.Id)))
        { unknowns.Add("存在未纳入触发边界的我方附加效果，当前法强或原生结算次数不能静默当作未改变。"); return Result(); }
        if (!TrySpellPower(participants, local, cards, out var power))
        { unknowns.Add("触发时当前法强、学校专属加成或其来源存在缺失/冲突；不缓存安装时法强，也不把临时下一法术加成当作本次伤害。"); return Result(); }
        spellPower = power; damage = checked(2 + power);
        var enemies = participants.Where(entity => entity.Kind == EntityKind.Card && entity.Zone == "PLAY" && entity.ControllerId == enemy && ClaimsCharacter(entity, cards)).OrderBy(entity => entity.Id).ToArray();
        var uncertainAfterDamage = false;
        foreach (var target in enemies)
        {
            if (!Typed(target, "MINION", "4") && !Typed(target, "HERO", "3"))
            { unknowns.Add("敌方场上角色类别未确认，不能把它作为不存在的群伤目标。"); targets.Clear(); return Result(); }
            var hp = Health(target); var sleep = Flag(target, "DORMANT", "1518"); var untouchable = Flag(target, "UNTOUCHABLE");
            if (hp is <= 0 || sleep == true || untouchable == true) continue;
            var shield = Flag(target, "DIVINE_SHIELD", "194", Printed(target, cards, "DIVINE_SHIELD"));
            var immune = Flag(target, "IMMUNE", "240", Printed(target, cards, "IMMUNE"));
            if (target.Id == attacker.Id && longbowImmune) immune = true;
            var hero = Typed(target, "HERO", "3"); var armor = Number(target, "ARMOR", "292") ?? (hero ? null : 0);
            if (hp is null || sleep is null || untouchable is null || shield is null || immune is null || armor is null or < 0)
            { unknowns.Add("敌方当前生命、护甲、盾或免疫未可靠确认，不选择任一份冲突数值。"); targets.Clear(); return Result(); }
            var dealt = immune == true || shield == true ? 0 : damage.Value;
            var absorbed = Math.Min(armor.Value, dealt); var afterHealth = hp.Value - (dealt - absorbed);
            targets.Add(new(target.Id, hero ? "HERO" : "MINION", dealt, hp.Value, afterHealth, armor.Value, armor.Value - absorbed,
                shield.Value, immune == true && shield.Value, afterHealth > 0));
            if (dealt > 0 && (UnsafeAfterDamage(target, cards) || afterHealth <= 0 &&
                (Flag(target, "DEATHRATTLE", "217", Printed(target, cards, "DEATHRATTLE")) != false ||
                 Flag(target, "REBORN", "1085", Printed(target, cards, "REBORN")) != false))) uncertainAfterDamage = true;
        }
        if (targets.Count(target => target.Type == "HERO") != 1 || !targets.Any(target => target.EntityId == attacker.Id))
        { unknowns.Add("敌方英雄或攻击者不在本次完整当前角色集合中，未输出局部群伤冒充全体后果。"); targets.Clear(); return Result(); }
        active = true; consumed = true; resolves = true;
        survives = targets.Single(target => target.EntityId == attacker.Id).IsAliveAfter;
        stopped = uncertainAfterDamage ? null : survives == false || !targets.Single(target => target.Type == "HERO").IsAliveAfter;
        if (uncertainAfterDamage) unknowns.Add("以下仅是同一批直接群伤的前缀；受伤状态、触发、亡语或复生仍待结算，不创建新目标或据此继续原攻击。");
            else if (stopped == true) coverage.Add("direct-attacker-or-enemy-hero-death-stops-declared-attack");
        assumptions.Add("以本陷阱到达触发时点为条件，对触发前冻结的每个敌方角色各结算一个群伤包；不在途中对亡语新实体再次造成本批伤害。");
        assumptions.Add("只使用触发时当前全局法强或当前存活法强体证据；不重复相加全局与单体法强，不使用印刷费用、旧施放时状态或下一法术临时 token。");
        coverage.AddRange(["build255790-native-explosive-trap-two-aliases", "opponent-current-turn-only-conditional-incoming-hero-attack",
            "current-spell-damage-not-secret-installation-spell-damage", "one-all-enemy-direct-batch-before-attack-damage",
            "shield-absorbs-whole-batch-packet-immune-preserves-shield-and-armor", "no-opponent-action-or-authoritative-future-state"]);
        if (longbowImmune)
        {
            coverage.Add("native-equipped-hero-immune-while-attacking-prevents-explosive-trap-damage");
            assumptions.Add("角斗士的长弓抵御爆炸陷阱的时点有官方补丁直接确认；蜡烛弓依据固定版本相同完整免疫文字推导此时点，不声明已读取客户端脚本。");
        }
        return Result();

        ExplosiveTrapOutcome Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence, secretEntityId, sourceId,
            attackerEntityId, defender, active, resolves, consumed, spellPower, damage, Array.AsReadOnly(targets.ToArray()), survives, stopped,
            active != false, pending, Freeze(unknowns), Freeze(assumptions), Freeze(coverage));
    }

    private static bool TrySpellPower(IReadOnlyList<EntitySnapshot> participants, int local, IReadOnlyDictionary<string, CardRecord> cards, out long total)
    {
        total = 0; var players = participants.Where(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == local).ToArray();
        if (players.Length != 1 || !PlayerOwner(players[0], local) ||
            participants.Where(entity => entity.ControllerId == local || entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == local)
                .Any(entity => SchoolPower.Any(pair => Present(entity, pair.Item1, pair.Item2) && Number(entity, pair.Item1, pair.Item2) != 0))) return false;
        var player = players[0];
        long? aggregate = null;
        if (Present(player, "CURRENT_SPELLPOWER", "291"))
        { aggregate = Number(player, "CURRENT_SPELLPOWER", "291"); if (aggregate is null or < 0) return false; }
        foreach (var other in participants.Where(entity => entity.ControllerId == local && entity.Kind == EntityKind.Card && entity.Zone == "PLAY" &&
            !Typed(entity, "MINION", "4")))
            if (Present(other, "SPELLPOWER", "192") || other.CardId is { } id && cards.TryGetValue(id, out var card) &&
                (Mechanics(card).Contains("SPELLPOWER") || Mechanics(card).Contains("AURA"))) return false;
        foreach (var minion in participants.Where(entity => entity.ControllerId == local && entity.Zone == "PLAY" && Typed(entity, "MINION", "4")))
        {
            var life = Health(minion); var dormant = Flag(minion, "DORMANT", "1518"); var silence = Flag(minion, "SILENCED", "188");
            if (life is <= 0 || dormant == true) continue;
            if (life is null || dormant is null || silence is null) return false;
            if (Present(minion, "SPELLPOWER", "192"))
            { var observed = Number(minion, "SPELLPOWER", "192"); if (observed is null or < 0) return false; total += observed.Value; continue; }
            if (silence == true) continue;
            if (!Definition(minion, cards, out var card, out var raw)) return false;
            using (raw)
            {
                if (raw.RootElement.TryGetProperty("spellDamage", out var value))
                { if (!value.TryGetInt32(out var count) || count < 0) return false; total += count; }
                else if (Mechanics(card).Contains("SPELLPOWER")) return false;
                else if (Mechanics(card).Contains("AURA") && card.Id != "EDR_480") return false;
            }
        }
        if (aggregate.HasValue) { if (aggregate.Value < total) return false; total = aggregate.Value; }
        return true;
    }
    private static bool Valid(EntitySnapshot? entity) => entity is not null && entity.Tags is not null;
    private static bool PlayerOwner(EntitySnapshot player, int local) => !Present(player, "CONTROLLER", "50") || Number(player, "CONTROLLER", "50") == local;
    private static bool TurnPlayerMetadata(EntitySnapshot player, int active) => Number(player, "PLAYER_ID", "30") is 1 or 2 &&
        PlayerOwner(player, (int)Number(player, "PLAYER_ID", "30")!.Value) &&
        (!Present(player, "CURRENT_PLAYER", "23") || Flag(player, "CURRENT_PLAYER", "23") == (Number(player, "PLAYER_ID", "30") == active));
    private static bool Typed(EntitySnapshot entity, string type, string code) => Present(entity, "CARDTYPE", "202") &&
        (!entity.Tags.TryGetValue("CARDTYPE", out var named) || named == type || named == code) && (!entity.Tags.TryGetValue("202", out var numeric) || numeric == type || numeric == code);
    private static bool ClaimsCharacter(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards) =>
        new[] { "CARDTYPE", "202" }.Any(key => entity.Tags.GetValueOrDefault(key) is "MINION" or "4" or "HERO" or "3") ||
        entity.CardId is { } id && cards.TryGetValue(id, out var card) && card.Type is "MINION" or "HERO";
    private static bool Identity(EntitySnapshot entity) => entity.Id > 0 && entity.ControllerId is 1 or 2 &&
        (!entity.Tags.ContainsKey("50") || Number(entity, "CONTROLLER", "50") == entity.ControllerId) &&
        (!Present(entity, "ENTITY_ID", "53") || Number(entity, "ENTITY_ID", "53") == entity.Id) &&
        (!entity.Tags.TryGetValue("49", out var zone) || zone == entity.Zone || zone == (entity.Zone switch
            { "PLAY" => "1", "GRAVEYARD" => "4", "REMOVEDFROMGAME" => "5", "SECRET" => "7", _ => "?" }));
    private static bool SourceTags(EntitySnapshot entity) => Typed(entity, "SPELL", "5") &&
        (!Present(entity, "SECRET", "219") || Flag(entity, "SECRET", "219") == true) &&
        (!entity.Tags.TryGetValue("CARDRACE", out var race) || race is "INVALID" or "0") &&
        (!entity.Tags.TryGetValue("200", out var raceAlias) || raceAlias is "INVALID" or "0") &&
        (!entity.Tags.TryGetValue("SPELL_SCHOOL", out var school) || school is "FIRE" or "2") &&
        (!entity.Tags.TryGetValue("1635", out var schoolAlias) || schoolAlias is "FIRE" or "2");
    private static bool RawConsistent(EntitySnapshot entity) =>
        new[] { ("SILENCED", "188"), ("DORMANT", "1518"), ("IMMUNE", "240"), ("DIVINE_SHIELD", "194"), ("SECRET", "219"),
            ("LIFESTEAL", "685"), ("POISONOUS", "363"), ("DEATHRATTLE", "217"), ("REBORN", "1085") }
            .All(pair => Flag(entity, pair.Item1, pair.Item2) is not null) &&
        new[] { ("HEALTH", "45"), ("DAMAGE", "44"), ("ARMOR", "292"), ("SPELLPOWER", "192"), ("CURRENT_SPELLPOWER", "291") }
            .All(pair => !Present(entity, pair.Item1, pair.Item2) || Number(entity, pair.Item1, pair.Item2) is not null);
    private static bool Present(EntitySnapshot entity, string name, string? alias = null) => entity.Tags.ContainsKey(name) || alias is not null && entity.Tags.ContainsKey(alias);
    private static long? Number(EntitySnapshot entity, string name, string? alias = null)
    {
        var a = entity.Tags.TryGetValue(name, out var first); var b = alias is not null && entity.Tags.TryGetValue(alias, out _);
        long? Parse(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var x = a ? Parse(first) : null; var y = b ? Parse(entity.Tags[alias!]) : null;
        return a && x is null || b && y is null || a && b && x != y ? null : x ?? y;
    }
    private static long? Health(EntitySnapshot entity) => Number(entity, "HEALTH", "45") is >= 0 and var maximum && Number(entity, "DAMAGE", "44") is >= 0 and var damage ? maximum - damage : null;
    private static bool? Flag(EntitySnapshot entity, string name, string? alias = null, bool fallback = false) =>
        !Present(entity, name, alias) ? fallback : Number(entity, name, alias) switch { 0 => false, 1 => true, _ => null };
    private static bool Printed(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards, string mechanic) =>
        Flag(entity, "SILENCED", "188") == false && entity.CardId is { } id && cards.TryGetValue(id, out var card) && Mechanics(card).Contains(mechanic);
    private static HashSet<string> Mechanics(CardRecord card)
    {
        try { using var document = JsonDocument.Parse(card.RawJson); return document.RootElement.TryGetProperty("mechanics", out var list) ? list.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : []; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { return new(StringComparer.Ordinal) { "UNKNOWN" }; }
    }
    private static bool Definition(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards, out CardRecord card, out JsonDocument raw)
    {
        card = null!; raw = null!;
        if (entity.CardId is not { } id || !cards.TryGetValue(id, out card!) || card is null) return false;
        try { raw = JsonDocument.Parse(card.RawJson); if (NativeIdentity(card, raw.RootElement)) return true; raw.Dispose(); return false; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { raw?.Dispose(); return false; }
    }
    private static bool NativeIdentity(CardRecord card, JsonElement raw) => raw.ValueKind == JsonValueKind.Object &&
        raw.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == raw.EnumerateObject().Count() &&
        raw.GetProperty("id").GetString() == card.Id && raw.GetProperty("dbfId").GetInt32() == card.DbfId && raw.GetProperty("type").GetString() == card.Type &&
        (raw.TryGetProperty("text", out var text) ? text.GetString() : "") == card.Text;
    private static bool KnownNonSecretQuest(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (entity.CardId != "TLC_EVENT_400" || !Typed(entity, "SPELL", "5") || AssembledCardText.RequiresCustomText(entity.Tags) ||
            Flag(entity, "SECRET", "219") != false || !Definition(entity, cards, out var card, out var raw)) return false;
        using (raw) return card.DbfId == 120648 && card.Type == "SPELL" &&
            card.Text == "<b>支线任务：</b>使用亡灵或野兽牌，总计3张。<b>奖励：</b>制造一个自定义的僵尸兽，其法力值消耗减少（3）点。" &&
            Mechanics(card).SetEquals(["SIDE_QUEST"]);
    }
    private static bool NativeLongbow(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        var expected = entity.CardId switch { "DS1_188" => 311, "CORE_LOOT_222" => 86543, "LOOT_222" => 45537, _ => 0 };
        if (expected == 0 || !Typed(entity, "WEAPON", "7") || !Identity(entity) ||
            AssembledCardText.RequiresCustomText(entity.Tags) || Health(entity) is not > 0 ||
            Flag(entity, "SILENCED", "188") != false || Flag(entity, "DORMANT", "1518") != false ||
            !Definition(entity, cards, out var card, out var raw)) return false;
        using (raw) return card.DbfId == expected && card.Type == "WEAPON" && card.Text == "你的英雄在攻击时<b>免疫</b>。" && Mechanics(card).Count == 0;
    }
    private static bool SafeOwnerContext(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (Flag(entity, "SILENCED", "188") == true || Flag(entity, "DORMANT", "1518") == true) return true;
        if (!Definition(entity, cards, out var card, out var raw)) return false;
        using (raw)
        {
            if (Typed(entity, "HERO_POWER", "10") && card.Type == "HERO_POWER" && ActivePowers.TryGetValue(card.Id, out var power) &&
                card.DbfId == power.Dbf && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(card.Type + "\n" + card.Text + "\n" +
                    string.Join("|", Mechanics(card).Order(StringComparer.Ordinal))))) == power.Fingerprint) return true;
            if (card.Id == "EDR_480" && card.DbfId == 114254 && card.Text == "<b>突袭</b>。友方野兽造成的伤害翻倍。" && Mechanics(card).SetEquals(["AURA", "RUSH"])) return true;
            if (card.Id == "FIR_953" && MagmaSplitRules.IsAuditedSource(card)) return true;
            if (card.Id == "EDR_849" && card.DbfId == 114933 && card.Text == "在你使用一张随从牌后，随机使其获得一项<b>额外效果</b>。" && Mechanics(card).SetEquals(["TRIGGER_VISUAL"])) return true;
            return !UnsafeAfterDamage(entity, cards);
        }
    }
    private static bool UnsafeAfterDamage(EntitySnapshot entity, IReadOnlyDictionary<string, CardRecord> cards)
    {
        if (Flag(entity, "SILENCED", "188") == true || Typed(entity, "HERO", "3") && entity.CardId?.StartsWith("HERO_", StringComparison.Ordinal) == true) return false;
        if (!Definition(entity, cards, out var card, out var raw)) return true;
        using (raw)
        {
            if (card.Id == "CORE_UNG_912" && card.DbfId == 97376 && card.Text == "<b>战吼：</b>&随机将一张野兽牌置入你的手牌。" && Mechanics(card).SetEquals(["BATTLECRY"])) return false;
            if (card.Id == "EDR_227" && card.DbfId == 114020 && card.Text == "<b>突袭</b>。<b>亡语：</b><b>灌注</b>你的英雄技能。" && Mechanics(card).SetEquals(["RUSH", "DEATHRATTLE"])) return false;
            if (Mechanics(card).Any(mechanic => !Passive.Contains(mechanic) && mechanic is not ("DEATHRATTLE" or "REBORN"))) return true;
            return Regex.Replace(card.Text ?? "", "<[^>]+>|法术伤害\\s*\\+\\d+|Spell Damage\\s*\\+\\d+|[\\s&。；，,.!！:：]|嘲讽|圣盾|冲锋|突袭|风怒|吸血|剧毒|潜行|扰魔|复生|Taunt|Divine Shield|Charge|Rush|Windfury|Lifesteal|Poisonous|Stealth|Elusive|Reborn", "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Length > 0;
        }
    }
    private static IReadOnlyList<string> Freeze(IEnumerable<string> values) => Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).ToArray());
}
