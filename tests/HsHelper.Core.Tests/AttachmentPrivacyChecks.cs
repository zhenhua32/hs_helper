using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Core;

public static class AttachmentPrivacyChecks
{
    private const string Gift = "EDR_100t13e";
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("attachment source incremental zone type and binding never expose an opposing private gift", Incremental);
        yield return ("attachments inherit opposing HAND DECK SETASIDE privacy while public and own targets remain visible", Zones);
        yield return ("attachment visibility rechecks target zone and perspective without source tag changes", Rebinding);
        yield return ("only a ready actual own choice permits attachment sources across four private option zones", Choice);
        yield return ("unknown unbound chained and cyclic attachment relationships fail closed", Relationships);
        yield return ("numeric attached40 works and conflicting raw aliases do not authorize a source", NumericAndConflict);
        yield return ("attachment events retain only identity actually observed then and honor later perspective", EventProof);
        yield return ("legacy attachment projection masks missing proof without changing original bytes and is idempotent", Legacy);
    }
    private static PowerLogStateEngine Start(int? local = 1)
    {
        var engine = new PowerLogStateEngine(local, "attachment-privacy");
        Feed(engine, "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", "tag=CURRENT_PLAYER value=1", "tag=HERO_ENTITY value=11",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", "tag=CURRENT_PLAYER value=0", "tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05", "tag=CARDTYPE value=HERO", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08", "tag=CARDTYPE value=HERO", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY");
        return engine;
    }
    private static void Target(PowerLogStateEngine engine, int id = 70, string zone = "HAND", int owner = 2, string card = "TARGET_CARD") => Feed(engine,
        $"FULL_ENTITY - Creating ID={id} CardID={card}", "tag=CARDTYPE value=MINION", $"tag=CONTROLLER value={owner}", $"tag=ZONE value={zone}",
        "tag=COST value=1", "tag=ATK value=1", "tag=HEALTH value=1", "tag=DAMAGE value=0");
    private static void Source(PowerLogStateEngine engine, int id = 50, int? target = 70, int owner = 2, bool numeric = false) => Feed(engine,
        $"FULL_ENTITY - Creating ID={id} CardID={Gift}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT",
        $"tag={(numeric ? "40" : "ATTACHED")} value={target ?? 0}");
    private static void Complete(PowerLogStateEngine engine) => engine.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
    private static void Feed(PowerLogStateEngine engine, params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
    private static void Assert(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static void Hidden(GameSnapshot frame, params int[] ids) => Assert(ids.All(id => !frame.Entities.ContainsKey(id)), "A private or unresolved attachment remained in the observer entity view.");
    private static void Visible(GameSnapshot frame, int id = 50) => Assert(frame.Entities.TryGetValue(id, out var entity) && entity.CardId == Gift, "A confirmed observable source was hidden.");

    private static void Incremental()
    {
        var engine = Start(); Target(engine);
        foreach (var line in new[] { $"FULL_ENTITY - Creating ID=50 CardID={Gift}", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=70" })
        {
            engine.ProcessLine(line); var frame = engine.Snapshot();
            Assert(!frame.Entities.Values.Any(entity => entity.CardId == Gift) && !frame.RecentEvents.Any(entry => entry.CardId == Gift), "An incomplete source tag sequence temporarily exposed a private gift.");
        }
        Complete(engine); Hidden(engine.Snapshot(), 50);
    }
    private static void Zones()
    {
        foreach (var zone in new[] { "HAND", "DECK", "SETASIDE" })
        {
            var engine = Start(); Target(engine, zone: zone); Source(engine); Complete(engine);
            var frame = engine.Snapshot(); Hidden(frame, 50); Assert(frame.Entities[70].CardId is null, "Private target identity leaked.");
            Assert(!frame.RecentEvents.Any(entry => entry.CardId == Gift), "Private attachment source identity leaked through a source event.");
        }
        foreach (var (zone, owner) in new[] { ("PLAY", 2), ("HAND", 1), ("SECRET", 1) })
        {
            var engine = Start(); Target(engine, zone: zone, owner: owner); Source(engine, owner: owner); Complete(engine); Visible(engine.Snapshot());
        }
        var hero = Start(); Source(hero, target: 11, owner: 1); Complete(hero); Visible(hero.Snapshot());
    }
    private static void Rebinding()
    {
        var engine = Start(); Target(engine, zone: "PLAY"); Source(engine); Complete(engine);
        var publicFrame = engine.Snapshot(); Visible(publicFrame); var old = JsonSerializer.Serialize(publicFrame);
        Feed(engine, "TAG_CHANGE Entity=70 tag=ZONE value=HAND"); Complete(engine); Hidden(engine.Snapshot(), 50);
        engine.SetLocalPlayerId(2); Visible(engine.Snapshot());
        engine.SetLocalPlayerId(1); Hidden(engine.Snapshot(), 50);
        Feed(engine, "TAG_CHANGE Entity=70 tag=ZONE value=PLAY"); Complete(engine); Visible(engine.Snapshot());
        Assert(old == JsonSerializer.Serialize(publicFrame), "A target or perspective change mutated a previously published snapshot.");
        engine.ProcessLine("Begin Spectating"); Feed(engine, "TAG_CHANGE Entity=70 tag=ZONE value=HAND"); Complete(engine); Hidden(engine.Snapshot(), 50);
    }
    private static void Choice()
    {
        foreach (var zone in new[] { "DECK", "SETASIDE", "REMOVEDFROMGAME", "INVALID" })
        {
            var engine = Start();
            Feed(engine, "FULL_ENTITY - Creating ID=30 CardID=CHOICE_SOURCE", "tag=CARDTYPE value=SPELL", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY");
            Target(engine, zone: zone, owner: 1, card: "CHOICE_A"); Source(engine, owner: 1);
            Feed(engine, "tag=ACCOUNT_ID value=NEVER_PROJECT", "tag=PLAYER_NAME value=NEVER_PROJECT"); Complete(engine); Hidden(engine.Snapshot(), 50);
            Feed(engine, "GameState.DebugPrintEntityChoices() - id=77 Player=1 TaskList= ChoiceType=GENERAL CountMin=1 CountMax=1",
                "GameState.DebugPrintEntityChoices() - Source=[entityName=Anonymous id=30 cardId=CHOICE_SOURCE player=1]",
                $"GameState.DebugPrintEntityChoices() - Entities[0]=[entityName=Anonymous id=70 zone={zone} cardId=CHOICE_A player=1]", "ChoiceCardMgr.OnChoicesShown() - shown");
            var offered = engine.Snapshot(); Assert(offered.Choice is { IsReady: true }, "The fixture did not create an actual complete own choice."); Visible(offered);
            Assert(!offered.Entities[50].Tags.ContainsKey("ACCOUNT_ID") && !offered.Entities[50].Tags.ContainsKey("PLAYER_NAME"), "Own-choice source projection exposed unsafe source tags.");
            // Actual completion uses the same chosen header/row protocol for every observed option zone.
            Feed(engine, "GameState.DebugPrintEntitiesChosen() - id=77 Player=1 EntitiesCount=1", "GameState.DebugPrintEntitiesChosen() - Entities[0]=70", "choice boundary");
            Hidden(engine.Snapshot(), 50); engine.ProcessLine("Begin Spectating"); Hidden(engine.Snapshot(), 50);
        }
    }
    private static void Relationships()
    {
        foreach (var target in new int?[] { null, 999 })
        {
            var engine = Start(); Source(engine, target: target); Complete(engine); var frame = engine.Snapshot(); Hidden(frame, 50);
            Assert(!frame.Quality.IsStable && !frame.Quality.IsComplete, "An unbound/unknown active modifier allowed a complete exact frame.");
        }
        var chain = Start(); Target(chain, zone: "PLAY"); Source(chain, 51, 70); Source(chain, 50, 51); Complete(chain);
        Visible(chain.Snapshot(), 50); Visible(chain.Snapshot(), 51);
        Feed(chain, "TAG_CHANGE Entity=70 tag=ZONE value=DECK"); Complete(chain); Hidden(chain.Snapshot(), 50, 51);
        var cycle = Start(); Source(cycle, 50, 51); Source(cycle, 51, 50); Complete(cycle); Hidden(cycle.Snapshot(), 50, 51);
        Assert(!cycle.Snapshot().Quality.IsStable, "A cyclic active attachment graph became actionable.");
    }
    private static void NumericAndConflict()
    {
        var engine = Start(); Target(engine, zone: "PLAY"); Source(engine, numeric: true); Complete(engine); var frame = engine.Snapshot(); Visible(frame);
        Assert(frame.Entities[50].GetIntTag("ATTACHED") == 70, "Real numeric40 did not normalize to the observed relation.");
        var entities = frame.Entities.ToDictionary(); entities[50] = frame.Entities[50] with { Tags = frame.Entities[50].Tags.Append(new KeyValuePair<string, string>("40", "71")).ToFrozenDictionary() };
        var conflict = AttachmentVisibility.Project(frame with { Entities = entities.ToFrozenDictionary() }); Hidden(conflict, 50);
        Assert(!conflict.Quality.IsComplete, "Conflicting raw relationship aliases were treated as an exact active source.");
    }
    private static void EventProof()
    {
        var engine = Start(); Target(engine, owner: 1); Source(engine, target: null, owner: 2); Complete(engine);
        var unseen = engine.Snapshot(); Assert(!unseen.RecentEvents.Any(entry => entry.EntityId == 50 && entry.CardId is not null), "Unbound or untyped source event gained an identity.");
        Feed(engine, "TAG_CHANGE Entity=50 tag=ATTACHED value=70", "TAG_CHANGE Entity=50 tag=ZONE value=SETASIDE", "TAG_CHANGE Entity=50 tag=ZONE value=PLAY"); Complete(engine);
        var seen = engine.Snapshot(); Assert(seen.RecentEvents.Any(entry => entry.EntityId == 50 && entry.CardId == Gift && entry.IdentityVisibility == EventIdentityVisibility.PlayerOne), "Actual own observation did not retain its contemporaneous proof.");
        Feed(engine, "TAG_CHANGE Entity=70 tag=ZONE value=DECK"); Complete(engine); Hidden(engine.Snapshot(), 50);
        Assert(engine.Snapshot().RecentEvents.Any(entry => entry.EntityId == 50 && entry.CardId == Gift), "A true seen fact was erased merely because its source is currently private.");
        engine.SetLocalPlayerId(2); Assert(!engine.Snapshot().RecentEvents.Any(entry => entry.EntityId == 50 && entry.CardId == Gift), "An old own-only observation crossed perspective.");
        engine.SetLocalPlayerId(1); Assert(!unseen.RecentEvents.Any(entry => entry.EntityId == 50 && entry.CardId == Gift), "Later binding backfilled the immutable early event view.");
        Target(engine, 71, "PLAY", 2); Source(engine, 60, 71); Feed(engine, "TAG_CHANGE Entity=60 tag=ZONE value=SETASIDE", "TAG_CHANGE Entity=60 tag=ZONE value=PLAY"); Complete(engine);
        engine.SetLocalPlayerId(null); Assert(engine.Snapshot().RecentEvents.Any(entry => entry.EntityId == 60 && entry.CardId == Gift && entry.IdentityVisibility == EventIdentityVisibility.Public), "Actual public facts were hidden from an unconfirmed observer.");
    }
    private static void Legacy()
    {
        var engine = Start(); Target(engine, zone: "PLAY"); Source(engine); Complete(engine); var original = engine.Snapshot();
        var entities = original.Entities.ToDictionary(); entities[70] = new(70, null, EntityKind.Card, new Dictionary<string, string> { ["CONTROLLER"] = "2", ["ZONE"] = "HAND" }.ToFrozenDictionary());
        var raw = original with { Entities = entities.ToFrozenDictionary(), RecentEvents = [
            new(original.EventSequence, "ZoneChanged", 50, 2, Gift, "SETASIDE", "PLAY", "Synthetic legacy row"),
            new(original.EventSequence, "ZoneChanged", 50, 2, Gift, "SETASIDE", "PLAY", "Actually public row") { IdentityVisibility = EventIdentityVisibility.Public }] };
        var before = JsonSerializer.Serialize(raw); var projected = AttachmentVisibility.Project(raw); Hidden(projected, 50);
        Assert(projected.RecentEvents[0].CardId is null && projected.RecentEvents[1].CardId == Gift, "Legacy missing proof was trusted, or explicit true public proof was erased.");
        Assert(before == JsonSerializer.Serialize(raw) && JsonSerializer.Serialize(projected) == JsonSerializer.Serialize(AttachmentVisibility.Project(projected)), "Projection mutated original bytes or was not idempotent.");
        var missing = raw with { Entities = raw.Entities.Where(pair => pair.Key != 50).ToFrozenDictionary() };
        var missingBefore = JsonSerializer.Serialize(missing); var missingProjection = AttachmentVisibility.Project(missing);
        Assert(missingProjection.RecentEvents[0].CardId is null && missingProjection.RecentEvents[1].CardId == Gift &&
            missingBefore == JsonSerializer.Serialize(missing), "A legacy row whose attachment actor is missing gained an identity without proof, or its genuine public proof was erased.");
    }
}
