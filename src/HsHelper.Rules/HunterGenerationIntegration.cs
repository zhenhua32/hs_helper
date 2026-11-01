using System.Globalization;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        private readonly List<GenerationResolutionOutcome> _generationPreviews = [];
        public bool GenerationBoundaryReached { get; private set; }

        public GenerationResolutionOutcome RecordNativeGeneration(Entity source)
        {
            var observedPlayer = Player is not null ? _snapshot.Entities.GetValueOrDefault(Player.Id) : null;
            var originalHandCount = _snapshot.Entities.Values.Count(entity => entity.ControllerId == Local && entity.Zone == "HAND");
            int? handCount = Entities.Values.Count(entity => entity.Controller == Local && entity.Zone == "HAND");
            if (observedPlayer?.Tags.GetValueOrDefault("NUM_CARDS_IN_HAND") is { } rawCount &&
                (!int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != originalHandCount))
                handCount = null;
            var named = observedPlayer?.Tags.GetValueOrDefault("MAXHANDSIZE");
            var numeric = observedPlayer?.Tags.GetValueOrDefault("28");
            int? maximum = null;
            if ((named is null || int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) &&
                (numeric is null || int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) &&
                (named is null || numeric is null || int.Parse(named, CultureInfo.InvariantCulture) == int.Parse(numeric, CultureInfo.InvariantCulture)) &&
                int.TryParse(named ?? numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit >= 0)
                maximum = limit;
            var before = new GenerationResolutionState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, handCount, maximum);
            var result = GenerationResolutionRules.Evaluate(_snapshot, source.Id, _cards, before);
            _generationPreviews.Add(result);
            foreach (var coverage in result.RuleCoverage) Coverage.Add(coverage);
            if (!result.ResolvesEffects || result.RequestedCards.Maximum != 0)
            {
                GenerationBoundaryReached = true;
                Conditional("随机生成的直接数量和满手损失见独立预览；未创建新手牌实体，生成及后续触发需要新局面后继续。", "GENERATION_REQUIRES_NEXT_OBSERVATION");
            }
            return result;
        }
    }
}
