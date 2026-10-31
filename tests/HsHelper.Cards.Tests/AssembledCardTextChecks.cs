using System.Text.Json;
using HsHelper.Cards;

internal static class AssembledCardTextChecks
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID";
    private const string Second = "CRAFTABLE_SECOND_HALF_DBID";
    private const int Eyemaul = 130871;
    private const int Warmedic = 130580;
    private const int Guide = 118076;
    private const int Deathchiller = 117027;

    public static async Task RunAsync(Func<string, Func<Task>, Task> run)
    {
        var cards = LoadCards();
        foreach (var (name, test) in Cases(cards))
            await run(name, () => { test(); return Task.CompletedTask; });
    }

    private static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        yield return ("assembled text native cards keep their original text and require no inferred halves", () => Native(cards));
        yield return ("assembled effect half can come from an actually offered weapon without changing the base identity", () => Effect(cards));
        yield return ("assembled trigger half retains only an explicitly zero base effect half", () => Trigger(cards));
        yield return ("assembled two-donor text matches the fixed real-game Yesterloc example", () => Both(cards));
        yield return ("assembled four half buttons describe their donor half without creating a finished minion", () => Offers(cards));
        yield return ("assembled conflicting offer markers never claim a complete or unambiguous fragment", () => OfferConflicts(cards));
        yield return ("assembled missing replacement counterpart is unknown rather than assumed zero", () => MissingHalf(cards));
        yield return ("assembled named and numeric aliases must agree and database references exclude markers", () => Aliases(cards));
        yield return ("assembled malformed negative overflowing and conflicting tags always block native fallback", () => InvalidTags(cards));
        yield return ("assembled missing ambiguous and incorrectly indexed donors never fall back to base effects", () => MissingCards(cards));
        yield return ("assembled missing or ambiguous ampersand boundaries preserve unknown text", () => Boundaries(cards));
        yield return ("assembled text rejects unsupported builds and unknown base identities", () => Bindings(cards));
        yield return ("assembled half text preserves formatting and only normalizes line whitespace", () => Formatting(cards));
        yield return ("assembled text results references and diagnostics stay immutable after caller mutation", () => Frozen(cards));
    }

    private static void Native(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var tags in new[] { Tags(), Tags((First, "0"), (Second, "0")), Tags(("4572", "0")) })
        {
            var result = Resolve(cards, tags);
            Check(!AssembledCardText.RequiresCustomText(tags) && AssembledCardText.ReferencedDbfIds(tags).Count == 0 &&
                result.IsResolved && !result.IsOffer && result.EffectiveText == cards["CORE_CFM_753"].Text &&
                result.TriggerCardId == "CORE_CFM_753" && result.EffectCardId == "CORE_CFM_753" &&
                result.IsObserved == (tags.Count > 0) && result.Unknowns.Count == 0,
                "Native text was split/replaced or an unobserved half was presented as custom text.");
        }
        Check(Resolve(cards, Tags(), "BE_044").EffectiveText == cards["BE_044"].Text,
            "A native card without a split boundary lost its original text.");
    }

    private static void Effect(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var tags = Tags((First, "0"), (Second, Eyemaul.ToString()));
        var result = Resolve(cards, tags);
        Check(cards["BE_349"].Type == "WEAPON" && result is { IsObserved: true, IsOffer: false, IsResolved: true,
            BaseCardId: "CORE_CFM_753", BaseCardName: "污手街供货商", TriggerCardId: "CORE_CFM_753", EffectCardId: "BE_349" } &&
            result.EffectiveText == "<b>战吼：</b> 召唤两个2/2并具有<b>突袭</b>的元素。" &&
            result.OfferedHalfText is null && result.FirstHalfDbfId == 0 && result.SecondHalfDbfId == Eyemaul &&
            AssembledCardText.ReferencedDbfIds(tags).SequenceEqual([Eyemaul]),
            "A weapon donor was rejected, base text reused, or base identity replaced by its donor.");
    }

    private static void Trigger(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var result = Resolve(cards, Tags((First, Eyemaul.ToString()), (Second, "0")));
        Check(result.IsResolved && result.TriggerCardId == "BE_349" && result.EffectCardId == "CORE_CFM_753" &&
            result.EffectiveText == "在你的英雄攻击后， 使你手牌中的所有随从牌获得+1/+1。",
            "A trigger replacement changed the wrong half or retained the original Battlecry.");
    }

    private static void Both(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var result = Resolve(cards, Tags((First, Deathchiller.ToString()), (Second, Guide.ToString())), "TIME_428");
        Check(result.IsResolved && result.BaseCardName == "昨日鱼人" && result.TriggerCardId == "CORE_RLK_083" &&
            result.EffectCardId == "CORE_REV_308" && result.EffectiveText == "在你施放一个法术后， 随机召唤一个法力值消耗为（2）的随从。" &&
            result.Unknowns.Count == 0, "Observed two-donor text retained a Yesterloc native half or confused the DBF references.");
    }

    private static void Offers(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var id in new[] { "BE_349", "CORE_BAR_878" })
        foreach (var half in new[] { "Trigger", "Effect" })
        foreach (var counterpart in new int?[] { null, 0, cards[id].DbfId })
        {
            var marker = half == "Trigger" ? Second : First;
            var provided = half == "Trigger" ? First : Second;
            var tags = Tags((marker, "1"));
            if (counterpart.HasValue) tags[provided] = counterpart.Value.ToString();
            var result = Resolve(cards, tags, id);
            Check(result is { IsObserved: true, IsOffer: true, IsResolved: false, EffectiveText: null,
                TriggerCardId: null, EffectCardId: null } && result.OfferedHalf == half &&
                result.OfferedHalfText == cards[id].Text.Split('&')[half == "Trigger" ? 0 : 1].Trim() &&
                AssembledCardText.RequiresCustomText(tags), "An offered half became a finished card or took the other half's text.");
            Check(!AssembledCardText.ReferencedDbfIds(tags).Contains(1), "Offer marker 1 was treated as a donor database ID.");
        }
    }

    private static void OfferConflicts(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var tags in new[] { Tags((First, "1"), (Second, "1")),
            Tags((First, "1"), (Second, Warmedic.ToString())), Tags((First, "1"), (Second, "bad")) })
        {
            var result = Resolve(cards, tags, "BE_349");
            Check(result.IsOffer && !result.IsResolved && result.EffectiveText is null && result.OfferedHalfText is null &&
                result.Unknowns.Count > 0, "Contradictory/invalid offered tags supplied a usable half or finished text.");
        }
    }

    private static void MissingHalf(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var tags in new[] { Tags((First, Eyemaul.ToString())), Tags((Second, Eyemaul.ToString())) })
        {
            var result = Resolve(cards, tags);
            Check(AssembledCardText.RequiresCustomText(tags) && result.IsObserved && !result.IsOffer && !result.IsResolved &&
                result.EffectiveText is null && result.TriggerCardId is null && result.EffectCardId is null && result.Unknowns.Count > 0,
                "A missing half was silently converted to an explicit zero/base half.");
        }
    }

    private static void Aliases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var tags = Tags((First, "0"), ("4572", "0"), (Second, Eyemaul.ToString()), ("4573", Eyemaul.ToString()));
        Check(Resolve(cards, tags).IsResolved && AssembledCardText.ReferencedDbfIds(tags).SequenceEqual([Eyemaul]),
            "Consistent aliases became two donors or conflicted.");
        var numeric = Resolve(cards, Tags(("4572", "0"), ("4573", Eyemaul.ToString())));
        Check(numeric.IsResolved && numeric.EffectCardId == "BE_349", "Raw numeric tags lost their fixed semantic mapping.");
        var same = Tags((First, Eyemaul.ToString()), (Second, Eyemaul.ToString()));
        Check(AssembledCardText.ReferencedDbfIds(same).SequenceEqual([Eyemaul]), "The same donor was requested twice.");
    }

    private static void InvalidTags(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var value in new[] { "-1", "-0", "+1", "1.0", " 0", "", "unknown", "2147483648", null! })
        foreach (var name in new[] { First, Second, "4572", "4573" })
        {
            var tags = Tags((name, value));
            var result = Resolve(cards, tags);
            Check(AssembledCardText.RequiresCustomText(tags) && !result.IsResolved && result.EffectiveText is null &&
                AssembledCardText.ReferencedDbfIds(tags).Count == 0 && result.Unknowns.Count > 0,
                "Invalid tags were clamped/defaulted into a native effect or treated as donor IDs.");
        }
        foreach (var tags in new[] { Tags((First, "0"), ("4572", "1")),
            Tags((First, Eyemaul.ToString()), ("4572", Warmedic.ToString()), (Second, "0")),
            Tags((First, "0"), (Second, "0"), ("4573", "unknown")) })
            Check(AssembledCardText.RequiresCustomText(tags) && !Resolve(cards, tags).IsResolved,
                "Conflicting aliases used one arbitrary value or fell back to base text.");
    }

    private static void MissingCards(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var tags = Tags((First, "0"), (Second, Eyemaul.ToString()));
        var missing = cards.ToDictionary(); missing.Remove("BE_349");
        var duplicate = cards.ToDictionary(); duplicate["DUPLICATE"] = cards["BE_349"] with { Id = "DUPLICATE" };
        var wrongIndex = cards.ToDictionary(); wrongIndex.Remove("BE_349"); wrongIndex["WRONG"] = cards["BE_349"];
        foreach (var definitions in new[] { missing, duplicate, wrongIndex })
            Check(Resolve(definitions, tags) is { IsResolved: false, EffectiveText: null, TriggerCardId: null, EffectCardId: null },
                "A missing, ambiguous or misindexed donor fell back to native effects.");
        var badBase = cards.ToDictionary(); badBase["CORE_CFM_753"] = cards["CORE_CFM_753"] with { Id = "OTHER_BASE" };
        Check(!Resolve(badBase, tags).IsResolved, "A mismatched base card identity was accepted.");
    }

    private static void Boundaries(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var text in new[] { "没有分段", "甲&乙&丙" })
        {
            var changed = cards.ToDictionary(); changed["BE_349"] = cards["BE_349"] with { Text = text };
            Check(Resolve(changed, Tags((First, "0"), (Second, Eyemaul.ToString()))) is { IsResolved: false, EffectiveText: null },
                "A missing/ambiguous donor boundary was replaced with base text.");
            var offered = Resolve(changed, Tags((First, "1")), "BE_349");
            Check(offered.IsOffer && offered.OfferedHalf == "Effect" && offered.OfferedHalfText is null && !offered.IsResolved,
                "An offered half's missing boundary was guessed.");
        }
        Check(!Resolve(cards, Tags((First, "0"), (Second, Eyemaul.ToString())), "BE_044").IsResolved,
            "A non-splittable base card was used as a retained half.");
    }

    private static void Bindings(IReadOnlyDictionary<string, CardRecord> cards)
    {
        foreach (var build in new string?[] { null, "", "latest", "255791" })
            Check(AssembledCardText.Resolve(build, "CORE_CFM_753", Tags((First, "0"), (Second, Eyemaul.ToString())), cards)
                is { IsObserved: true, IsResolved: false, EffectiveText: null }, "Unaudited build supplied resolved custom text.");
        foreach (var id in new string?[] { null, "", "MISSING" })
            Check(AssembledCardText.Resolve("255790", id, Tags((First, "0"), (Second, Eyemaul.ToString())), cards)
                is { IsResolved: false, EffectiveText: null }, "Unknown base identity was filled from the donor.");
    }

    private static void Formatting(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var changed = cards.ToDictionary();
        changed["BE_349"] = cards["BE_349"] with { Text = "<b>触发：</b>\n\t& <i>一个</i>\n效果。 " };
        var result = Resolve(changed, Tags((First, "0"), (Second, Eyemaul.ToString())));
        Check(result.EffectiveText == "<b>战吼：</b> <i>一个</i> 效果。", "Half reconstruction lost formatting tags or kept frame line breaks.");
        Check(Resolve(changed, Tags((Second, "1")), "BE_349").OfferedHalfText == "<b>触发：</b>",
            "Offered trigger normalization changed markup or took the effect.");
    }

    private static void Frozen(IReadOnlyDictionary<string, CardRecord> cards)
    {
        var tags = Tags((First, "0"), (Second, Eyemaul.ToString()));
        var result = Resolve(cards, tags); var references = AssembledCardText.ReferencedDbfIds(tags);
        tags[Second] = "1"; tags.Clear();
        Check(result.IsResolved && result.EffectCardId == "BE_349" && result.SecondHalfDbfId == Eyemaul && references.SequenceEqual([Eyemaul]),
            "Caller tag mutations changed an earlier result or database reference list.");
        try { ((IList<int>)references).Clear(); throw new Exception("Mutable donor references."); } catch (NotSupportedException) { }
        try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable assembled text diagnostics."); } catch (NotSupportedException) { }
    }

    private static AssembledTextResolution Resolve(IReadOnlyDictionary<string, CardRecord> cards,
        IReadOnlyDictionary<string, string> tags, string id = "CORE_CFM_753") => AssembledCardText.Resolve("255790", id, tags, cards);
    private static Dictionary<string, string> Tags(params (string Name, string Value)[] values) => values.ToDictionary(value => value.Name, value => value.Value);
    private static IReadOnlyDictionary<string, CardRecord> LoadCards()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assembled-card-text-255790.json")));
        Check(fixture.RootElement.GetProperty("build").GetString() == "255790", "Assembled text fixture build changed.");
        return fixture.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!,
            raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!, raw.GetProperty("text").GetString()!,
            raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToDictionary(card => card.Id);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
