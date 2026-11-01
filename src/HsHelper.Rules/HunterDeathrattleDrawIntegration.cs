using System.Collections.Frozen;
using HsHelper.Core;

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
        private readonly Dictionary<int, EntitySnapshot> _enteredDeathDrawSources = [];

        public IReadOnlySet<int> AuditedStaticDrawAttachmentIds() => Entities.Values
            .Where(entity => TryObservedStaticDarkGift(entity, out _) || IsAuditedDeathrattleAttachment(entity)).Select(entity => entity.Id).ToHashSet();

        private bool HasPotentialDeathDraw(Entity body) => body.Type == "MINION" && body.Controller == Local &&
            _snapshot.Entities.Values.Any(entity => entity.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId && entity.Zone != "GRAVEYARD" &&
                (entity.GetIntTag("ATTACHED") == body.Id || entity.GetIntTag("40") == body.Id));

        private bool TryObservedDeathDrawEnchantment(Entity enchantment)
        {
            if (enchantment.CardId != DrawResolutionRules.DeathrattleEnchantmentCardId ||
                !DeathrattleMultiplicityRules.TryAuditDrawAttachment(_snapshot, enchantment.Id, _cards, out var attached) ||
                !_snapshot.Entities.TryGetValue(attached, out var observedBody) ||
                DeathrattleMultiplicityRules.Evaluate(_snapshot, attached, _cards, enchantment.Id) is not { IsResolved: true } ||
                !Entities.TryGetValue(attached, out var body) || body.CardId != observedBody.CardId ||
                body.Controller != Local || body.Controller != observedBody.ControllerId || body.Type != "MINION" ||
                !body.Alive || body.Health is not > 0 || body.Zone is not ("HAND" or "PLAY")) return false;
            return true;
        }

        // Only the successful Summon call can establish this candidate source. The immutable
        // observed HAND frame remains unusable by the public direct-preview entry point.
        private void RememberEnteredDeathDrawSource(Entity source)
        {
            if (!_snapshot.Entities.TryGetValue(source.Id, out var original) || original.Zone != "HAND" ||
                source.CardId != original.CardId || source.Controller != Local || source.Controller != original.ControllerId ||
                source.Type != "MINION" || source.Zone != "PLAY" || !source.Alive || source.Health is not > 0 ||
                !RawNumber(original, "COST", "48", true, out var cost) || cost is null or < 0 ||
                !Entities.Values.Any(gift => TryObservedDeathDrawEnchantment(gift) &&
                    _snapshot.Entities.TryGetValue(gift.Id, out var raw) &&
                    GiftInt(raw, "ATTACHED", "40", out var attached) && attached == source.Id)) return;
            var frozen = FreezeForPlayTriggers(source);
            var candidate = DeathDrawSnapshot(frozen);
            if (DrawResolutionRules.TryDeathrattleSource(candidate, source.Id, _cards, out _, out _, out _))
                _enteredDeathDrawSources[source.Id] = frozen;
        }

        private GameSnapshot DeathDrawSnapshot(EntitySnapshot source) => _snapshot with
        {
            Entities = _snapshot.Entities.ToFrozenDictionary(pair => pair.Key,
                pair => pair.Key == source.Id ? source : pair.Value)
        };

        private bool IsObservedDeathDrawNumericTag(Entity entity, string tag)
        {
            if (entity.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId)
                return tag is "40" or "49" or "50" or "53" or "188" or "202" or "313" or "330" or "331" or "1518" or "3567" &&
                    TryObservedDeathDrawEnchantment(entity);
            if (tag is not ("44" or "45" or "47" or "48" or "49" or "50" or "53" or "188" or "202" or "217" or "1518")) return false;
            return Entities.Values.Any(gift => gift.CardId == DrawResolutionRules.DeathrattleEnchantmentCardId &&
                _snapshot.Entities.TryGetValue(gift.Id, out var original) &&
                GiftInt(original, "ATTACHED", "40", out var attached) && attached == entity.Id && TryObservedDeathDrawEnchantment(gift));
        }

        private bool RecordDeathrattleDraw(Entity body)
        {
            var anotherDeathPending = _currentDeathBatch.Any(other => other.Id != body.Id &&
                (Has(other, "REBORN") || HasPotentialDeathDraw(other) || DeathrattleMultiplicityRules.Evaluate(_snapshot, other.Id, _cards) is not { IsResolved: true, Activations: 0 } && Has(other, "DEATHRATTLE")));
            var before = CaptureDirectDrawState() with
            {
                PriorResolutionPending = anotherDeathPending || _assumptions.Count > 0 || DrawBoundaryReached ||
                    GenerationBoundaryReached || DiscoveryBoundaryReached || RaptorBoundaryReached || MapFollowupBoundaryReached ||
                    MagmaSplitBoundaryReached || WolfBlessingBoundaryReached || WeaponResolutionBoundaryReached
            };
            var drawSnapshot = _enteredDeathDrawSources.TryGetValue(body.Id, out var entered) &&
                entered.CardId == body.CardId && entered.ControllerId == body.Controller
                    ? DeathDrawSnapshot(entered) : _snapshot;
            var outcome = DrawResolutionRules.EvaluateDeathrattle(drawSnapshot, body.Id, _cards, before);
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
