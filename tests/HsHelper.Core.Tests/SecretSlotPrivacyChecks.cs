using System.Text.Json;
using HsHelper.Core;

internal static class SecretSlotPrivacyChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("opposing visible secret slots expose only public classification without identity or other attributes", () =>
        {
            var frame = Start().Snapshot(); var slot = frame.Entities[30];
            Check(slot.CardId is null && slot.GetIntTag("SECRET") == 1 && slot.GetIntTag("QUEST") == 0 && slot.GetIntTag("SIDE_QUEST") == 0 &&
                slot.Cost is null && slot.Attack is null && !slot.Tags.ContainsKey("CARDTYPE") && !slot.Tags.ContainsKey("PRIVATE_KEY") &&
                !slot.Tags.ContainsKey("219") && !slot.Tags.ContainsKey("462") && !JsonSerializer.Serialize(slot).Contains("PRIVATE_SLOT", StringComparison.Ordinal),
                "Public classification exposed a hidden identity, stats or unnormalized numeric keywords.");
        });
        yield return ("secret classification stays private in opposing hand deck and setaside zones", () =>
        {
            foreach (var zone in new[] { "HAND", "DECK", "SETASIDE" })
            {
                var engine = Start(); Feed(engine, [$"TAG_CHANGE Entity=30 tag=ZONE value={zone}"]);
                var card = engine.Snapshot().Entities[30];
                Check(card.CardId is null && !card.Tags.ContainsKey("SECRET") && !card.Tags.ContainsKey("QUEST") && !card.Tags.ContainsKey("SIDE_QUEST"),
                    "Hidden-zone keywords narrowed the identity of an opponent card.");
            }
        });
        yield return ("public classification projections never cross unconfirmed or spectator perspective caches", () =>
        {
            var engine = Start(null); var unconfirmed = engine.Snapshot();
            Check(!unconfirmed.Entities[30].Tags.ContainsKey("SECRET"), "An unknown perspective exposed private classifying tags.");
            engine.SetLocalPlayerId(1); var confirmed = engine.Snapshot();
            Check(confirmed.Entities[30].GetIntTag("SECRET") == 1 && !unconfirmed.Entities[30].Tags.ContainsKey("SECRET"),
                "Generic hidden cache suppressed new public facts or changed an older frame.");
            engine.SetLocalPlayerId(null);
            Check(!engine.Snapshot().Entities[30].Tags.ContainsKey("SECRET"), "A confirmed projection was reused without a confirmed perspective.");
            engine.SetLocalPlayerId(1); engine.ProcessLine("Begin Spectating");
            Check(engine.Snapshot().IsSpectating && !engine.Snapshot().Entities[30].Tags.ContainsKey("SECRET"), "A spectator inherited private classification from a previous perspective.");
        });
        yield return ("moving a public slot back to hand strips classification while previous frames remain immutable", () =>
        {
            var engine = Start(); var before = engine.Snapshot();
            Feed(engine, ["TAG_CHANGE Entity=30 tag=ZONE value=HAND", "TAG_CHANGE Entity=30 tag=219 value=0"]);
            var hidden = engine.Snapshot();
            Check(!hidden.Entities[30].Tags.ContainsKey("SECRET") && before.Entities[30].GetIntTag("SECRET") == 1,
                "Slot classifications survived return-to-hand or rewrote an older observation.");
            Feed(engine, ["TAG_CHANGE Entity=30 tag=ZONE value=SECRET"]);
            Check(engine.Snapshot().Entities[30].GetIntTag("SECRET") == 0, "Explicit current zero was lost or replaced by an old public-slot cache.");
        });
        yield return ("public quest and sidequest markers distinguish a slot without revealing its private definition", () =>
        {
            var engine = Start(); Feed(engine, ["TAG_CHANGE Entity=30 tag=219 value=0", "TAG_CHANGE Entity=30 tag=462 value=1", "TAG_CHANGE Entity=30 tag=1192 value=1"]);
            var slot = engine.Snapshot().Entities[30];
            Check(slot.GetIntTag("SECRET") == 0 && slot.GetIntTag("QUEST") == 1 && slot.GetIntTag("SIDE_QUEST") == 1 && slot.CardId is null && slot.Cost is null,
                "Public quest classification was omitted, conflated with a Secret, or exposed its definition.");
        });
        yield return ("malformed public classification preserves unknown without exporting arbitrary hidden strings", () =>
        {
            var engine = Start(); Feed(engine, ["TAG_CHANGE Entity=30 tag=219 value=PRIVATE_MALFORMED_KEYWORD"]);
            var slot = engine.Snapshot().Entities[30];
            Check(slot.Tags.GetValueOrDefault("SECRET") == "unknown" && slot.GetIntTag("SECRET") is null &&
                !JsonSerializer.Serialize(slot).Contains("PRIVATE_MALFORMED", StringComparison.Ordinal), "A malformed keyword leaked a private value or became a trusted boolean.");
        });
    }

    private static PowerLogStateEngine Start(int? local = 1)
    {
        var engine = new PowerLogStateEngine(local, "secret-slot-privacy");
        Feed(engine, ["CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=STEP value=MAIN_ACTION", "tag=TURN value=2",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=0", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=1",
            "FULL_ENTITY - Creating ID=30 CardID=PRIVATE_SLOT", "tag=CONTROLLER value=2", "tag=ZONE value=SECRET", "tag=CARDTYPE value=SPELL",
            "tag=219 value=1", "tag=462 value=0", "tag=1192 value=0", "tag=COST value=4", "tag=ATK value=99", "tag=PRIVATE_KEY value=PRIVATE_SLOT_VALUE",
            "tag=ZONE_POSITION value=1", "PowerProcessor.EndCurrentTaskList() - complete"]);
        return engine;
    }
    private static void Feed(PowerLogStateEngine engine, IEnumerable<string> lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
