namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    // Reuse the established current-value audit. This does not evaluate actions, reapply a gift,
    // or replace the observed frame; it only identifies passive attachments safe for this scope.
    internal static IReadOnlySet<int> AuditedStaticAttachmentsForDeathDraw(HsHelper.Core.GameSnapshot snapshot,
        IReadOnlyDictionary<string, HsHelper.Cards.CardRecord> cards) => new Runtime(snapshot, cards).AuditedStaticDrawAttachmentIds();

    private sealed partial class Runtime
    {
        private Entity[] _currentDeathBatch = [];

        public IReadOnlySet<int> AuditedStaticDrawAttachmentIds() => Entities.Values
            .Where(entity => TryObservedStaticDarkGift(entity, out _)).Select(entity => entity.Id).ToHashSet();

        private bool HasPotentialDeathDraw(Entity body) => body.Type == "MINION" && body.Controller == Local &&
            _snapshot.Entities.Values.Any(entity => entity.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId && entity.Zone != "GRAVEYARD" &&
                (entity.GetIntTag("ATTACHED") == body.Id || entity.GetIntTag("40") == body.Id));

        private bool TryObservedDeathDrawEnchantment(Entity enchantment)
        {
            if (enchantment.CardId != DrawResolutionRules.DeathrattleEnchantmentCardId ||
                !_snapshot.Entities.TryGetValue(enchantment.Id, out var original) ||
                !GiftInt(original, "ATTACHED", "40", out var attached) || attached is not > 0 ||
                !DrawResolutionRules.TryDeathrattleSource(_snapshot, attached.Value, _cards, out var observedBody, out var observedGift, out _) ||
                observedGift?.Id != enchantment.Id || !Entities.TryGetValue(attached.Value, out var body) ||
                body.CardId != observedBody.CardId || body.Controller != Local || body.Type != "MINION" ||
                !body.Alive || body.Health is not > 0 || body.Zone != "PLAY" || Silenced(body)) return false;
            return true;
        }

        private bool IsObservedDeathDrawNumericTag(Entity entity, string tag)
        {
            if (entity.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId)
                return tag is "40" or "49" or "50" or "53" or "188" or "202" or "330" or "331" or "1518" or "3567" &&
                    TryObservedDeathDrawEnchantment(entity);
            if (tag is not ("44" or "45" or "47" or "48" or "49" or "50" or "53" or "188" or "202" or "217" or "1518")) return false;
            return Entities.Values.Any(gift => gift.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId &&
                _snapshot.Entities.TryGetValue(gift.Id, out var original) &&
                GiftInt(original, "ATTACHED", "40", out var attached) && attached == entity.Id && TryObservedDeathDrawEnchantment(gift));
        }

        private bool RecordDeathrattleDraw(Entity body)
        {
            var anotherDeathPending = _currentDeathBatch.Any(other => other.Id != body.Id &&
                (Has(other, "DEATHRATTLE") || other.Int("217") is not (null or 0) || Has(other, "REBORN") || other.Int("1085") is not (null or 0) ||
                 HasPotentialDeathDraw(other)));
            var before = CaptureDirectDrawState() with
            {
                PriorResolutionPending = anotherDeathPending || _assumptions.Count > 0 || DrawBoundaryReached ||
                    GenerationBoundaryReached || DiscoveryBoundaryReached || RaptorBoundaryReached || MapFollowupBoundaryReached ||
                    MagmaSplitBoundaryReached || WolfBlessingBoundaryReached || WeaponResolutionBoundaryReached
            };
            var outcome = DrawResolutionRules.EvaluateDeathrattle(_snapshot, body.Id, _cards, before);
            _drawPreviews.Add(outcome);
            foreach (var coverage in outcome.RuleCoverage) Coverage.Add(coverage);
            DrawBoundaryReached = true;
            Conditional(outcome.ResolvesEffects
                ? "基础交换已计算；心中的恶魔亡语的直接抽牌、爆牌和疲劳见独立预览。实体与资源只投影到抽牌前，等待同一来源的新日志后再安排动作。"
                : "基础交换已计算，但死亡或抽牌来源、修正和顺序仍未确认；亡语后果保持未知，不能继续安排后续动作。",
                outcome.ResolvesEffects ? "DEATHRATTLE_DRAW_REQUIRES_OBSERVATION" : "UNRESOLVED_DEATHRATTLE_DRAW");
            return outcome.ResolvesEffects;
        }
    }
}
