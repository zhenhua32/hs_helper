using System.Collections.Frozen;
using System.Globalization;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        private readonly List<WolfBlessingResolutionOutcome> _wolfBlessingPreviews = [];
        public bool WolfBlessingBoundaryReached { get; private set; }

        public WolfBlessingResolutionOutcome PreviewWolfBlessing(Entity source, int level)
        {
            var observedPlayer = Player is not null ? _snapshot.Entities.GetValueOrDefault(Player.Id) : null;
            var originalHandCount = _snapshot.Entities.Values.Count(entity => entity.ControllerId == Local && entity.Zone == "HAND");
            if (observedPlayer?.Tags.GetValueOrDefault("NUM_CARDS_IN_HAND") is { } rawCount &&
                (!int.TryParse(rawCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count != originalHandCount))
                // A simulated departure cannot repair an originally incomplete candidate set.
                return WolfBlessingResolutionRules.Evaluate(_snapshot, source.Id, _cards);
            var hand = Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "HAND").Select(entity =>
            {
                var observed = _snapshot.Entities[entity.Id];
                var tags = observed.Tags.ToDictionary();
                foreach (var (key, value) in entity.Flags) tags[key] = value.ToString(CultureInfo.InvariantCulture);
                tags["ZONE"] = entity.Zone; tags["CONTROLLER"] = Local!.Value.ToString(CultureInfo.InvariantCulture); tags["CARDTYPE"] = entity.Type;
                // Only current simulated values are used. Missing observations stay missing.
                Set("ATK", "47", entity.Attack); Set("COST", "48", entity.Cost);
                return observed with { CardId = entity.CardId, Tags = tags.ToFrozenDictionary() };
                void Set(string key, string alias, int? value)
                {
                    if (!value.HasValue) { tags.Remove(key); tags.Remove(alias); return; }
                    var named = observed.Tags.GetValueOrDefault(key); var numeric = observed.Tags.GetValueOrDefault(alias);
                    var namedValid = int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first);
                    var numericValid = int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second);
                    if (named is not null && !namedValid || numeric is not null && !numericValid ||
                        named is not null && numeric is not null && first != second) return;
                    tags[key] = value.Value.ToString(CultureInfo.InvariantCulture);
                    if (numeric is not null) tags[alias] = tags[key];
                }
            }).ToArray();
            var before = new WolfBlessingResolutionState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, level,
                Array.AsReadOnly(hand)) { CurrentSourceCardId = source.CardId };
            return WolfBlessingResolutionRules.Evaluate(_snapshot, source.Id, _cards, before);
        }

        public void RecordWolfBlessing(WolfBlessingResolutionOutcome preview)
        {
            _wolfBlessingPreviews.Add(preview);
            foreach (var coverage in preview.RuleCoverage) Coverage.Add(coverage);
            if (preview.Branches.Count <= 1) return;
            WolfBlessingBoundaryReached = true;
            Conditional("巨狼祝福的各候选增益见独立分支；技能费用已记录，未修改任一随机候选，后续动作需要观察实际命中。", "RANDOM_WOLF_TARGET");
        }
    }
}
