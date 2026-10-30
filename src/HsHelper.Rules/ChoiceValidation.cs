using HsHelper.Core;

namespace HsHelper.Rules;

public sealed partial class LineEvaluator
{
    /// <summary>Validates a submitted option set, without resolving the choice's card effects.</summary>
    public ChoiceValidationResult ValidateChoice(GameSnapshot snapshot, IReadOnlyList<int> selectedEntityIds,
        int? expectedChoiceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(selectedEntityIds);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = Array.AsReadOnly(selectedEntityIds.ToArray());
        var errors = new List<string>();
        var assumptions = new List<string> { "只验证当前选择范围、数量和归属；未模拟选择后效果，不能据此断言斩杀或后续动作。" };
        var coverage = new List<string>();
        var unsupported = false;
        var choice = snapshot.Choice;
        if (ChoiceVisibility.ConfirmedOwn(snapshot) is { } confirmed) choice = confirmed;
        else
        {
            unsupported = true;
            assumptions.Add("候选、已有实体与玩家视角尚未共同通过当前选择资格校验，不能将冲突或未稳定的选择当作已确认。");
        }
        if (!snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || !snapshot.Quality.HasLocalPerspective || snapshot.IsSpectating)
        { unsupported = true; assumptions.Add("选择需要完整、稳定且已确认我方身份的玩家视角。"); }
        if (snapshot.Phase is not (GamePhase.Choice or GamePhase.Mulligan)) errors.Add("当前不是可提交选择的阶段。");
        if (snapshot.LocalPlayerId is null || choice is null)
        { unsupported = true; assumptions.Add("当前选择或我方身份未知。"); }
        else
        {
            if (expectedChoiceId is { } expected && expected != choice.Id) errors.Add("选择标识已变化，候选来自另一次选择。");
            if (choice.PlayerId is null) { unsupported = true; assumptions.Add("选择归属尚未确认。"); }
            else if (choice.PlayerId != snapshot.LocalPlayerId) errors.Add("不能替对手提交选择。");
            else coverage.Add("current-choice-owner");
            if (!choice.IsReady) { unsupported = true; assumptions.Add("候选详情尚未读取完成。"); }
            else coverage.Add("ready-choice-options");
            if (choice.Options is null || choice.CountMin < 0 || choice.CountMax < choice.CountMin || choice.CountMax > choice.Options.Count ||
                choice.Options.Any(option => option is null || option.EntityId <= 0) || choice.Options.Select(option => option.EntityId).Distinct().Count() != choice.Options.Count)
            { unsupported = true; assumptions.Add("候选集合或选择数量约束不完整或存在冲突。"); }
            else
            {
                if (selected.Count < choice.CountMin || selected.Count > choice.CountMax) errors.Add("选择数量不在当前允许范围内。");
                if (selected.Distinct().Count() != selected.Count) errors.Add("同一候选不能重复选择。");
                var options = choice.Options.ToDictionary(option => option.EntityId);
                foreach (var id in selected)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!options.TryGetValue(id, out var option)) errors.Add("所选实体不是本次提供的候选。");
                    else if (option.ControllerId is { } controller && controller != snapshot.LocalPlayerId)
                        errors.Add("所选候选的归属与当前选择不一致。");
                }
                coverage.AddRange(["choice-id-binding", "option-membership", "selection-cardinality", "selection-uniqueness"]);
            }
        }
        var verification = errors.Count > 0 ? LineVerification.Invalid : unsupported ? LineVerification.Unsupported : LineVerification.Verified;
        return new(snapshot.MatchId, snapshot.StateVersion, choice?.Id, verification,
            verification == LineVerification.Verified, selected, errors.AsReadOnly(), assumptions.AsReadOnly(), coverage.AsReadOnly());
    }
}
