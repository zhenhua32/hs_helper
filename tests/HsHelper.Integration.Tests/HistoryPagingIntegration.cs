using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Storage;

internal static class HistoryPagingIntegration
{
    public static async Task RunAsync(string root, Action<string> pass)
    {
        var directory = Path.Combine(root, "history-paging");
        Directory.CreateDirectory(directory);
        var store = new SessionStore(Path.Combine(directory, "history.db"));
        await store.InitializeAsync();
        var deck = await store.SaveDeckVersionAsync("分页验收套牌", new DeckDefinition(2, [31], [new(100, 2)], []), "paging-fixture");
        var recorder = new SessionRecorder(store, new(deck.Id, "paging-fixture", 1, PlayerClass: "HUNTER"));
        for (var index = 0; index < 103; index++)
        {
            var id = "paging-integration-" + index.ToString("D3");
            recorder.Observe(Frame(id, 10, 1, false), index * 2 + 1);
            recorder.Observe(Frame(id, 20, 9, true), index * 2 + 2);
        }
        await recorder.CompleteAsync();
        var filter = new SessionFilter(CardBuild: "paging-fixture", DeckVersionId: deck.Id, Format: "STANDARD", Region: "CN", PlayerClass: "HUNTER", GameType: "GT_RANKED");
        var first = await store.ListSessionPageAsync(filter);
        var second = await store.ListSessionPageAsync(filter, pageNumber: 2);
        Check(first.Sessions.Count == 100 && first.HasMore && !first.HasPrevious && second.Sessions.Count == 3 && !second.HasMore && second.HasPrevious &&
            first.Sessions.Concat(second.Sessions).Select(session => session.Id).Distinct().Count() == 103,
            "A saved session beyond the default first hundred is unreachable or duplicated by paging.");
        var old = second.Sessions.Last();
        Check((await store.GetStatsAsync(filter)).Games == 103 && (await store.ListSessionsAsync(filter)).Count == 100,
            "Paging changed the existing list contract or made statistics count only one page.");
        pass("recorded histories beyond one hundred sessions remain reachable across bounded pages while statistics include every match");

        var points = await store.ListDecisionPointsAsync(old.Id, includeUnstable: true);
        var point = points.Single(item => item.EventSequence == 10);
        var historical = await store.GetHistoricalContextAtAsync(old.Id, point.EventSequence);
        var exact = await store.GetDecisionSnapshotAsync(point.Id);
        Check(historical is { DeckVersionId: not null } && historical.DeckVersionId == deck.Id && historical.Deck?.Id == deck.Id &&
            historical.Snapshot.MatchId == old.Id && historical.Snapshot.EventSequence == 10 && historical.Snapshot.Hand.Single().Cost == 1 &&
            exact?.MatchId == old.Id && exact.EventSequence == 10 && exact.Hand.Single().Cost == 1 &&
            historical.RecentEvents.All(item => item.EventSequence <= 10),
            "Opening a later history page selected the wrong session/deck or leaked its final state into an earlier review.");
        pass("a match selected from an older page opens its own fixed deck and earlier decision without later-state information");

        var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db"));
        await archive.InitializeAsync();
        var saved = await archive.SaveAsync(new AdviceRecordDraft(old.Id, 10, 10, "paging-fixture", "paging-fixture/review", "较早一局的当时建议")
            { DeckVersionId = deck.Id, Task = "局面复盘", Verification = "strategy" });
        await archive.SaveAsync(new AdviceRecordDraft(first.Sessions[0].Id, 10, 10, "paging-fixture", "paging-fixture/review", "另一局的建议")
            { DeckVersionId = deck.Id, Task = "局面复盘", Verification = "strategy" });
        var restored = await archive.ListAsync(new AdviceArchiveFilter(MatchId: old.Id));
        Check(restored.Count == 1 && restored[0].Id == saved.Id && restored[0].Advice.DeckVersionId == deck.Id && restored[0].Advice.EventSequence == point.EventSequence,
            "An older-page match opened another match's advice archive.");
        Check((await store.ListSessionPageAsync(filter, pageNumber: 1, pageSize: 7)).Sessions.Count == 7 &&
            (await store.ListSessionPageAsync(filter with { OpponentClass = "WARRIOR" })).Sessions.Count == 0,
            "Changing page size or filters reused rows from the previous history query.");
        pass("older-page matches retain advice archive bindings and a changed history filter starts from its own result set");
    }

    private static GameSnapshot Frame(string id, long sequence, int cost, bool ended)
    {
        EntitySnapshot Entity(int entityId, string? cardId, EntityKind kind, params (string Key, string Value)[] tags) =>
            new(entityId, cardId, kind, tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value));
        var entities = new[]
        {
            Entity(1, null, EntityKind.Game, ("STATE", ended ? "COMPLETE" : "RUNNING"), ("TURN", "1")),
            Entity(2, null, EntityKind.Player, ("PLAYER_ID", "1"), ("HERO_ENTITY", "10"), ("FIRST_PLAYER", "1"), ("PLAYSTATE", ended ? "WON" : "PLAYING")),
            Entity(3, null, EntityKind.Player, ("PLAYER_ID", "2"), ("HERO_ENTITY", "20"), ("PLAYSTATE", ended ? "LOST" : "PLAYING")),
            Entity(10, "HERO_05", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", "HUNTER"), ("HEALTH", "30"), ("DAMAGE", "0")),
            Entity(20, "HERO_08", EntityKind.Card, ("CONTROLLER", "2"), ("ZONE", "PLAY"), ("CARDTYPE", "HERO"), ("CLASS", "MAGE"), ("HEALTH", "30"), ("DAMAGE", "0")),
            Entity(30, "PAGING_VISIBLE_CARD", EntityKind.Card, ("CONTROLLER", "1"), ("ZONE", "HAND"), ("CARDTYPE", "SPELL"), ("COST", cost.ToString()))
        };
        return new(id, sequence, sequence, 1, 1, 1, ended ? GamePhase.Complete : GamePhase.Main,
            entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "paging-fixture", Format = "STANDARD", GameType = "GT_RANKED" };
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
