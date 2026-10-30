namespace HsHelper.Cards;

public sealed partial class CardCatalog
{
    private static DeckConstructionEvidence? NormalizeConstruction(DeckConstructionEvidence? evidence, CardPoolRules parent)
    {
        if (evidence is null) return null;
        static void Label(string? value, int maximum, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
                throw new InvalidDataException("构筑证据的" + label + "无效。");
        }
        static IReadOnlyList<string> Ids(IReadOnlyList<string>? ids, bool allowEmpty)
        {
            if (ids is null || ids.Count > 50000 || !allowEmpty && ids.Count == 0)
                throw new InvalidDataException("构筑证据的卡牌列表无效。");
            foreach (var id in ids) Label(id, 200, "卡牌标识");
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
                throw new InvalidDataException("构筑证据包含重复卡牌标识。");
            return Array.AsReadOnly(ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        }
        Label(evidence.RuleId, 200, "RuleId"); Label(evidence.Source, 2000, "来源");
        if (evidence.Build != parent.Build || evidence.Format != parent.Format ||
            evidence.EffectiveFrom < parent.EffectiveFrom || evidence.ValidUntil > parent.ValidUntil ||
            evidence.ValidUntil <= evidence.EffectiveFrom)
            throw new InvalidDataException("构筑证据的 build、模式和有效时间必须在父卡池范围内。");
        if (evidence.CardGroups is null || evidence.Sideboards is null ||
            evidence.CardGroups.Count > 100 || evidence.Sideboards.Count > 100 ||
            evidence.CardGroups.Count + evidence.Sideboards.Count == 0)
            throw new InvalidDataException("构筑证据约束列表无效。");
        var groups = new List<DeckCardGroupLimit>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in evidence.CardGroups)
        {
            if (group is null) throw new InvalidDataException("构筑证据不能包含空组。");
            Label(group.GroupId, 200, "组标识");
            if (!groupIds.Add(group.GroupId) || group.MaximumCopies is < 1 or > 1000)
                throw new InvalidDataException("构筑证据包含重复组或无效数量限制。");
            groups.Add(group with { CardIds = Ids(group.CardIds, false) });
        }
        var sideboards = new List<DeckSideboardRule>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sideboard in evidence.Sideboards)
        {
            if (sideboard is null) throw new InvalidDataException("构筑证据不能包含空边栏规则。");
            Label(sideboard.OwnerCardId, 200, "边栏拥有者");
            if (!owners.Add(sideboard.OwnerCardId) || sideboard.MaximumCards is < 1 or > 1000 ||
                sideboard.MinimumCards is < 0 || sideboard.MinimumCards > sideboard.MaximumCards ||
                sideboard.MaximumCopiesPerCard is < 1 || sideboard.MaximumCopiesPerCard > sideboard.MaximumCards)
                throw new InvalidDataException("构筑证据包含重复拥有者或冲突的边栏数量限制。");
            sideboards.Add(sideboard with { AllowedCardIds = Ids(sideboard.AllowedCardIds, !sideboard.AllowedCardsComplete) });
        }
        return evidence with
        {
            EffectiveFrom = evidence.EffectiveFrom.ToUniversalTime(), ValidUntil = evidence.ValidUntil.ToUniversalTime(),
            CardGroups = Array.AsReadOnly(groups.OrderBy(g => g.GroupId, StringComparer.Ordinal).ToArray()),
            Sideboards = Array.AsReadOnly(sideboards.OrderBy(s => s.OwnerCardId, StringComparer.Ordinal).ToArray())
        };
    }

    private static bool NeedsUnmappedConstructionRule(CardRecord card, DeckConstructionEvidence? evidence)
    {
        using var raw = System.Text.Json.JsonDocument.Parse(card.RawJson);
        if (IsConstructionException(raw.RootElement, card.Text) &&
            evidence?.Sideboards.Any(rule => rule.OwnerCardId == card.Id && rule.KeepsOrdinaryMainDeckRules) != true)
            return true;
        // This only requests evidence; it never infers a group's members or grants legality from text.
        var mentionsOldGod = ReviewedConstructionRules.IsKnownOldGod(card.Id) || card.Text.Contains("<b>古神", StringComparison.Ordinal) ||
            card.Text.Contains("<b>Old God", StringComparison.OrdinalIgnoreCase);
        return mentionsOldGod && evidence?.CardGroups.Any(group => group.CardIds.Contains(card.Id, StringComparer.Ordinal)) != true;
    }
}

internal static class ReviewedConstructionRules
{
    private static readonly string[] OldGods = ["BE_009", "BE_013", "BE_017", "BE_784"];

    internal static bool IsKnownOldGod(string id) => OldGods.Contains(id, StringComparer.Ordinal);

    internal static DeckConstructionEvidence? Create(string build, DateTimeOffset from, DateTimeOffset until,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> definitions)
    {
        if (build != "255790") return null;
        var groups = new List<DeckCardGroupLimit>();
        var sideboards = new List<DeckSideboardRule>();
        if (OldGods.All(id => definitions.TryGetValue(id, out var tags) && tags.GetValueOrDefault("OLD_GOD") == "1"))
            groups.Add(new("old-gods", Array.AsReadOnly(OldGods.ToArray()), 1));
        if (definitions.TryGetValue("JAIL_831", out var owner) && owner.GetValueOrDefault("MAX_SIDEBOARD_CARDS") == "3" &&
            owner.GetValueOrDefault("SIDEBOARD_TYPE") == "4" && owner.GetValueOrDefault("ALLOWED_SIDEBOARD_SUBSET") == "19499" &&
            owner.GetValueOrDefault("SIDEBOARD_IGNORES_CLASS_RULE") == "1")
            // CardDefs identifies the subset but does not enumerate it. Capacity is proven; membership stays unknown.
            sideboards.Add(new("JAIL_831", 3, [], KeepsOrdinaryMainDeckRules: true, IgnoresHeroClass: true));
        if (groups.Count + sideboards.Count == 0) return null;
        return new($"reviewed-construction-{build}-{from:yyyyMMddHHmmssfff}", build, 2, from, until,
            "https://hearthstone.blizzard.com/en-gb/news/24304163/reign-of-the-black-empire-is-now-live ; " +
            $"https://api.hearthstonejson.com/v1/{build}/CardDefs.base.xml", groups.AsReadOnly(), sideboards.AsReadOnly());
    }
}
