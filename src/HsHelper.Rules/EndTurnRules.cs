using System.Collections.Frozen;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // Pinned card text/stats: https://api.hearthstonejson.com/v1/255790/zhCN/cards.json
    // Publisher card: https://hearthstone.blizzard.com/en-us/cards/129169
    // Reference only, not a build-255790 client guarantee:
    // https://github.com/jleclanche/fireplace/blob/master/fireplace/actions.py (Attack / EndTurn).
    private static bool TryPlayMythicalTerror(Runtime state, PlannedAction action, Entity source, Profile profile)
    {
        if (source.CardId != "CORE_TTN_866") return false;
        if (!state.RequireAuditedBuild()) return true;
        if (!state.IsAuditedTerrorSource(source))
        { state.Unsupported("UNAUDITED_MYTHICAL_TERROR", "神秘恐魔的固定身份、完整文字或机制不一致，未支付或入场。"); return true; }
        if (action.TargetEntityId is not null) { state.Invalid("神秘恐魔入场不需要指定目标。"); return true; }
        var extra = source.Flags.Where(flag => flag.Value > 0 &&
            flag.Key is "BATTLECRY" or "DEATHRATTLE" or "AURA").ToArray();
        if (!state.Summon(source, profile) || state.OpponentDefeatedByPayment) return true;
        foreach (var flag in extra) source.Flags[flag.Key] = flag.Value;
        if (extra.Length > 0)
            state.Conditional("神秘恐魔带有额外战吼、亡语或光环，结果仅展开已审计的原生效果。", "UNKNOWN_MYTHICAL_TERROR_EXTRA_EFFECT");
        state.Coverage.Add("audited-minion-summon:CORE_TTN_866");
        return true;
    }

    private static void EndTurn(Runtime state, PlannedAction action)
    {
        if (state.Player is null || action.SourceEntityId != state.Player.Id)
        { state.Invalid("结束回合的来源必须是当前我方玩家实体。"); return; }
        if (action.TargetEntityId is not null) { state.Invalid("结束回合不能指定目标。"); return; }
        state.ResolveOwnEndTurn();
    }

    private sealed partial class Runtime
    {
        public bool TurnEnded { get; private set; }
        public bool EndTurnTriggersResolved { get; private set; }
        private readonly List<ForcedAttackResult> _forcedAttacks = [];
        private readonly List<PendingForcedAttack> _pendingForcedAttacks = [];
        private readonly List<TerrorEndTurnOutcome> _terrorEndTurnPreviews = [];
        private readonly HashSet<string> _unresolvedEndTurnFields = new(StringComparer.Ordinal);
        public bool IsAuditedTerrorSource(Entity source) => source.CardId is { } id &&
            TerrorEndTurnResolutionRules.IsAuditedSource(_cards.GetValueOrDefault(id));

        public void ResolveOwnEndTurn()
        {
            TurnEnded = true;
            Coverage.Add("own-end-turn-boundary-before-opponent-start");
            foreach (var entity in Entities.Values.Where(e => e.Alive && e.Zone is "PLAY" or "HAND"))
            {
                var profile = Profile(entity);
                if (entity.CardId == "CORE_TTN_866" || profile is null || Silenced(entity) || Has(entity, "DORMANT")) continue;
                if (profile.Text.Contains("回合结束", StringComparison.Ordinal) || profile.Text.Contains("end of", StringComparison.OrdinalIgnoreCase))
                {
                    Conditional("存在尚未模拟的回合末效果，不能确认它与强制战斗的先后关系。", "UNMAPPED_END_TURN_EFFECT");
                    _unresolvedEndTurnFields.Add($"entity:{entity.Id}:END_TURN_EFFECT");
                }
            }
            if (Entities.Values.Any(e => e.Alive && e.Flags.Any(flag => flag.Value > 0 &&
                (flag.Key.Contains("EXTRA_END_TURN", StringComparison.Ordinal) || flag.Key.Contains("EXTRA_TURN_END", StringComparison.Ordinal)))))
                Conditional("存在额外回合末触发次数修正，当前未展开重复触发。", "END_TURN_TRIGGER_MULTIPLIER");

            var triggers = Entities.Values.Where(e => e.Alive && e.Zone == "PLAY" && e.Controller == Local &&
                e.CardId == "CORE_TTN_866" && !HasCustomAssembledText(e) && !Silenced(e) && !Has(e, "DORMANT")).ToArray();
            foreach (var terror in triggers)
            {
                var attackers = Entities.Values.Where(e => e.Alive && e.Zone == "PLAY" && e.Type == "MINION" &&
                    e.Controller != Local && !Has(e, "DORMANT")).ToArray();
                if (_snapshot.ClientBuild != AuditedBuild || Profile(terror) is null)
                { Pending(terror, attackers, "当前卡库版本或神秘恐魔牌效资料未确认。", "UNAUDITED_MYTHICAL_TERROR_END_TURN"); continue; }
                var otherPlayers = Entities.Values.Where(e => e.Type == "PLAYER" && e.Int("PLAYER_ID") != Local).Take(2).ToArray();
                var opponent = EnemyHero?.Controller;
                if (opponent is null || opponent == Local || otherPlayers.Length != 1 || otherPlayers[0].Int("PLAYER_ID") != opponent ||
                    attackers.Any(e => e.Controller != opponent))
                { Pending(terror, attackers, "对手玩家或场上随从归属未唯一确认，无法确定强制攻击候选集合。", "UNKNOWN_FORCED_COMBAT_OWNERSHIP"); continue; }
                if (attackers.Length == 0) { Coverage.Add("CORE_TTN_866-no-enemy-minions"); continue; }
                if (triggers.Length != 1 || attackers.Length != 1)
                {
                    if (_terrorEndTurnPreviews.Count == 0)
                    {
                        var overlay = new TerrorEndTurnState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                            terror.Id, Array.AsReadOnly(Entities.Values.OrderBy(entity => entity.Id).Select(FreezeTerrorEndTurn).ToArray()))
                        {
                            PriorResolutionPending = HasUnresolvedNativeResolution || WeaponResolutionBoundaryReached ||
                                _unsupported.Overlaps(["UNMAPPED_END_TURN_EFFECT", "END_TURN_TRIGGER_MULTIPLIER", "UNKNOWN_BOARD_CARD"])
                        };
                        var preview = TerrorEndTurnResolutionRules.Evaluate(_snapshot, terror.Id, _cards, overlay);
                        _terrorEndTurnPreviews.Add(preview);
                        Coverage.UnionWith(preview.RuleCoverage);
                    }
                    Pending(terror, attackers, "多个恐魔触发或敌方攻击者的实际顺序尚未观察；有限分支单列比较，不作为已发生状态。", "UNCONFIRMED_FORCED_ATTACK_ORDER");
                    continue;
                }
                var source = attackers[0];
                if (source.Controller is null || source.Attack is null or < 0 || source.Health is null ||
                    terror.Attack is null or < 0 || terror.Health is null || Profile(source) is null)
                { Pending(terror, attackers, "攻击者归属、当前属性或牌效未确认，不能用印刷属性代替。", "UNKNOWN_FORCED_COMBAT_VALUES"); continue; }
                if (Has(source, "UNTOUCHABLE") || Has(terror, "UNTOUCHABLE") || Has(terror, "IMMUNE") || Has(terror, "STEALTH") || Has(terror, "CANT_BE_ATTACKED"))
                { Pending(terror, attackers, "强制战斗含不可交互或特殊防御限制，尚未核验该组合。", "UNCONFIRMED_FORCED_ATTACK_TARGET"); continue; }

                // Keep the last observed normal attack counters. The reference simulator distinguishes
                // triggered combat from a normal player attack, but that is not evidence for this client.
                source.Flags["RULES_FORCED_ATTACK_READINESS_NOT_PROJECTED"] = 1;
                foreach (var tag in new[] { "NUM_ATTACKS_THIS_TURN", "EXHAUSTED", "FROZEN", "NUM_TURNS_IN_PLAY" })
                    _unresolvedEndTurnFields.Add($"entity:{source.Id}:{tag}");
                Conditional("已展开唯一强制战斗；普通攻击计数、疲劳、冻结及入场回合字段保留最后观察值，未声称它们是回合末结算后的精确状态。", "FORCED_ATTACK_READINESS_UNCONFIRMED");
                if (Has(source, "FROZEN") || Has(source, "EXHAUSTED") || Has(source, "CANT_ATTACK") ||
                    Has(source, "CANT_ATTACK_MINIONS") || Has(source, "STEALTH") || source.Attack == 0)
                    Conditional("本次交换以强制效果可绕过手动攻击限制为条件；固定版本的特殊组合仍需客户端实证。", "FORCED_ATTACK_SPECIAL_READINESS");
                if (!Has(terror, "TAUNT") && Entities.Values.Any(e => e.Alive && e.Zone == "PLAY" && e.Controller == Local &&
                    e.Type == "MINION" && e.Id != terror.Id && Has(e, "TAUNT") && !Has(e, "DORMANT")))
                    Conditional("本次交换以神秘恐魔强制目标可绕过其他友方嘲讽为条件。", "FORCED_ATTACK_TAUNT_BYPASS");
                source.Flags["STEALTH"] = 0;
                var attack = source.Attack.Value;
                var retaliation = terror.Attack.Value;
                var outgoing = Damage(source, terror, attack, true);
                var incoming = CombatHas(source, "IMMUNE_WHILE_ATTACKING") ? 0 : Damage(terror, source, retaliation, true);
                HealLifesteal(source, outgoing); HealLifesteal(terror, incoming);
                ResolveDeaths();
                ResolveMagmaHoundAfterAttack(source, terror);
                _forcedAttacks.Add(new(terror.Id, source.Id, terror.Id, outgoing, incoming, source.Alive, terror.Alive));
                Coverage.UnionWith(["CORE_TTN_866-single-enemy-forced-combat", "simultaneous-combat", "lifesteal", "armor", "divine-shield", "poisonous"]);
            }
            EndTurnTriggersResolved = _pendingForcedAttacks.Count == 0 && _assumptions.Count == 0 && !Stopped;
        }

        private void Pending(Entity terror, IReadOnlyList<Entity> attackers, string reason, string kind)
        {
            Conditional(reason, kind);
            _pendingForcedAttacks.Add(new(terror.Id, terror.Id,
                Array.AsReadOnly(attackers.Select(e => e.Id).Order().ToArray()), reason));
        }

        private HsHelper.Core.EntitySnapshot FreezeTerrorEndTurn(Entity entity)
        {
            var frozen = FreezeForPlayTriggers(entity);
            if (_snapshot.Entities[entity.Id].Zone is null && string.IsNullOrEmpty(entity.Zone))
                frozen = frozen with { Tags = frozen.Tags.Where(pair => pair.Key is not ("ZONE" or "49")).ToFrozenDictionary() };
            return frozen;
        }
    }
}
