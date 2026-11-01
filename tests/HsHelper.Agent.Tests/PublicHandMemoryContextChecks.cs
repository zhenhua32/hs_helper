using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class PublicHandMemoryContextChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("public hand memory is historical identity separated from hidden current entities and printed definitions", () => Run(Projection));
        yield return ("stale perspective and future public hand memory cannot preload definitions or enter compact context", () => Run(Binding));
        yield return ("snapshot and recent-event tools share safe memory while hidden current effect queries remain denied", Tools);
        yield return ("all task policies distinguish public hand memory from current attributes unknown cards and hindsight", () => Run(Policies));
        yield return ("snapshot coach rejects later caller contexts including their card definitions and event identities", () => Run(ContextBinding));
    }

    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Projection()
    {
        var frame = Frame();
        var context = CompactAdviceContextBuilder.Build(frame, "255790", cards: [Card()]);
        Check(context["public_hand_memory"]!["entries"]![0]!["card_id"]?.ToString() == "PUBLIC_RETURNED" &&
            context["public_hand_memory"]!["entries"]![0]!["public_at_sequence"]!.GetValue<int>() == 10 &&
            context["public_hand_memory"]!["entries"]![0]!["entered_hand_at_sequence"]!.GetValue<int>() == 15 &&
            context["cards"]![0]!["cost"]!.GetValue<int>() == 3,
            "A reliable public return lost identity provenance or its fixed base definition.");
        Check(context["entities"]!.AsArray().All(entity => entity!["id"]!.GetValue<int>() != 40) &&
            frame.Entities[40].CardId is null && frame.Entities[40].Cost is null &&
            !context["public_hand_memory"]!.ToJsonString().Contains("cost", StringComparison.Ordinal) &&
            context["zone_counts"]![0]!["count"]!.GetValue<int>() == 1,
            "Remembered identity fabricated visible hand properties or replaced the aggregate hand count.");
        var partial = frame with { Quality = frame.Quality with { IsComplete = false, IsStable = false },
            PublicHandMemory = new(frame.MatchId, 20, 20, 1, frame.PerspectiveSource, frame.PublicHandMemory!.Entries,
                false, ["缺口后的新公开事实，不补齐先前遗漏。"], isSettled: true) };
        var partialContext = CompactAdviceContextBuilder.Build(partial, "255790", cards: [Card()]);
        Check(partialContext["public_hand_memory"]!["complete"]!.GetValue<bool>() == false &&
            partialContext["public_hand_memory"]!["entries"]!.AsArray().Count == 1,
            "A historical gap permanently suppressed a newly reliable public fact or claimed a complete hand ledger.");
    }

    private static void Binding()
    {
        var frame = Frame();
        var memory = frame.PublicHandMemory!;
        PublicHandMemorySnapshot Ledger(string match, long state, long sequence, int local, string perspective,
            IReadOnlyList<PublicHandMemoryEntry>? entries = null) => new(match, state, sequence, local, perspective,
                entries ?? memory.Entries, true, [], isSettled: true);
        var candidates = new[]
        {
            frame with { PublicHandMemory = Ledger("another-match", 20, 20, 1, frame.PerspectiveSource) },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 21, 20, 1, frame.PerspectiveSource) },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 20, 21, 1, frame.PerspectiveSource) },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 20, 20, 2, frame.PerspectiveSource) },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 20, 20, 1, "different-source") },
            frame with { IsSpectating = true }, frame with { LocalPlayerId = null },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
                [new(40, "PUBLIC_RETURNED", 2, "PLAY", 10, 21)]) },
            frame with { PublicHandMemory = Ledger(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
                [memory.Entries.Single(), memory.Entries.Single()]) }
        };
        foreach (var candidate in candidates)
        {
            var context = CompactAdviceContextBuilder.Build(candidate, "255790", cards: [Card()]);
            Check(context["public_hand_memory"] is null && context["cards"]!.AsArray().Count == 0 &&
                !context.ToJsonString().Contains("PUBLIC_RETURNED", StringComparison.Ordinal),
                "A mismatched or malformed ledger identity entered a compact view or card preloading.");
        }
    }

    private static async Task Tools()
    {
        var path = Path.Combine(Path.GetTempPath(), "hs-helper-public-memory-tests", Guid.NewGuid().ToString("N"));
        var catalog = new CardCatalog(Path.Combine(path, "cards.db")); await catalog.InitializeAsync();
        using var cards = new MemoryStream(Encoding.UTF8.GetBytes("""
            [{"id":"PUBLIC_RETURNED","dbfId":1,"name":"合成公开返回牌","text":"仅用于测试的基础定义","cost":3,"type":"MINION","cardClass":"HUNTER","set":"SYNTHETIC","collectible":false}]
            """));
        await catalog.ImportAsync(cards, "255790", source: "synthetic://public-memory-context-only");
        var frame = Frame();
        var context = CompactAdviceContextBuilder.Build(frame, "255790", cards: [Card()]);
        var coach = new SnapshotCoach(frame, context, catalog, "255790");
        var snapshot = await coach.ExecuteAsync("get_snapshot", new(), CancellationToken.None);
        var events = await coach.ExecuteAsync("get_recent_events", new(), CancellationToken.None);
        var definition = await coach.ExecuteAsync("get_cards", new() { ["card_ids"] = new JsonArray("PUBLIC_RETURNED") }, CancellationToken.None);
        var effect = await coach.ExecuteAsync("get_entity_effect", new() { ["entity_id"] = 40 }, CancellationToken.None);
        Check(JsonNode.DeepEquals(snapshot["public_hand_memory"], context["public_hand_memory"]) &&
            JsonNode.DeepEquals(events["public_hand_memory"], context["public_hand_memory"]) &&
            definition["cards"]![0]!["Cost"]!.GetValue<int>() == 3 && effect["error"]?.ToString().Contains("当前可见", StringComparison.Ordinal) == true,
            "The tools conflated a remembered base identity with current entity visibility.");
        var stale = frame with { StateVersion = 21 };
        var forgedContext = CompactAdviceContextBuilder.Build(stale, "255790");
        forgedContext["public_hand_memory"] = context["public_hand_memory"]!.DeepClone();
        var guarded = new SnapshotCoach(stale, forgedContext, catalog, "255790");
        Check((await guarded.ExecuteAsync("get_snapshot", new(), CancellationToken.None))["public_hand_memory"] is null &&
            (await guarded.ExecuteAsync("get_recent_events", new(), CancellationToken.None))["public_hand_memory"] is null,
            "A supplied stale context bypassed the shared host memory boundary.");
    }

    private static void Policies()
    {
        foreach (var kind in Enum.GetValues<AdviceTaskKind>())
        {
            var policy = AdviceTaskTemplates.Get(kind);
            Check(policy.Instructions.Contains("public_hand_memory", StringComparison.Ordinal) &&
                policy.Instructions.Contains("当前费用、增益、效果或手牌位置", StringComparison.Ordinal) &&
                policy.Instructions.Contains("不得从后来公开信息回填较早复盘", StringComparison.Ordinal) &&
                policy.Version == (kind is AdviceTaskKind.Turn or AdviceTaskKind.Choice ? "hunter-v25/" : "hunter-v7/") + kind.ToString().ToLowerInvariant(),
                "A task treats memory as a current dynamic fact or unbounded hindsight: " + kind);
        }
    }

    private static void ContextBinding()
    {
        var late = Frame() with { RecentEvents = [new(14, "CardRevealed", 40, 2, "PUBLIC_RETURNED", null, "PLAY", "合成公开身份")
            { IdentityVisibility = EventIdentityVisibility.Public }] };
        var context = CompactAdviceContextBuilder.Build(late, "255790", cards: [Card()]);
        Check(context["cards"]!.AsArray().Count == 1 && context["recent_events"]!.AsArray().Count == 1,
            "The source-mismatch probe omitted the later selected definition or public event.");
        var catalog = new CardCatalog(Path.Combine(Path.GetTempPath(), "hs-helper-public-memory-context-binding", Guid.NewGuid().ToString("N"), "unopened.db"));
        foreach (var earlier in new[]
        {
            late with { StateVersion = 9, EventSequence = 9, PublicHandMemory = null },
            late with { MatchId = "other-context-match", PublicHandMemory = null },
            late with { EventSequence = 19, PublicHandMemory = null },
            late with { LocalPlayerId = 2, PublicHandMemory = null }
        })
        {
            try { _ = new SnapshotCoach(earlier, context, catalog, "255790"); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("A mismatched caller context retained later definitions or events beside the authoritative earlier snapshot.");
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
        var frame = new GameSnapshot("public-memory-context", 20, 20, 3, 1, 1, GamePhase.Main,
            rows.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, []))
            { ClientBuild = "255790", PerspectiveSource = "manual" };
        return frame with { PublicHandMemory = new(frame.MatchId, 20, 20, 1, frame.PerspectiveSource,
            [new(40, "PUBLIC_RETURNED", 2, "PLAY", 10, 15)], true, [], isSettled: true) };
    }
    private static CardRecord Card() => new("PUBLIC_RETURNED", 1, "合成公开返回牌", "仅用于测试的基础定义", "HUNTER", "SYNTHETIC",
        "MINION", 3, false, [], [], "{}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
