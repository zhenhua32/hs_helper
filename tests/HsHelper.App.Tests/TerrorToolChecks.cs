using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.App;

internal static class TerrorToolChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("terror result display keeps combat order healing and board within each correlated branch", () =>
        {
            var text = LocalToolResultFormatter.Format("evaluate_line", Line(), id => "角色-" + id);
            Check(text.Contains("角色-30回合末相关分支：2 个", StringComparison.Ordinal) && text.Contains("分支 a", StringComparison.Ordinal) &&
                text.Contains("分支 b", StringComparison.Ordinal) && text.Contains("我方英雄生命 24", StringComparison.Ordinal) &&
                text.Contains("我方英雄生命 28", StringComparison.Ordinal) && text.Contains("角色-40 → 角色-30", StringComparison.Ordinal) &&
                text.Contains("恐魔已死亡", StringComparison.Ordinal) && text.Contains("圣盾 否", StringComparison.Ordinal) &&
                text.Contains("实际结果仍待观测", StringComparison.Ordinal) && text.Contains("不能拼接不同分支", StringComparison.Ordinal) &&
                !text.Contains("TerrorDied", StringComparison.Ordinal), "The display lost correlated outcomes or presented the branch as observed combat.");
        });
        yield return ("terror partial and truncated branches retain unknown values and explicitly bounded display", () =>
        {
            var line = Line(); var preview = line["Result"]!["TerrorEndTurnPreviews"]![0]!;
            preview["IsEnumerationComplete"] = false;
            var branches = preview["Branches"]!.AsArray();
            branches[0]!["IsComplete"] = false; branches[0]!["OwnHeroHealthAfter"] = null; branches[0]!["StopReason"] = "DeathrattleOrReborn";
            while (branches.Count < 9) { var branch = branches[1]!.DeepClone(); branch["Id"] = "copy-" + branches.Count; branches.Add(branch); }
            var text = LocalToolResultFormatter.Format("evaluate_line", line);
            Check(text.Contains("枚举未完成", StringComparison.Ordinal) && text.Contains("未完成，仅已计算前缀", StringComparison.Ordinal) &&
                text.Contains("我方英雄生命 未知", StringComparison.Ordinal) && text.Contains("亡语或复生", StringComparison.Ordinal) &&
                text.Contains("其余 1 个分支未在此摘要展开", StringComparison.Ordinal) && text.Contains("未列出某个结果不能证明它不存在", StringComparison.Ordinal),
                "Unknown, partial or omitted terror branches became exact global bounds or disappeared silently.");
        });
        yield return ("terror branches cannot promote forged line or lethal-search flags into verified results", () =>
        {
            var line = Line(); line["Verification"] = "Verified"; line["IsLethal"] = true;
            foreach (var tool in new[] { "evaluate_line", "find_lethal" })
            {
                JsonNode input = tool == "evaluate_line" ? line : new JsonObject { ["VerifiedLethalFound"] = true, ["Evaluation"] = line.DeepClone() };
                var text = LocalToolResultFormatter.Format(tool, input);
                Check(text.Contains("验证级别：条件成立", StringComparison.Ordinal) && !text.Contains("已验证斩杀：是", StringComparison.Ordinal) &&
                    !text.Contains("找到已验证的斩杀路线", StringComparison.Ordinal), "A forged banner promoted a conditional terror branch into verified lethal.");
            }
        });
        yield return ("turn and choice v22 policies constrain terror branch correlation and observation scope", () =>
        {
            foreach (var task in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
            {
                var policy = AdviceTaskTemplates.Get(task);
                Check(policy.Version == "hunter-v22/" + task.ToString().ToLowerInvariant() && policy.Instructions.Contains("TerrorEndTurnPreviews", StringComparison.Ordinal) &&
                    policy.Instructions.Contains("等概率", StringComparison.Ordinal) && policy.Instructions.Contains("普通攻击次数/就绪字段", StringComparison.Ordinal),
                    "The model policy omitted conditional forced-combat branches or their unobserved ordinary counters.");
            }
            Check(AdviceTaskTemplates.Get(AdviceTaskKind.Mulligan).Version == "hunter-v7/mulligan", "Terror policies changed unrelated task versioning.");
        });
    }

    private static JsonNode Line() => JsonNode.Parse("""
        {"Verification":"Conditional","IsLegal":null,"IsLethal":false,"AppliedActions":1,"Result":{"TurnEnded":true,"Entities":[],
         "TerrorEndTurnPreviews":[{"TerrorEntityId":30,"IsEnumerationComplete":true,"Branches":[
          {"Id":"a","IsComplete":true,"StopReason":"TerrorDied","OwnHeroHealthAfter":24,"OpponentHeroHealthAfter":30,"LifestealHealing":4,
           "Combats":[{"AttackerEntityId":40,"TargetEntityId":30,"DamageToTerror":5,"DamageToAttacker":4}],
           "Entities":[{"EntityId":41,"RemainingHealth":4,"RemainingArmor":0,"DivineShield":false,"IsAlive":true}]},
          {"Id":"b","IsComplete":true,"StopReason":"TerrorDied","OwnHeroHealthAfter":28,"OpponentHeroHealthAfter":30,"LifestealHealing":8,
           "Combats":[{"AttackerEntityId":41,"TargetEntityId":30,"DamageToTerror":1,"DamageToAttacker":4},{"AttackerEntityId":40,"TargetEntityId":30,"DamageToTerror":5,"DamageToAttacker":4}],
           "Entities":[{"EntityId":41,"RemainingHealth":0,"RemainingArmor":0,"DivineShield":false,"IsAlive":false}]}],"Unknowns":[]} ]}}
        """)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
