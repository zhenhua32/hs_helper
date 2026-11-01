using HsHelper.Core;
using System.Globalization;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        private int? _initialImbues;
        private int? _currentImbues;
        private readonly List<DrawResolutionOutcome> _drawPreviews = [];
        private readonly HashSet<int> _imbueCounterEntities = [];
        public bool DrawBoundaryReached { get; private set; }

        private void InitializeImbueEvidence()
        {
            var evidence = ImbueEvidence.Evaluate(_snapshot);
            _initialImbues = _currentImbues = evidence.Count;
            if (evidence.Count.HasValue) foreach (var id in evidence.SourceEntityIds) _imbueCounterEntities.Add(id);
        }

        private bool IsKnownImbueCounter(Entity entity, string tag) => tag == ImbueEvidence.CounterTag &&
            (_imbueCounterEntities.Contains(entity.Id) || entity.Type == "PLAYER" && entity.Int("PLAYER_ID") is 1 or 2 &&
                _snapshot.Entities.TryGetValue(entity.Id, out var original) && original.GetIntTag(ImbueEvidence.CounterTag) is >= 0);

        private void AdvanceImbueEvidence(int? controller)
        {
            if (controller != Local || _snapshot.ClientBuild != AuditedBuild) return;
            _currentImbues = _currentImbues is >= 0 and < int.MaxValue and var count ? count + 1 : null;
            Coverage.Add("cumulative-imbue-advances-for-covered-native-trigger");
        }

        private DrawResolutionState CaptureDirectDrawState()
        {
            var originalDeck = _snapshot.Entities.Values.Where(entity => entity.ControllerId == Local && entity.Zone == "DECK").ToArray();
            var currentDeck = Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "DECK").ToArray();
            var sameDeck = originalDeck.Length == currentDeck.Length && originalDeck.All(observed =>
                currentDeck.Any(entity => entity.Id == observed.Id && entity.CardId == observed.CardId));
            var observedPlayer = Player is not null ? _snapshot.Entities.GetValueOrDefault(Player.Id) : null;
            int? ConsistentCount(string tag, string zone, int current)
            {
                if (observedPlayer?.Tags.TryGetValue(tag, out var raw) != true) return current;
                var original = _snapshot.Entities.Values.Count(entity => entity.ControllerId == Local && entity.Zone == zone);
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0 && count == original ? current : null;
            }
            int? Alias(string name, string numeric)
            {
                var first = observedPlayer?.Tags.GetValueOrDefault(name); var second = observedPlayer?.Tags.GetValueOrDefault(numeric);
                if (first is null && second is null) return null;
                if (first is not null && !int.TryParse(first, out _) || second is not null && !int.TryParse(second, out _) ||
                    first is not null && second is not null && int.Parse(first, CultureInfo.InvariantCulture) != int.Parse(second, CultureInfo.InvariantCulture)) return null;
                var value = int.Parse(first ?? second!, CultureInfo.InvariantCulture); return value >= 0 ? value : null;
            }
            var observedHero = OwnHero is not null ? _snapshot.Entities.GetValueOrDefault(OwnHero.Id) : null;
            var namedImmune = observedHero?.Tags.GetValueOrDefault("IMMUNE"); var numericImmune = observedHero?.Tags.GetValueOrDefault("240");
            bool? immune = OwnHero is not null && Has(OwnHero, "IMMUNE");
            if (namedImmune is not null || numericImmune is not null)
                immune = namedImmune is not (null or "0" or "1") || numericImmune is not (null or "0" or "1") ||
                    namedImmune is not null && numericImmune is not null && namedImmune != numericImmune ? null : (namedImmune ?? numericImmune) == "1";
            return new DrawResolutionState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                ConsistentCount("NUM_CARDS_IN_HAND", "HAND", Entities.Values.Count(entity => entity.Controller == Local && entity.Zone == "HAND")),
                Alias("MAXHANDSIZE", "28"), ConsistentCount("NUM_CARDS_IN_DECK", "DECK", currentDeck.Length), Alias("FATIGUE", "22"),
                observedHero?.GetIntTag("HEALTH") is not null && observedHero.GetIntTag("DAMAGE") is not null ? OwnHero?.Health : null,
                observedHero?.GetIntTag("ARMOR") is not null ? OwnHero?.Armor : null,
                immune, sameDeck);
        }

        public DrawResolutionOutcome RecordNativeDraw(Entity source)
        {
            var overlay = CaptureDirectDrawState();
            var condition = _currentImbues.HasValue ? _currentImbues.Value >= 2 : (bool?)null;
            var result = DrawResolutionRules.Evaluate(_snapshot, source.Id, _cards, condition, overlay);
            _drawPreviews.Add(result);
            foreach (var coverage in result.RuleCoverage) Coverage.Add(coverage);
            if (!result.ResolvesEffects)
            {
                Conditional("已确认入场，但抽牌来源或直接计数证据不足，数量保持未知。", "UNRESOLVED_DRAW_EVIDENCE");
                DrawBoundaryReached = true;
            }
            else if (result.RequestedDraws.Maximum != 0)
            {
                Conditional("抽牌数量、满手损失与直接疲劳见独立预览；实体和资源投影只到抽牌前，不生成新手牌实体，后续动作需要新局面。", "DRAW_REQUIRES_NEXT_OBSERVATION");
                DrawBoundaryReached = true;
            }
            else Coverage.Add("native-draw-condition-not-met-no-draw-boundary");
            return result;
        }

        public void DeferImbueAfterDraw()
        {
            _currentImbues = null;
            Conditional("奇异训犬师先抽取野兽、再灌注；抽牌及触发尚未观察，不提前提高技能或累计灌注次数。", "PENDING_AFTER_DRAW_IMBUE");
        }
    }
}
