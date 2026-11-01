using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;

namespace HsHelper.Coaching;

/// <summary>Construction facts for the exact imported deck used by a pre-game plan.</summary>
public static class DeckConstructionContext
{
    public static async Task<AdviceContext> AttachAsync(AdviceContext context, CardCatalog catalog,
        DeckDefinition deck, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(deck);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TaskKind != AdviceTaskKind.DeckPlan)
            throw new ArgumentException("构筑背景只能附加到赛前套牌计划。", nameof(context));
        var at = context.KnowledgeAsOf ?? context.AsOf
            ?? throw new ArgumentException("套牌计划必须固定构筑查询时点。", nameof(context));
        var frozen = new DeckDefinition(deck.Format, Array.AsReadOnly(deck.Heroes.ToArray()),
            Array.AsReadOnly(deck.Cards.ToArray()), Array.AsReadOnly(deck.Sideboards.Select(board =>
                new DeckSideboard(board.OwnerDbfId, Array.AsReadOnly(board.Cards.ToArray()))).ToArray()));
        var projected = JsonSerializer.SerializeToNode(new
        {
            format = frozen.Format, heroes = frozen.Heroes,
            cards = frozen.Cards.Select(card => new { dbf_id = card.DbfId, count = card.Count }),
            sideboards = frozen.Sideboards.Select(board => new
            {
                owner_dbf_id = board.OwnerDbfId,
                cards = board.Cards.Select(card => new { dbf_id = card.DbfId, count = card.Count })
            })
        });
        static string? KnownBuild(string? value) => value is null or "" or "unknown" ? null : value;
        if (!JsonNode.DeepEquals(context.Snapshot["imported_deck"], projected) ||
            KnownBuild(context.Snapshot["card_build"]?.GetValue<string>()) != KnownBuild(context.CardBuild))
            throw new ArgumentException("构筑背景与模型上下文的原套牌或卡库不一致。", nameof(deck));
        var snapshot = (JsonObject)context.Snapshot.DeepClone();
        JsonNode? size = null;
        var legality = CardLegality.Unknown.ToString();
        string? ruleId = null;
        string[] issues;
        const int maximumConstructionEntries = 500;
        var oversized = frozen.Cards.Count > maximumConstructionEntries || frozen.Cards.Select(card => card.DbfId).Concat(frozen.Heroes)
            .Concat(frozen.Sideboards.Select(board => board.OwnerDbfId))
            .Concat(frozen.Sideboards.SelectMany(board => board.Cards).Select(card => card.DbfId))
            .Distinct().Take(maximumConstructionEntries + 1).Count() > maximumConstructionEntries;
        if (context.CardBuild is null or "" or "unknown")
            issues = ["没有固定卡库版本，不能核对构筑规则。"];
        else if (oversized)
            issues = ["套牌或边栏的卡牌种类超过500项构筑检查上限，构筑合法性保持未知；原套牌及卡牌资料仍可用于有条件的赛前计划。"];
        else
        {
            var description = await catalog.DescribeDeckAsync(frozen, context.CardBuild, at,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            legality = description.Legality.ToString();
            ruleId = description.RuleId;
            issues = description.Issues.Take(100).ToArray();
            if (description.MainDeckSize is { } outcome)
                size = JsonSerializer.SerializeToNode(new
                {
                    actual_cards = outcome.ActualCards, required_cards = outcome.RequiredCards,
                    known = outcome.IsKnown, satisfied = outcome.IsSatisfied,
                    sources = outcome.Sources.Select(source => new
                    {
                        owner_card_id = source.OwnerCardId, owner_name = source.OwnerName,
                        required_cards = source.RequiredCards, evidence_id = source.EvidenceId,
                        evidence_source = source.EvidenceSource
                    }),
                    unknowns = outcome.Unknowns
                });
        }
        snapshot["deck_construction"] = new JsonObject
        {
            ["card_build"] = context.CardBuild,
            ["format"] = frozen.Format,
            ["checked_at"] = at.ToString("o"),
            ["actual_main_cards"] = frozen.Cards.Sum(card => (long)card.Count),
            ["legality"] = legality,
            ["pool_rule_id"] = ruleId,
            ["ordinary_main_reference"] = 30,
            ["input_limit_reached"] = oversized,
            ["maximum_checked_entries"] = maximumConstructionEntries,
            ["special_main_deck_size"] = size,
            ["issues"] = JsonSerializer.SerializeToNode(issues),
            ["scope"] = "固定套牌与查询时点的构筑检查；张数要求不证明完整标准卡池合法，不是开局复制后的牌库数量、英雄生命或实时局面。"
        };
        return context with { Snapshot = snapshot };
    }
}
