using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class DirectHandRevealContextChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("direct hand context distinguishes closed history disclosure from a return and preloads only public base definitions", () => Run(Projection));
        yield return ("future missing and contradictory direct disclosure evidence cannot preload target or historical source identities", () => Run(Invalid));
        yield return ("initial lazy and recent-event tools agree on closed disclosure while current hidden effects stay unavailable", Tools);
        yield return ("all task policies use confirmation time and do not turn metadata anchors into earlier public knowledge", () => Run(Policies));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Projection()
    {
        var frame = Frame(); var context = CompactAdviceContextBuilder.Build(frame, "255790", cards: [Card("DIRECT_PUBLIC"), Card("PUBLIC_SOURCE")]);
        var entry = context["public_hand_memory"]!["entries"]![0]!;
        Check(entry["kind"]?.ToString() == "history_target" && entry["from"]?.ToString() == "HAND" &&
            entry["entered_hand_at_sequence"] is null && entry["public_at_sequence"]!.GetValue<long>() == 10 &&
            entry["direct_reveal"]!["show_at_sequence"]!.GetValue<long>() == 7 &&
            entry["direct_reveal"]!["history_target_at_sequence"]!.GetValue<long>() == 8 &&
            entry["direct_reveal"]!["confirmed_at_sequence"]!.GetValue<long>() == 10 &&
            entry["direct_reveal"]!["source_public_at_sequence"]!.GetValue<long>() == 6,
            "Direct disclosure was labeled as an earlier public hand arrival or lost its separate confirmation.");
        Check(context["cards"]!.AsArray().Select(card => card!["id"]!.ToString()).Order(StringComparer.Ordinal).SequenceEqual(["DIRECT_PUBLIC", "PUBLIC_SOURCE"]) &&
            context["entities"]!.AsArray().All(entity => entity!["id"]!.GetValue<int>() is not (40 or 50)) &&
            frame.Entities[40].CardId is null && frame.Entities[40].Cost is null,
            "Public target/source base definitions were lost or promoted to current entities and attributes.");
    }

    private static void Invalid()
    {
        var frame = Frame(); var entry = frame.PublicHandMemory!.Entries.Single(); var proof = entry.DirectReveal!;
        foreach (var invalid in new[]
        {
            entry with { DirectReveal = null }, entry with { EnteredHandAtSequence = 9 },
            entry with { DirectReveal = proof with { SourcePublicAtSequence = 0 } },
            entry with { DirectReveal = proof with { ConfirmedAtSequence = 21 }, PublicAtSequence = 21 },
            entry with { DirectReveal = proof with { SourceCardType = "ENCHANTMENT" } },
            entry with { DirectReveal = proof with { ClientBuild = "different-build" } }
        })
        {
            var candidate = frame with { PublicHandMemory = new(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
                [invalid], true, [], true, PublicHandMemoryVisibility.CurrentContinuityPolicy) };
            var context = CompactAdviceContextBuilder.Build(candidate, "255790", cards: [Card("DIRECT_PUBLIC"), Card("PUBLIC_SOURCE")]);
            Check(context["public_hand_memory"] is null && context["cards"]!.AsArray().Count == 0 &&
                !context.ToJsonString().Contains("DIRECT_PUBLIC", StringComparison.Ordinal) &&
                !context.ToJsonString().Contains("PUBLIC_SOURCE", StringComparison.Ordinal),
                "An unconfirmed or contradictory disclosure selected private identity definitions.");
        }
    }

    private static async Task Tools()
    {
        var catalog = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-direct-hand-context", Guid.NewGuid().ToString("N"), "cards.db"));
        await catalog.InitializeAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            [{"id":"DIRECT_PUBLIC","dbfId":31,"name":"合成展示牌","text":"仅用于测试的基础定义","cost":2,"type":"MINION","cardClass":"HUNTER","set":"SYNTHETIC","collectible":false},
             {"id":"PUBLIC_SOURCE","dbfId":32,"name":"合成公开来源","text":"仅用于测试的基础定义","cost":3,"type":"SPELL","cardClass":"HUNTER","set":"SYNTHETIC","collectible":false}]
            """));
        await catalog.ImportAsync(stream, "255790", source: "synthetic://direct-hand-context-only");
        var frame = Frame(); var context = CompactAdviceContextBuilder.Build(frame, "255790", cards: [Card("DIRECT_PUBLIC"), Card("PUBLIC_SOURCE")]);
        var coach = new SnapshotCoach(frame, context, catalog, "255790");
        var lazy = await coach.ExecuteAsync("get_snapshot", new(), default);
        var recent = await coach.ExecuteAsync("get_recent_events", new(), default);
        Check(JsonNode.DeepEquals(context["public_hand_memory"], lazy["public_hand_memory"]) &&
            JsonNode.DeepEquals(context["public_hand_memory"], recent["public_hand_memory"]),
            "Initial and tool views disagreed about the same confirmation boundary.");
        var definitions = await coach.ExecuteAsync("get_cards", new() { ["card_ids"] = new JsonArray("DIRECT_PUBLIC", "PUBLIC_SOURCE") }, default);
        Check(definitions["cards"]!.AsArray().Count == 2, "The fixed public base definitions became inaccessible.");
        foreach (var id in new[] { 40, 50 })
            Check((await coach.ExecuteAsync("get_entity_effect", new() { ["entity_id"] = id }, default))["error"] is not null,
                "A historical identity or source became a current visible effect entity.");
    }

    private static void Policies()
    {
        foreach (var kind in Enum.GetValues<AdviceTaskKind>())
        {
            var policy = AdviceTaskTemplates.Get(kind);
            Check(policy.Version == (kind is AdviceTaskKind.Turn or AdviceTaskKind.Choice ? "hunter-v25/" : kind == AdviceTaskKind.DeckPlan ? "hunter-v8/" : "hunter-v7/") + kind.ToString().ToLowerInvariant() &&
                policy.Instructions.Contains("kind=history_target", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不表示该牌回手", StringComparison.Ordinal) &&
                policy.Instructions.Contains("处理事件锚点", StringComparison.Ordinal) &&
                policy.Instructions.Contains("confirmed_at_sequence/public_at_sequence", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不得从后来公开信息回填较早复盘", StringComparison.Ordinal),
                "A task treated private SHOW time as immediate public knowledge: " + kind);
        }
    }

    private static GameSnapshot Frame()
    {
        var rows = new[]
        {
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            new EntitySnapshot(40, null, EntityKind.Card, new Dictionary<string,string> { ["ZONE"] = "HAND", ["CONTROLLER"] = "2" }.ToFrozenDictionary())
        };
        var frame = new GameSnapshot("direct-hand-context", 20, 20, 3, 1, 1, GamePhase.Main,
            rows.ToFrozenDictionary(row => row.Id), new(true, true, true, true, 0, []))
            { ClientBuild = "255790", PerspectiveSource = "manual" };
        var proof = new PublicHandDirectRevealEvidence("255790", 7, 8, 5, 10, 50, "PUBLIC_SOURCE", 1, "SPELL", "GRAVEYARD", "POWER", 6);
        return frame with { PublicHandMemory = new(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
            [new(40, "DIRECT_PUBLIC", 2, "HAND", 10, null) { DirectReveal = proof }], true, [], true, PublicHandMemoryVisibility.CurrentContinuityPolicy) };
    }
    private static CardRecord Card(string id) => new(id, id == "DIRECT_PUBLIC" ? 31 : 32, id == "DIRECT_PUBLIC" ? "合成展示牌" : "合成公开来源",
        "仅用于测试的基础定义", "HUNTER", "SYNTHETIC", id == "DIRECT_PUBLIC" ? "MINION" : "SPELL", id == "DIRECT_PUBLIC" ? 2 : 3, false, [], [], "{}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
