using System.Collections.Frozen;
using System.Globalization;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    private sealed record PlayTriggerFrame(EntitySnapshot PlayedBefore, IReadOnlyList<int> QuestIds,
        IReadOnlyList<EntitySnapshot> RaptorsBefore, IReadOnlyList<MapFollowupOutcome> MapsBefore);

    private sealed partial class Runtime
    {
        private readonly List<QuestProgressOutcome> _questProgressPreviews = [];
        private readonly List<DreamboundRaptorOutcome> _raptorPreviews = [];
        private readonly Dictionary<int, (int? Progress, int? Total)> _questCounters = [];
        private readonly HashSet<int> _installedQuestIds = [];
        private readonly HashSet<int> _summonedWithObservedHealth = [];
        public bool QuestRewardBoundaryReached { get; private set; }
        public bool RaptorBoundaryReached { get; private set; }

        public PlayTriggerFrame? CapturePlayTriggers(PlannedAction action)
        {
            if (!Entities.TryGetValue(action.SourceEntityId, out var source) || source.Controller != Local || source.Zone != "HAND") return null;
            return new(FreezeForPlayTriggers(source),
                Array.AsReadOnly(Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "SECRET" && entity.CardId == "TLC_EVENT_400")
                    .Select(entity => entity.Id).Order().ToArray()),
                Array.AsReadOnly(Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "PLAY" && entity.CardId == "EDR_849" && !HasCustomAssembledText(entity))
                    .OrderBy(entity => entity.Id).Select(FreezeForPlayTriggers).ToArray()), CaptureMapPlayTriggers(source));
        }

        public void ResolvePlayTriggers(PlayTriggerFrame? before)
        {
            if (before is null || OpponentDefeatedByPayment || OwnHero?.Health <= 0 || EnemyHero?.Health <= 0 ||
                !Entities.TryGetValue(before.PlayedBefore.Id, out var played) || played.Zone == "HAND") return;
            var pending = HasUnresolvedNativeResolution;
            pending |= RecordRaptorTriggers(before, played, pending);
            pending |= RecordMapPlayTriggers(before.MapsBefore, played, pending);
            foreach (var id in before.QuestIds)
            {
                if (!Entities.TryGetValue(id, out var quest) || quest.Zone != "SECRET" || quest.Controller != Local) continue;
                if (!_questCounters.TryGetValue(id, out var counters))
                {
                    var observed = QuestProgressRules.Evaluate(_snapshot, id, _cards);
                    counters = (observed.CurrentProgress, observed.Total);
                }
                var state = new QuestProgressState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, id, counters.Progress, counters.Total)
                    { IsInstalled = _installedQuestIds.Contains(id), PriorResolutionPending = pending };
                var outcome = QuestProgressRules.Evaluate(_snapshot, id, _cards, before.PlayedBefore, state);
                _questProgressPreviews.Add(outcome);
                foreach (var coverage in outcome.RuleCoverage) Coverage.Add(coverage);
                if (outcome.CanProjectProgress && outcome.ResultingProgress is { } progress)
                {
                    _questCounters[id] = (progress, outcome.Total);
                    quest.Flags["QUEST_PROGRESS"] = progress;
                    if (quest.Flags.ContainsKey("534")) quest.Flags["534"] = progress;
                }
                if (outcome.RequiresRewardChoice || !outcome.CanProjectProgress && outcome.PlayedCardQualifies != false)
                {
                    QuestRewardBoundaryReached = true;
                    Conditional("任务计数或奖励选择需要新观测；进度见独立预览，不生成僵尸兽或继续跨越奖励选择。", "QUEST_PROGRESS_REQUIRES_OBSERVATION");
                }
            }
        }

        private bool RecordRaptorTriggers(PlayTriggerFrame before, Entity played, bool pending)
        {
            if (played.Type != "MINION" || before.RaptorsBefore.Count == 0 && (before.PlayedBefore.CardId != "EDR_849" || HasCustomAssembledText(played))) return false;
            var afterSources = before.RaptorsBefore.Select(source => Entities[source.Id]).ToList();
            if (played.CardId == "EDR_849" && !HasCustomAssembledText(played) && afterSources.All(source => source.Id != played.Id)) afterSources.Add(played);
            var after = new DreamboundRaptorState(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                before.PlayedBefore, FreezeForPlayTriggers(played), before.RaptorsBefore,
                Array.AsReadOnly(afterSources.Select(FreezeForPlayTriggers).ToArray())) { PriorResolutionPending = pending };
            var raptor = DreamboundRaptorRules.Evaluate(_snapshot, played.Id, _cards, after);
            _raptorPreviews.Add(raptor);
            foreach (var coverage in raptor.RuleCoverage) Coverage.Add(coverage);
            if (raptor.RequiresObservation || !raptor.ResolvesTriggerCount)
            {
                RaptorBoundaryReached = true;
                Conditional("梦缚迅猛龙的触发来源和次数见独立预览，随机额外效果尚未观察，不赋予任意关键词或继续后续动作。", "RAPTOR_BONUS_REQUIRES_OBSERVATION");
            }
            return raptor.RequiresObservation || !raptor.ResolvesTriggerCount;
        }

        private EntitySnapshot FreezeForPlayTriggers(Entity entity)
        {
            var original = _snapshot.Entities[entity.Id]; var tags = original.Tags.ToDictionary();
            foreach (var (key, value) in entity.Flags) tags[key] = value.ToString(CultureInfo.InvariantCulture);
            Semantic("ZONE", "49", entity.Zone, ZoneNumber);
            // Preserve absent or explicitly invalid type evidence rather than repairing it with a profile.
            if (original.Tags.ContainsKey("CARDTYPE") || original.Tags.ContainsKey("202")) Semantic("CARDTYPE", "202", entity.Type, TypeNumber);
            if (entity.Controller.HasValue) Semantic("CONTROLLER", "50", entity.Controller.Value.ToString(CultureInfo.InvariantCulture), Number);
            var observedHealth = original.GetIntTag("HEALTH").HasValue;
            var knownFreshHealth = _summonedWithObservedHealth.Contains(entity.Id);
            Set("ATK", "47", original.GetIntTag("ATK").HasValue ? entity.Attack : null);
            Set("COST", "48", entity.Cost);
            Set("HEALTH", "45", observedHealth || knownFreshHealth ? entity.MaximumHealth : null);
            Set("DAMAGE", "44", (original.GetIntTag("DAMAGE").HasValue || knownFreshHealth) &&
                entity.MaximumHealth is { } max && entity.Health is { } health ? max - health : null);
            Set("NUM_ATTACKS_THIS_TURN", "297", entity.Attacks);
            return original with { CardId = entity.CardId, Tags = tags.ToFrozenDictionary() };
            void Semantic(string name, string alias, string value, Func<string, int?> parse)
            {
                var named = original.Tags.GetValueOrDefault(name); var numeric = original.Tags.GetValueOrDefault(alias);
                var first = named is null ? null : parse(named); var second = numeric is null ? null : parse(numeric);
                if (named is not null && first is null || numeric is not null && second is null ||
                    first.HasValue && second.HasValue && first != second) return;
                tags[name] = value;
                if (numeric is not null && parse(value) is { } next) tags[alias] = next.ToString(CultureInfo.InvariantCulture);
            }
            void Set(string name, string alias, int? value)
            {
                if (value is null) { tags.Remove(name); tags.Remove(alias); return; }
                var named = original.Tags.GetValueOrDefault(name); var numeric = original.Tags.GetValueOrDefault(alias);
                var namedOk = int.TryParse(named, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first);
                var numericOk = int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second);
                if (named is not null && !namedOk || numeric is not null && !numericOk || named is not null && numeric is not null && first != second) return;
                tags[name] = value.Value.ToString(CultureInfo.InvariantCulture);
                if (numeric is not null) tags[alias] = tags[name];
            }
        }

        private static int? Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
        private static int? ZoneNumber(string value) => value switch
        { "PLAY" => 1, "DECK" => 2, "HAND" => 3, "GRAVEYARD" => 4, "REMOVEDFROMGAME" => 5, "SETASIDE" => 6, "SECRET" => 7, _ => Number(value) };
        private static int? TypeNumber(string value) => value switch
        { "MINION" => 4, "SPELL" => 5, "WEAPON" => 7, "HERO" => 3, "HERO_POWER" => 10, "LOCATION" => 39, _ => Number(value) };
        public bool ObservedCardType(Entity entity, string expected)
        {
            var original = _snapshot.Entities[entity.Id]; var expectedNumber = TypeNumber(expected);
            return (!original.Tags.TryGetValue("CARDTYPE", out var named) || TypeNumber(named) == expectedNumber) &&
                (!original.Tags.TryGetValue("202", out var alias) || TypeNumber(alias) == expectedNumber);
        }
        public bool ObservedPlayIdentity(Entity entity)
        {
            var original = _snapshot.Entities[entity.Id];
            return (!original.Tags.TryGetValue("50", out var controller) || Number(controller) == entity.Controller) &&
                (!original.Tags.TryGetValue("49", out var zone) || ZoneNumber(zone) == ZoneNumber(entity.Zone)) &&
                (!original.Tags.TryGetValue("ENTITY_ID", out var id) || Number(id) == entity.Id) &&
                (!original.Tags.TryGetValue("53", out var numericId) || Number(numericId) == entity.Id);
        }

        public bool StopForExistingQuestReward()
        {
            foreach (var quest in Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "SECRET" && IsNativeSidequest(entity)))
            {
                var outcome = QuestProgressRules.Evaluate(_snapshot, quest.Id, _cards);
                if (!outcome.RequiresRewardChoice) continue;
                _questProgressPreviews.Add(outcome); QuestRewardBoundaryReached = true;
                Unsupported("AWAIT_QUEST_REWARD_SNAPSHOT", "已观察任务完成计数，奖励选择尚未就绪，不能在此前继续支付或执行动作。");
                return true;
            }
            return false;
        }

        public bool IsNativeSidequest(Entity entity) => _snapshot.ClientBuild == AuditedBuild && entity.CardId == "TLC_EVENT_400" &&
            NativeCardDefinitions.IsAudited(_snapshot.ClientBuild, entity.CardId, _cards.GetValueOrDefault(entity.CardId)) &&
            Profile(entity) is { Type: "SPELL" } profile && profile.Mechanics.Contains("SIDE_QUEST") && !HasCustomAssembledText(entity);

        public bool CanInstallInSecretZone(Entity source)
        {
            // Sidequests share the existing five-slot secret-zone model. Same-sidequest uniqueness is publisher-confirmed.
            var occupants = Entities.Values.Where(entity => entity.Controller == Local && entity.Zone == "SECRET").ToArray();
            if (occupants.Length >= 5) { Invalid("奥秘区已达五槽上限（包含支线任务和其他占位效果）。"); return false; }
            if (occupants.Any(entity => entity.CardId == source.CardId || source.CardId is { } id && ExplosiveTrap.Contains(id) &&
                entity.CardId is { } other && ExplosiveTrap.Contains(other)))
            { Invalid("已有同名奥秘或支线任务，不能同时安装第二份。"); return false; }
            if (occupants.Any(entity => string.IsNullOrWhiteSpace(entity.CardId) || Profile(entity) is null))
            { Unsupported("UNKNOWN_SECRET_ZONE_OCCUPANT", "我方奥秘区存在身份未确认的占位，不能确认同名限制。"); return false; }
            return true;
        }

        public void InstallSidequest(Entity source)
        {
            if (!CanInstallInSecretZone(source)) return;
            var check = QuestProgressRules.Evaluate(_snapshot, source.Id, _cards, beforePlay:
                new(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence, source.Id, 0, 3) { IsInstalled = true });
            if (check.QuestCardId != "TLC_EVENT_400" || check.CurrentProgress != 0 || check.Total != 3 || check.Unknowns.Count != 0)
            { Unsupported("UNCONFIRMED_SIDEQUEST_DEFINITION", "围攻城门的原生定义、当前类型或归属证据不足，未支付或安装。"); return; }
            if (!Pay(source) || OpponentDefeatedByPayment) return;
            source.Zone = "SECRET"; source.Flags["SIDE_QUEST"] = 1;
            source.Flags["QUEST_PROGRESS"] = 0; source.Flags["QUEST_PROGRESS_TOTAL"] = 3;
            source.Flags.Remove("534"); source.Flags.Remove("535");
            _installedQuestIds.Add(source.Id); _questCounters[source.Id] = (0, 3);
            Coverage.Add("TLC_EVENT_400-install-new-sidequest-counter-zero");
        }
    }
}
