using System.Globalization;
using HsHelper.Core;

namespace HsHelper.Rules;

public sealed record ImbueCountEvidence(string MatchId, long StateVersion, long EventSequence,
    string CardBuild, int? PlayerId, int? Count, IReadOnlyList<int> SourceEntityIds,
    IReadOnlyList<string> Unknowns, IReadOnlyList<string> RuleCoverage)
{
    public bool? AtLeast(int threshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        return Count.HasValue ? Count.Value >= threshold : null;
    }
}

/// <summary>
/// Build 255790 cumulative Imbue evidence, independent of the current hero power's text parameters.
/// HearthSim's ImbueCounter has read numeric tag 3527 by CONTROLLER since commit
/// ced472ea87bc5d9dbfa28167412628f29f3935db (2025-03-28); current source blob:
/// https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/23e977a99728981b5d183fec9cb1be00b378e74c/Hearthstone%20Deck%20Tracker/Hearthstone/CounterSystem/Counters/ImbueCounter.cs .
/// This snapshot reader accepts the confirmed player and explicitly linked current hero/power only.
/// It does not reuse an old power, a hidden card, an arbitrary enchantment, or an inferred skill level.
/// </summary>
public static class ImbueEvidence
{
    public const string CounterTag = "3527";

    public static ImbueCountEvidence Evaluate(GameSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var unknowns = new List<string>();
        var coverage = new List<string>();
        var sources = new List<int>();
        int? count = null;
        var local = ChoiceVisibility.ConfirmedLocalPlayer(snapshot);
        if (snapshot.ClientBuild != "255790" || local is null || !snapshot.Quality.HasGameStart ||
            !snapshot.Quality.IsComplete || !snapshot.Quality.IsStable || snapshot.Quality.OpenBlockCount != 0)
        {
            unknowns.Add("累计灌注需要 build 255790 的完整稳定我方视角。");
            return Result();
        }
        var player = snapshot.Entities.Values.Single(entity => entity.Kind == EntityKind.Player && entity.GetIntTag("PLAYER_ID") == local);
        if (player.ControllerId is { } playerOwner && playerOwner != local)
        { unknowns.Add("我方玩家实体的归属冲突，累计灌注保持未知。"); return Result(); }

        var carriers = new List<EntitySnapshot> { player };
        var hero = Linked(player.GetIntTag("HERO_ENTITY"), "HERO", 3);
        if (hero is not null) carriers.Add(hero);
        var powerLinks = new[] { player.GetIntTag("HERO_POWER_ENTITY"), hero?.GetIntTag("HERO_POWER_ENTITY") }
            .Where(id => id is > 0).Select(id => id!.Value).Distinct().ToArray();
        if (powerLinks.Length > 1)
        { unknowns.Add("当前英雄技能实体链接冲突，累计灌注保持未知。"); return Result(); }
        if (powerLinks.Length == 1 && Linked(powerLinks[0], "HERO_POWER", 10) is { } power) carriers.Add(power);

        var observed = new List<int>();
        foreach (var carrier in carriers.DistinctBy(entity => entity.Id))
        {
            if (!carrier.Tags.TryGetValue(CounterTag, out var value)) continue;
            sources.Add(carrier.Id);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) || current < 0)
            {
                unknowns.Add("已观察的累计灌注计数格式或范围无效。");
                return Result();
            }
            observed.Add(current);
        }
        if (observed.Count == 0)
        { unknowns.Add("当前玩家及已链接英雄/技能未观察到累计标签3527；不能从技能等级推算次数或把缺失当零。"); return Result(); }
        if (observed.Distinct().Count() != 1)
        { unknowns.Add("当前累计灌注计数互相冲突，未选取较大值或技能等级替代。"); return Result(); }
        count = observed[0];
        coverage.Add("build-255790-explicit-cumulative-imbue-tag-3527");
        return Result();

        EntitySnapshot? Linked(int? id, string type, int numericType)
        {
            if (id is not (> 0) || !snapshot.Entities.TryGetValue(id.Value, out var entity) || entity.Id != id ||
                entity.Kind != EntityKind.Card || entity.ControllerId != local || entity.Zone != "PLAY" ||
                string.IsNullOrWhiteSpace(entity.CardId) ||
                entity.Tags.GetValueOrDefault("CARDTYPE") is not { } cardType ||
                cardType != type && cardType != numericType.ToString(CultureInfo.InvariantCulture)) return null;
            return entity;
        }

        ImbueCountEvidence Result() => new(snapshot.MatchId, snapshot.StateVersion, snapshot.EventSequence,
            snapshot.ClientBuild ?? "unknown", local, count, Array.AsReadOnly(sources.ToArray()),
            Array.AsReadOnly(unknowns.ToArray()), Array.AsReadOnly(coverage.ToArray()));
    }
}
