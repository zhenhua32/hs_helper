namespace HsHelper.Rules;

/// <summary>A local simulated request after using a proven first selection, not a logged PLAY or observed second offer.</summary>
public sealed record MapPlayTriggerOutcome(string MatchId, long StateVersion, long EventSequence,
    int MapSourceEntityId, int PlayedEntityId, int InitialChoiceId, string OriginalChosenCardId, string? PlayedCardId,
    bool? IdentityMatch, bool PriorResolutionPending, bool RequiresObservation, IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Assumptions, IReadOnlyList<string> RuleCoverage)
{
    public bool IsConditionalRequest => true;
    public bool CanEvaluateFollowupActions => false;
    public bool HasAuthoritativeNextState => false;
}

public sealed partial class LineEvaluator
{
    private sealed partial class Runtime
    {
        private readonly List<MapPlayTriggerOutcome> _mapPlayPreviews = [];
        public bool MapFollowupBoundaryReached { get; private set; }

        public IReadOnlyList<MapFollowupOutcome> CaptureMapPlayTriggers(Entity source)
        {
            if (source.Controller != Local || source.Zone != "HAND" || _snapshot.Phase != HsHelper.Core.GamePhase.Main ||
                _snapshot.ActivePlayerId != Local) return Array.AsReadOnly(Array.Empty<MapFollowupOutcome>());
            var captured = new List<MapFollowupOutcome>();
            foreach (var map in _snapshot.Entities.Values.Where(entity => entity.CardId == "TLC_824" &&
                entity.ControllerId == Local && entity.Zone is "PLAY" or "GRAVEYARD").OrderBy(entity => entity.Id))
            {
                var evidence = MapFollowupRules.Evaluate(_snapshot, map.Id, _cards);
                if (evidence.Stage != "AwaitingSelectedCardPlay" || evidence.InitialSelectedEntityId != source.Id ||
                    evidence.InitialChoiceId is null || string.IsNullOrWhiteSpace(evidence.InitialSelectedCardId) ||
                    evidence.TurnNumber != _snapshot.Turn || _snapshot.TurnHistory is not { } turns ||
                    !turns.Turns.Any(turn => turn.PlayerId == Local && turn.TurnNumber == evidence.TurnNumber &&
                        turn.HasStartBoundary && turn.EndedAtEventSequence is null &&
                        turn.StartedAtEventSequence < evidence.InitialMapPlayedAtEventSequence)) continue;
                captured.Add(evidence);
            }
            return Array.AsReadOnly(captured.ToArray());
        }

        public bool RecordMapPlayTriggers(IReadOnlyList<MapFollowupOutcome> before, Entity played, bool priorPending)
        {
            ArgumentNullException.ThrowIfNull(before);
            if (played.Zone == "HAND") return false;
            var waits = false;
            foreach (var captured in before)
            {
                if (captured is null || captured.MatchId != _snapshot.MatchId || captured.StateVersion != _snapshot.StateVersion ||
                    captured.EventSequence != _snapshot.EventSequence || captured.Stage != "AwaitingSelectedCardPlay" ||
                    captured.InitialSelectedEntityId != played.Id || captured.InitialChoiceId is not { } firstId ||
                    string.IsNullOrWhiteSpace(captured.InitialSelectedCardId)) continue;
                // Recheck the immutable observed evidence. The simulated play must never be appended to TurnHistory.
                var actual = MapFollowupRules.Evaluate(_snapshot, captured.MapSourceEntityId, _cards);
                if (actual.Stage != captured.Stage || actual.InitialChoiceId != firstId || actual.InitialSelectedEntityId != played.Id ||
                    actual.InitialSelectedCardId != captured.InitialSelectedCardId || actual.TurnNumber != _snapshot.Turn) continue;
                waits = true;
                if (_mapPlayPreviews.Any(value => value.MapSourceEntityId == captured.MapSourceEntityId && value.InitialChoiceId == firstId &&
                    value.PlayedEntityId == played.Id)) continue;
                bool? identity = string.IsNullOrWhiteSpace(played.CardId) ? null : played.CardId == captured.InitialSelectedCardId;
                var unknowns = new List<string>
                {
                    "本地模拟已使用历史确认的首选实体；追加选择尚未由日志实际提供，不能创建候选、第二张手牌或新权威局面。"
                };
                if (identity != true) unknowns.Add("同一实体的当前卡牌身份与原始首选不同或未知；是否仍满足地图脚本关联保持未知，不能自行宣称无触发。");
                if (played.Controller != Local) unknowns.Add("模拟使用后实体归属发生变化，不能把当前控制者倒填为当时使用者；追加脚本结果待观察。");
                if (priorPending) unknowns.Add("前序战吼、抽牌、生成或其他使用后结算尚未完成，地图追加请求也在等待这些结算。");
                var assumptions = Array.AsReadOnly(new[]
                {
                    "这是本地模拟首选使用后的条件请求；实际使用与追加提供仍由新日志的 AtPlay 和 EntitiesChosen/Choice 证据核验。",
                    "原地图使用、实际首选实体和当前开放己方回合来自绑定历史；不按同名牌、CardId 或 CREATOR 猜测新实体映射。"
                });
                var coverage = Array.AsReadOnly(new[] { "TLC_824-simulated-original-selected-entity-use",
                    "TLC_824-map-followup-boundary-no-fabricated-actual-play-or-offer" });
                _mapPlayPreviews.Add(new(_snapshot.MatchId, _snapshot.StateVersion, _snapshot.EventSequence,
                    captured.MapSourceEntityId, played.Id, firstId, captured.InitialSelectedCardId, played.CardId, identity,
                    priorPending, true, Array.AsReadOnly(unknowns.ToArray()), assumptions, coverage));
                Coverage.UnionWith(coverage);
            }
            if (waits)
            {
                MapFollowupBoundaryReached = true;
                Conditional("地图首选实体的模拟使用已记录为条件请求；请等待真实追加选择，不创建候选或越过该边界继续动作。",
                    "MAP_FOLLOWUP_REQUIRES_OBSERVATION");
            }
            return waits;
        }
    }
}
