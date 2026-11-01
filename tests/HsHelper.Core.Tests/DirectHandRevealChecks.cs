using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Core;

public static class DirectHandRevealChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("history hand identity is staged through every ancestor and never backfills earlier frames", ClosedHistory);
        yield return ("history target and SHOW may appear in either order within the same block", BothOrders);
        yield return ("a played hand source must actually become public in the same identity epoch", PlayedSource);
        yield return ("audited battlecry and deathrattle keywords retain explicit closed history proof", OrdinaryKeywords);
        yield return ("unambiguous numeric and partial source references use actual public observations", NumericSources);
        yield return ("known public game and player carriers preserve nested card proof without becoming identity sources", PublicCarriers);
        yield return ("recreated entity IDs cannot publish a new private identity through the previous public pose", RecreatedIdentityPrivacy);
        yield return ("SHOW and FULL headers apply explicit hidden pose before publishing the new identity", HeaderPosePrivacy);
        yield return ("explicit public SHOW poses preserve hand plays return memory and frozen turn history", PublicHeaderPlays);
        yield return ("history info explicit zone counterevidence invalidates the complete metadata scope", HistoryZoneCounterevidence);
        yield return ("numeric history targets cannot cross a known identity change before their first SHOW", TargetIdentityEpoch);
        yield return ("a changed SHOW identity cannot inherit an old ordinary category to expose a private attachment", ChangedShowPrivacy);
        yield return ("private or contradictory block descriptors do not prove a public source", SourceEvidence);
        yield return ("late override startgame joust and unknown scopes deny only direct reveal eligibility", HiddenScopes);
        yield return ("history metadata requires complete unique indexed entity information", MetaCompleteness);
        yield return ("bare SHOW reveal forge prepare zone markers and creation never authorize a hand identity", PrivateMarkers);
        yield return ("history info must match the same actual entity identity and owner", TargetEvidence);
        yield return ("direct hand candidates are lost on reconstruction control category and input boundaries", Invalidations);
        yield return ("source reconstruction and ownership changes invalidate pending history proof", SourceInvalidations);
        yield return ("confirmed direct identities survive repeated hide and retain same-card entity multiplicity", HiddenMultiplicity);
        yield return ("direct history proof requires audited build main phase and confirmed observer", ScopeBoundaries);
        yield return ("direct consumer evidence is validated and old return JSON shape stays unchanged", ConsumerAndJson);
        yield return ("metadata staging preserves existing state event and turn history numbering", MetadataNumbering);
    }

    private static PowerLogStateEngine Start(string? build = "255790", int? local = 1, string step = "MAIN_ACTION")
    {
        var engine = new PowerLogStateEngine(local, "direct-hand");
        if (build is not null) Feed(engine, $"GameState.DebugPrintGame() - BuildNumber={build}");
        Feed(engine, "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", $"tag=STEP value={step}",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0");
        Card(engine, 30, "SOURCE_CARD", "PLAY"); Card(engine, 41, "", "HAND"); Complete(engine); return engine;
    }
    private static void Card(PowerLogStateEngine engine, int id, string card, string zone, int owner = 2, string type = "MINION") =>
        Feed(engine, $"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", $"tag=CARDTYPE value={type}",
            $"tag=ZONE value={zone}", "tag=COST value=0", "tag=ATK value=99");
    private static string Block(string type = "TRIGGER", string card = "SOURCE_CARD", int owner = 2, string zone = "PLAY", string suffix = "") =>
        $"BLOCK_START BlockType={type} Entity=[entityName=source id=30 zone={zone} zonePos=1 cardId={card} player={owner}] EffectCardId= EffectIndex=0 Target=0 SubOption=-1{suffix}";
    private static void Show(PowerLogStateEngine engine, int id = 41, string card = "DIRECT_CARD") => Feed(engine, $"SHOW_ENTITY - Updating Entity={id} CardID={card}");
    private static void Meta(PowerLogStateEngine engine, int id = 41) => Feed(engine, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", $"Info[0] = {id}");
    private static void Feed(PowerLogStateEngine engine, params string[] lines)
    { foreach (var line in lines) { engine.ProcessLine(line); _ = engine.Snapshot(); } }
    private static void Complete(PowerLogStateEngine engine) => Feed(engine, "PowerProcessor.EndCurrentTaskList() - complete");
    private static PublicHandMemorySnapshot Memory(PowerLogStateEngine engine) => PublicHandMemoryVisibility.Confirmed(engine.Snapshot()) ?? throw new Exception("Expected settled memory.");
    private static void Empty(PowerLogStateEngine engine) { Complete(engine); Check(Memory(engine).Entries.Count == 0, "An unproven direct identity escaped staging."); }
    private static void Reveal(PowerLogStateEngine engine) { Feed(engine, Block()); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Complete(engine); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void ClosedHistory()
    {
        var engine = Start(); var frames = new List<GameSnapshot> { engine.Snapshot() };
        foreach (var line in new[] { Block(), Block(), "SHOW_ENTITY - Updating Entity=41 CardID=DIRECT_CARD",
            "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = 41", "BLOCK_END" })
        { engine.ProcessLine(line); frames.Add(engine.Snapshot()); }
        Check(frames.All(frame => frame.PublicHandMemory!.Entries.Count == 0), "A pending child proof appeared in a prior snapshot.");
        var json = frames.Select(frame => JsonSerializer.Serialize(frame)).ToArray();
        Feed(engine, "BLOCK_END"); var confirmedAt = engine.Snapshot().EventSequence;
        Check(PublicHandMemoryVisibility.Confirmed(engine.Snapshot()) is null, "Root closure skipped the task settlement boundary.");
        Complete(engine); var entry = Memory(engine).Entries.Single(); var evidence = entry.DirectReveal!;
        Check(entry.FromZone == "HAND" && entry.EnteredHandAtSequence is null && evidence is not null &&
            entry.PublicAtSequence == confirmedAt && evidence.ConfirmedAtSequence == confirmedAt && evidence.SourceEntityId == 30 &&
            evidence.SourceCardId == "SOURCE_CARD" && evidence.SourceZone == "PLAY", "Direct proof was represented as a public-zone return.");
        Check(frames.Select(frame => JsonSerializer.Serialize(frame)).SequenceEqual(json), "Closing a later block rewrote an earlier frame.");
        Check(engine.Snapshot().Entities[41].CardId is null && engine.Snapshot().Entities[41].GetIntTag("COST") is null &&
            engine.Snapshot().RecentEvents.All(item => item.EntityId != 41 || item.CardId is null), "History proof declassified hidden entities or events.");
    }
    private static void BothOrders()
    {
        foreach (var first in new[] { true, false })
        {
            var engine = Start(); Feed(engine, Block());
            if (first) { Meta(engine); Show(engine); } else { Show(engine); Meta(engine); }
            Feed(engine, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Single().DirectReveal is not null, "A valid order lost same-block history proof.");
        }
        foreach (var hideFirst in new[] { true, false })
        {
            var engine = Start(); Feed(engine, Block());
            if (hideFirst) { Show(engine); Feed(engine, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND"); }
            Feed(engine, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = [id=41 cardId=DIRECT_CARD player=2]");
            if (!hideFirst) Show(engine);
            Feed(engine, "BLOCK_END"); Complete(engine); Check(Memory(engine).Entries.Single().DirectReveal is not null, "A descriptor was used as proof or rejected despite a matching actual SHOW.");
        }
        var continuation = Start(); Feed(continuation, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", Block()); Show(continuation);
        Feed(continuation, "tag=CARDTYPE value=MINION", "tag=ATTACHED value=0"); Meta(continuation); Feed(continuation, "BLOCK_END"); Complete(continuation);
        Check(Memory(continuation).Entries.Count == 1, "SHOW continuation did not establish the actual ordinary category.");
    }
    private static void PlayedSource()
    {
        foreach (var becomesPublic in new[] { true, false })
        {
            var engine = Start(); Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE value=HAND", Block("PLAY", zone: "HAND"));
            Show(engine); Meta(engine);
            if (becomesPublic) Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE value=PLAY");
            Feed(engine, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Any(entry => entry.EntityId == 41) == becomesPublic, "The source descriptor was treated as public proof or actual played-source proof was lost.");
        }
        var hidden = Start(); Card(hidden, 30, "", "HAND"); Feed(hidden, Block("PLAY", zone: "HAND"));
        Show(hidden); Meta(hidden); Show(hidden, 30, "SOURCE_CARD"); Feed(hidden, "TAG_CHANGE Entity=30 tag=ZONE value=PLAY", "BLOCK_END"); Complete(hidden);
        var proof = Memory(hidden).Entries.Single().DirectReveal!;
        Check(proof.SourcePublicAtSequence > proof.BlockStartedAtSequence, "A newly played opponent source never gained actual public proof or used its descriptor as proof.");
        var privateSource = Start(); Card(privateSource, 30, "", "HAND"); Feed(privateSource, Block("PLAY", zone: "HAND"));
        Show(privateSource); Meta(privateSource); Show(privateSource, 30, "SOURCE_CARD"); Feed(privateSource, "BLOCK_END"); Empty(privateSource);
    }
    private static void SourceEvidence()
    {
        foreach (var block in new[] { Block(card: "DIFFERENT"), Block(owner: 1), Block(zone: "HAND"), "BLOCK_START BlockType=TRIGGER Entity=30" })
        { var engine = Start(); Feed(engine, block); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Empty(engine); }
        foreach (var type in new[] { "ENCHANTMENT", "UNKNOWN" })
        { var engine = Start(); Card(engine, 30, "SOURCE_CARD", "PLAY", type: type); Feed(engine, Block()); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Empty(engine); }
    }
    private static string ReferenceBlock(string reference, string suffix = "") =>
        $"BLOCK_START BlockType=POWER Entity={reference} EffectCardId= EffectIndex=0 Target=0 SubOption=-1{suffix}";
    private static void PrivateFrame(PowerLogStateEngine engine, string token)
    {
        var frame = engine.Snapshot();
        Check(!JsonSerializer.Serialize(frame).Contains(token, StringComparison.Ordinal),
            "A new private identity appeared in a snapshot entity, event or hand-memory field through an old public pose.");
    }
    private static void RecreatedIdentityPrivacy()
    {
        var engine = Start(); Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE_POSITION value=7"); var old = engine.Snapshot();
        foreach (var line in new[] { "FULL_ENTITY - Creating ID=30 CardID=NEW_PRIVATE_RECREATION", "tag=CONTROLLER value=2",
            "tag=CARDTYPE value=MINION", "tag=ZONE value=HAND", "tag=COST value=0", "PowerProcessor.EndCurrentTaskList() - complete" })
        { Feed(engine, line); PrivateFrame(engine, "NEW_PRIVATE_RECREATION"); }
        var current = engine.Snapshot();
        Check(Memory(engine).Entries.Count == 0 && current.Entities[30].GetIntTag("ZONE_POSITION") is null &&
            current.Entities[30].GetIntTag("ATK") is null && old.Entities[30].CardId == "SOURCE_CARD",
            "Recreation inherited old position, dynamic values or identity history into a new entity observation.");
        Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE value=PLAY");
        Check(engine.Snapshot().Entities[30].CardId == "NEW_PRIVATE_RECREATION" && engine.Snapshot().Entities[30].GetIntTag("ATK") is null &&
            engine.Snapshot().Entities[30].GetIntTag("ZONE_POSITION") is null, "A later actual public zone resurrected pre-creation attributes or position.");
        Feed(engine, "FULL_ENTITY - Creating ID=30 CardID=NEW_PUBLIC_RECREATION", "tag=CONTROLLER value=2",
            "tag=CARDTYPE value=MINION", "tag=ZONE value=PLAY", "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND"); Complete(engine);
        Check(Memory(engine).Entries.Single().CardId == "NEW_PUBLIC_RECREATION", "Actual new public creation could not establish a later reliable return.");
    }
    private static void HeaderPosePrivacy()
    {
        foreach (var operation in new[] { "SHOW_ENTITY", "FULL_ENTITY" })
            foreach (var wasOwnHand in new[] { false, true })
            {
                var engine = Start();
                if (wasOwnHand) Card(engine, 30, "OLD_OWN_HAND", "HAND", owner: 1);
                var descriptor = wasOwnHand ? "[id=30 player=2]" : "[id=30 zone=HAND zonePos=1 player=2]";
                foreach (var line in new[] { $"{operation} - Updating Entity={descriptor} CardID=NEW_PRIVATE_HEADER",
                    "tag=CARDTYPE value=MINION", "tag=CONTROLLER value=2", "tag=ZONE value=HAND", "PowerProcessor.EndCurrentTaskList() - complete" })
                { Feed(engine, line); PrivateFrame(engine, "NEW_PRIVATE_HEADER"); }
                Check(Memory(engine).Entries.Count == 0 && engine.Snapshot().Entities[30].ControllerId == 2 &&
                    engine.Snapshot().Entities[30].Zone == "HAND", "Explicit header counterevidence was discarded or became a public return proof.");
            }
        foreach (var descriptor in new[] { "[id=30 zone=HAND zone=PLAY player=2]", "[id=30 zone=HAND player=1 player=2]", "[id=30 zone=HAND player=UNKNOWN]" })
        {
            var engine = Start(); Feed(engine, $"SHOW_ENTITY - Updating Entity={descriptor} CardID=NEW_PRIVATE_AMBIGUOUS");
            PrivateFrame(engine, "NEW_PRIVATE_AMBIGUOUS");
        }
    }
    private static void PublicHeaderPlays()
    {
        var engine = Start(); Card(engine, 60, "OWN_PLAY", "HAND", owner: 1);
        var plays = new PublicPlayTracker(); plays.Observe(engine.Snapshot());
        foreach (var line in new[] {
            "BLOCK_START BlockType=PLAY Entity=[id=60 zone=HAND cardId=OWN_PLAY player=1] EffectCardId= EffectIndex=0 Target=0 SubOption=-1",
            "SHOW_ENTITY - Updating Entity=[id=60 zone=PLAY zonePos=1 player=1] CardID=OWN_PLAY", "tag=ZONE value=PLAY", "BLOCK_END",
            "PowerProcessor.EndCurrentTaskList() - complete" })
        { Feed(engine, line); plays.Observe(engine.Snapshot()); }
        Check(engine.Snapshot().Entities[60].CardId == "OWN_PLAY" &&
            plays.Snapshot().Plays.Count(play => play.EntityId == 60 && play.CardId == "OWN_PLAY") == 1 &&
            engine.Snapshot().TurnHistory!.Turns.SelectMany(turn => turn.Plays).Single(play => play.AtPlay.Id == 60).AtPlay.Zone == "HAND",
            "A header-observed public play lost its actual transition, duplicated it or rewrote the frozen hand boundary.");
        Feed(engine, "SHOW_ENTITY - Updating Entity=[id=30 zone=PLAY player=2] CardID=SOURCE_CARD");
        Check(engine.Snapshot().Entities[30].CardId == "SOURCE_CARD", "A same-definition ordinary SHOW lost its already observed public category.");
        Feed(engine, "SHOW_ENTITY - Updating Entity=[id=30 zone=PLAY player=2] CardID=CONFIRMED_PUBLIC_SHOW", "tag=CARDTYPE value=MINION");
        Check(engine.Snapshot().Entities[30].CardId == "CONFIRMED_PUBLIC_SHOW", "Normal actual public SHOW was incorrectly concealed.");
        Feed(engine, "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND"); Complete(engine);
        Check(Memory(engine).Entries.Single().CardId == "CONFIRMED_PUBLIC_SHOW", "A normal public SHOW could no longer establish reliable returned identity.");
    }
    private static void HistoryZoneCounterevidence()
    {
        foreach (var zone in new[] { "DECK", "SETASIDE", "PLAY", "GRAVEYARD", "", "HAND zone=DECK" })
        {
            var engine = Start(); Feed(engine, Block()); Show(engine);
            Feed(engine, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", $"Info[0] = [id=41 zone={zone} cardId=DIRECT_CARD player=2]", "BLOCK_END");
            Empty(engine);
        }
        var group = Start(); Card(group, 42, "", "HAND"); Feed(group, Block()); Show(group); Show(group, 42, "SECOND_CARD");
        Feed(group, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=2", "Info[0] = [id=41 zone=HAND cardId=DIRECT_CARD player=2]",
            "Info[1] = [id=42 zone=DECK cardId=SECOND_CARD player=2]", "BLOCK_END"); Empty(group);
        foreach (var info in new[] { "41", "[id=41 cardId=DIRECT_CARD player=2]", "[id=41 zone=HAND cardId=DIRECT_CARD player=2]" })
        {
            var engine = Start(); Feed(engine, Block()); Show(engine);
            Feed(engine, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = " + info, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Single().CardId == "DIRECT_CARD", "Numeric absent-zone or matching actual hand metadata was incorrectly denied.");
        }
    }
    private static void TargetIdentityEpoch()
    {
        foreach (var hide in new[] { false, true })
        {
            var engine = Start(); Card(engine, 41, "PRIVATE_A", "HAND"); Feed(engine, Block()); Meta(engine);
            if (hide) Feed(engine, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND");
            Show(engine, card: "PRIVATE_B"); Feed(engine, "tag=CARDTYPE value=MINION", "BLOCK_END"); Empty(engine);
        }
        var unchanged = Start(); Card(unchanged, 41, "PRIVATE_A", "HAND"); Feed(unchanged, Block()); Meta(unchanged); Show(unchanged, card: "PRIVATE_A");
        Feed(unchanged, "BLOCK_END"); Complete(unchanged); Check(Memory(unchanged).Entries.Single().CardId == "PRIVATE_A", "Same-identity target-first evidence was lost.");
        var fresh = Start(); Card(fresh, 41, "PRIVATE_A", "HAND"); Feed(fresh, Block()); Meta(fresh); Show(fresh, card: "PRIVATE_B");
        Feed(fresh, "tag=CARDTYPE value=MINION"); Meta(fresh); Feed(fresh, "BLOCK_END"); Complete(fresh);
        Check(Memory(fresh).Entries.Single().CardId == "PRIVATE_B", "A new actual history target after the identity change could not establish fresh evidence.");
    }
    private static void ChangedShowPrivacy()
    {
        var engine = Start(); Feed(engine, "TAG_CHANGE Entity=30 tag=ATTACHED value=0"); var old = engine.Snapshot();
        foreach (var line in new[] { "SHOW_ENTITY - Updating Entity=[id=30 zone=PLAY cardId=PRIVATE_ENCH player=2] CardID=PRIVATE_ENCH",
            "tag=CARDTYPE value=ENCHANTMENT", "tag=ATTACHED value=41", "PowerProcessor.EndCurrentTaskList() - complete" })
        { Feed(engine, line); PrivateFrame(engine, "PRIVATE_ENCH"); }
        Check(old.Entities[30].CardId == "SOURCE_CARD" && engine.Snapshot().PublicHandMemory!.Entries.Count == 0,
            "A changed private attachment altered already public history or supplied a public return source.");
        var same = Start(); Feed(same, "SHOW_ENTITY - Updating Entity=30 CardID=SOURCE_CARD");
        Check(same.Snapshot().Entities[30].CardId == "SOURCE_CARD" && same.Snapshot().Entities[30].GetIntTag("ATK") == 99,
            "A same-definition SHOW erased actual observed public category or attributes.");
        var first = Start(); Feed(first, Block()); Show(first); Meta(first); Feed(first, "BLOCK_END"); Complete(first);
        Check(Memory(first).Entries.Single().CardId == "DIRECT_CARD", "The first actual SHOW of an unspecified identity lost its observed ordinary category.");
    }
    private static void OrdinaryKeywords()
    {
        foreach (var keyword in new[] { "BATTLECRY", "DEATHRATTLE" })
        {
            var engine = Start(); Feed(engine, Block(suffix: " TriggerKeyword=" + keyword)); Show(engine); Meta(engine);
            Feed(engine, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Single().DirectReveal is not null, "An audited ordinary keyword was confused with an unknown or start-of-game scope.");
        }
        foreach (var keyword in new[] { "START_OF_GAME", "START_OF_GAME_KEYWORD", "968", "UNKNOWN_KEYWORD" })
        { var engine = Start(); Feed(engine, Block(suffix: " TriggerKeyword=" + keyword)); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Empty(engine); }
    }
    private static void NumericSources()
    {
        foreach (var reference in new[] { "30", "[id=30]", "[id=30 cardId=SOURCE_CARD]", "[id=30 player=2]", "[id=30 zone=PLAY]" })
        {
            var engine = Start(); Feed(engine, ReferenceBlock(reference)); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Complete(engine);
            var proof = Memory(engine).Entries.Single().DirectReveal!;
            Check(proof.SourceEntityId == 30 && proof.SourceCardId == "SOURCE_CARD" && proof.SourceControllerId == 2 &&
                proof.SourceCardType == "MINION" && proof.SourceZone == "PLAY", "Missing descriptor fields displaced actual observed public source facts.");
        }
        foreach (var reference in new[] { "[id=30 cardId=OTHER]", "[id=30 player=1]", "[id=30 zone=HAND]", "[id=30 id=31]", "999" })
        { var engine = Start(); Feed(engine, ReferenceBlock(reference)); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Empty(engine); }
        foreach (var zone in new[] { "HAND", "DECK", "SETASIDE" })
        { var engine = Start(); Card(engine, 60, "PRIVATE_SOURCE", zone); Feed(engine, ReferenceBlock("60")); Show(engine); Meta(engine); Feed(engine, "BLOCK_END"); Empty(engine); }
        foreach (var initial in new[] { "", "ACTUAL_PLAYED_SOURCE" })
        {
            var engine = Start(); Card(engine, 60, initial, "HAND"); Feed(engine, ReferenceBlock("60")); Show(engine); Meta(engine);
            Show(engine, 60, "ACTUAL_PLAYED_SOURCE"); Feed(engine, "TAG_CHANGE Entity=60 tag=ZONE value=PLAY", "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Single().DirectReveal!.SourceCardId == "ACTUAL_PLAYED_SOURCE", "A numeric source that became actually public in the same epoch was rejected or guessed before disclosure.");
        }
    }
    private static void PublicCarriers()
    {
        foreach (var reference in new[] { "1", "2", "3", "GameEntity" })
        {
            var engine = Start(); Feed(engine, ReferenceBlock(reference), Block()); Show(engine); Meta(engine); Feed(engine, "BLOCK_END");
            Check(engine.Snapshot().PublicHandMemory!.Entries.Count == 0, "A child bypassed its public carrier closure.");
            Feed(engine, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Single().DirectReveal!.SourceEntityId == 30, "A public carrier was mistaken for a card identity source or erased proven child evidence.");
            var directCarrier = Start(); Feed(directCarrier, ReferenceBlock(reference)); Show(directCarrier); Meta(directCarrier); Feed(directCarrier, "BLOCK_END"); Empty(directCarrier);
        }
        foreach (var reference in new[] { "1", "2", "3" })
        {
            var engine = Start(); Feed(engine, ReferenceBlock(reference), Block()); Show(engine); Meta(engine); Feed(engine, "BLOCK_END",
                "META_DATA - Meta=OVERRIDE_HISTORY Data=0 InfoCount=0", "BLOCK_END"); Empty(engine);
        }
        foreach (var root in new[] { "60", "999", "[id=1 cardId=FAKE]" })
        {
            var engine = Start(); Card(engine, 60, "PRIVATE_ANCESTOR", "HAND"); Feed(engine, ReferenceBlock(root), Block()); Show(engine); Meta(engine);
            Feed(engine, "BLOCK_END", "BLOCK_END"); Empty(engine);
        }
    }
    private static void HiddenScopes()
    {
        foreach (var variation in new[] { "late", "ancestor", "child", "start", "joust", "unknown" })
        {
            var engine = Start(); Card(engine, 60, "RETURN_CARD", "PLAY"); Feed(engine, "HIDE_ENTITY - Entity=60 tag=ZONE value=HAND");
            Feed(engine, variation == "start" ? Block(suffix: " TriggerKeyword=START_OF_GAME") : variation == "joust" ? Block("JOUST") : variation == "unknown" ? Block("UNRECOGNIZED") : Block());
            if (variation is "ancestor" or "child") Feed(engine, Block());
            Show(engine); Meta(engine);
            if (variation == "child") Feed(engine, "META_DATA - Meta=OVERRIDE_HISTORY Data=0 InfoCount=0");
            if (variation is "ancestor" or "child") Feed(engine, "BLOCK_END");
            if (variation is "late" or "ancestor") Feed(engine, "META_DATA - Meta=OVERRIDE_HISTORY Data=0 InfoCount=0");
            Feed(engine, "BLOCK_END"); Complete(engine);
            Check(Memory(engine).Entries.Select(entry => entry.EntityId).SequenceEqual([60]), "A hidden scope leaked direct identity or erased an unrelated reliable return.");
        }
    }
    private static void MetaCompleteness()
    {
        foreach (var rows in new[] { new[] { "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=2", "Info[0] = 41" },
            ["META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[1] = 41"],
            ["META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=2", "Info[0] = 41", "Info[0] = 41"],
            ["META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=2", "Info[0] = 41", "Info[1] = 41"],
            ["META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = unknown"],
            ["META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = 41", "Info[1] = 41"],
            ["META_DATA - Meta=HISTORY_TARGET Data=0", "Info[0] = 41"], ["Info[0] = 41"] })
        { var engine = Start(); Feed(engine, Block()); Show(engine); Feed(engine, rows); Feed(engine, "BLOCK_END"); Empty(engine); }
        var malformed = Start(); Card(malformed, 60, "RETURN_CARD", "PLAY"); Feed(malformed, "HIDE_ENTITY - Entity=60 tag=ZONE value=HAND", Block()); Show(malformed);
        Feed(malformed, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = [id=999999999999999999999 cardId=DIRECT_CARD player=2]", "BLOCK_END"); Complete(malformed);
        Check(malformed.Snapshot().Quality.IsComplete && Memory(malformed).Entries.Select(entry => entry.EntityId).SequenceEqual([60]), "Unknown optional history scope invalidated unrelated continuous return facts.");
    }
    private static void PrivateMarkers()
    {
        foreach (var tag in new[] { "REVEALED", "410", "ZONES_REVEALED", "348", "FORGE_REVEALED", "3070", "PREPARE_REVEALED", "4459" })
        { var engine = Start(); Feed(engine, Block()); Show(engine); Feed(engine, $"TAG_CHANGE Entity=41 tag={tag} value=1", "BLOCK_END"); Empty(engine); }
        var created = Start(); Feed(created, Block()); Card(created, 41, "DIRECT_CARD", "HAND"); Meta(created); Feed(created, "BLOCK_END"); Empty(created);
        var alone = Start(); Show(alone); Meta(alone); Empty(alone);
    }
    private static void TargetEvidence()
    {
        foreach (var descriptor in new[] { "42", "[id=41 cardId=OTHER player=2]", "[id=41 cardId=DIRECT_CARD player=1]",
            "[id=41 id=42 cardId=DIRECT_CARD player=2]", "[id=41 cardId=DIRECT_CARD cardId=OTHER player=2]" })
        { var engine = Start(); Feed(engine, Block()); Show(engine); Feed(engine, "META_DATA - Meta=HISTORY_TARGET Data=0 InfoCount=1", "Info[0] = " + descriptor, "BLOCK_END"); Empty(engine); }
        foreach (var type in new[] { "ENCHANTMENT", "UNKNOWN" })
        { var engine = Start(); Card(engine, 41, "", "HAND", type: type); Reveal(engine); Empty(engine); }
        var banned = Start(); Feed(banned, Block()); Show(banned, card: "TIME_875t"); Meta(banned); Feed(banned, "BLOCK_END"); Empty(banned);
        var different = Start(); Feed(different, Block()); Show(different); Feed(different, "BLOCK_END", Block()); Meta(different); Feed(different, "BLOCK_END"); Empty(different);
        var conflict = Start(); Feed(conflict, Block()); Show(conflict); Meta(conflict); Show(conflict, card: "CONFLICTING"); Feed(conflict, "BLOCK_END"); Empty(conflict);
    }
    private static void Invalidations()
    {
        foreach (var line in new[] { "CHANGE_ENTITY - Updating Entity=41 CardID=OTHER", "FULL_ENTITY - Updating Entity=41 CardID=DIRECT_CARD",
            "FULL_ENTITY - Creating ID=41 CardID=DIRECT_CARD", "TAG_CHANGE Entity=41 tag=CONTROLLER value=1", "TAG_CHANGE Entity=41 tag=CARDTYPE value=UNKNOWN",
            "TAG_CHANGE Entity=41 tag=ATTACHED value=30", "TAG_CHANGE Entity=41 tag=ZONE value=DECK", "RESET_GAME" })
        { var engine = Start(); Feed(engine, Block()); Show(engine); Meta(engine); Feed(engine, line, "BLOCK_END"); Empty(engine); }
        var confirmed = Start(); Reveal(confirmed); Feed(confirmed, "CHANGE_ENTITY - Updating Entity=30 CardID=CHANGED", "TAG_CHANGE Entity=30 tag=ZONE value=DECK"); Complete(confirmed);
        Check(Memory(confirmed).Entries.Single().DirectReveal!.SourceCardId == "SOURCE_CARD", "Later source lifecycle rewrote already confirmed historical evidence.");
        var gap = Start(); Feed(gap, Block()); Show(gap); Meta(gap); gap.MarkDataGap(); Feed(gap, "BLOCK_END"); Empty(gap);
        var perspective = Start(); Feed(perspective, Block()); Show(perspective); Meta(perspective); perspective.SetLocalPlayerId(2); perspective.SetLocalPlayerId(1); Feed(perspective, "BLOCK_END"); Empty(perspective);
        var players = Start(); Feed(players, Block()); Show(players); Meta(players);
        Feed(players, "TAG_CHANGE Entity=3 tag=PLAYER_ID value=1", "TAG_CHANGE Entity=3 tag=PLAYER_ID value=2", "BLOCK_END"); Empty(players);
        var hidden = Start(); Reveal(hidden); Feed(hidden, "CHANGE_ENTITY - Updating Entity=41 CardID=OTHER"); Empty(hidden);
        var repeated = Start(); repeated.MarkDataGap("same"); Complete(repeated); Show(repeated, 30, "SOURCE_CARD"); Complete(repeated);
        Feed(repeated, Block()); Show(repeated); Meta(repeated); var pending = repeated.Snapshot(); repeated.MarkDataGap("same");
        Check(repeated.Snapshot().StateVersion == pending.StateVersion + 1 && repeated.Snapshot().EventSequence == pending.EventSequence,
            "Clearing pending history on a repeated external gap did not invalidate the snapshot binding.");
        Feed(repeated, "BLOCK_END"); Empty(repeated);
    }
    private static void SourceInvalidations()
    {
        foreach (var line in new[] { "CHANGE_ENTITY - Updating Entity=30 CardID=SOURCE_CARD", "FULL_ENTITY - Updating Entity=30 CardID=SOURCE_CARD",
            "TAG_CHANGE Entity=30 tag=CONTROLLER value=1", "TAG_CHANGE Entity=30 tag=CARDTYPE value=UNKNOWN" })
        { var engine = Start(); Feed(engine, Block()); Show(engine); Meta(engine); Feed(engine, line, "BLOCK_END"); Empty(engine); }
    }
    private static void HiddenMultiplicity()
    {
        var engine = Start(); Card(engine, 42, "", "HAND"); Feed(engine, Block()); Show(engine); Meta(engine);
        Feed(engine, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND", "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND");
        Show(engine, 42); Meta(engine, 42); Feed(engine, "BLOCK_END"); Complete(engine);
        Check(Memory(engine).Entries.Count == 2 && Memory(engine).Entries.Select(entry => entry.EntityId).Distinct().Count() == 2, "Same-card public memories were merged or ordinary hiding erased proof.");
        Feed(engine, "HIDE_ENTITY - Entity=41 tag=ZONE value=HAND"); Complete(engine); Check(Memory(engine).Entries.Count == 2, "Repeated hide erased confirmed history proof.");
        Feed(engine, "TAG_CHANGE Entity=41 tag=ZONE value=DECK"); Complete(engine); Check(Memory(engine).Entries.Select(entry => entry.EntityId).SequenceEqual([42]), "Leaving hand retained direct memory.");
    }
    private static void ScopeBoundaries()
    {
        foreach (var build in new string?[] { null, "255791", "0" }) { var engine = Start(build); Reveal(engine); Empty(engine); }
        var starting = Start(step: "BEGIN_MULLIGAN"); Reveal(starting); Empty(starting);
        var unknown = Start(local: null); Reveal(unknown); Check(PublicHandMemoryVisibility.Confirmed(unknown.Snapshot()) is null, "Unknown perspective exposed direct history.");
        var spectator = Start(); Feed(spectator, "Begin Spectating"); Reveal(spectator); Check(PublicHandMemoryVisibility.Confirmed(spectator.Snapshot()) is null, "Spectating exposed direct history.");
        var gap = Start(); gap.MarkDataGap(); Reveal(gap); Empty(gap);
        Show(gap, 30, "SOURCE_CARD"); Complete(gap); Reveal(gap);
        Check(!Memory(gap).IsComplete && Memory(gap).Entries.Single().DirectReveal is not null, "Fresh independently public source proof could not recover after a gap.");
    }
    private static void ConsumerAndJson()
    {
        var engine = Start(); Reveal(engine); var frame = engine.Snapshot(); var entry = Memory(engine).Entries.Single();
        var evidence = entry.DirectReveal!;
        foreach (var probe in new[] { entry with { EnteredHandAtSequence = entry.PublicAtSequence }, entry with { FromZone = "PLAY" }, entry with { DirectReveal = evidence with { ClientBuild = "255791" } },
            entry with { DirectReveal = evidence with { ShowAtSequence = evidence.ConfirmedAtSequence + 1 } }, entry with { DirectReveal = evidence with { SourceEntityId = 41 } },
            entry with { DirectReveal = evidence with { SourceCardType = "ENCHANTMENT" } }, entry with { DirectReveal = evidence with { SourceCardId = "private secret" } } })
        {
            var memory = frame.PublicHandMemory!;
            var altered = new PublicHandMemorySnapshot(memory.MatchId, memory.StateVersion, memory.EventSequence, memory.LocalPlayerId, memory.PerspectiveSource, [probe], memory.IsComplete, memory.Warnings, memory.IsSettled, memory.ContinuityPolicy);
            Check(PublicHandMemoryVisibility.Confirmed(frame with { PublicHandMemory = altered }) is null, "A forged direct evidence shape passed the shared gate.");
        }
        var returnEntry = new PublicHandMemoryEntry(60, "RETURN_CARD", 2, "PLAY", 3, 5);
        Check(JsonSerializer.Serialize(returnEntry) == "{\"EntityId\":60,\"CardId\":\"RETURN_CARD\",\"ControllerId\":2,\"FromZone\":\"PLAY\",\"PublicAtSequence\":3,\"EnteredHandAtSequence\":5}", "New optional fields rewrote legacy return JSON.");
        var json = JsonSerializer.Serialize(entry); Check(!json.Contains("EnteredHandAtSequence") && JsonSerializer.Deserialize<PublicHandMemoryEntry>(json) == entry, "Direct evidence did not survive optional-field JSON round trip.");
        var ids = PublicHandMemoryVisibility.KnownCardIds(frame);
        Check(ids.SequenceEqual(new[] { "DIRECT_CARD", "SOURCE_CARD" }), "Shared referenced identities omitted a proven target or its historical public source.");
        try { ((IList<string>)ids).Clear(); throw new Exception("Mutable referenced identities."); } catch (NotSupportedException) { }
    }
    private static void MetadataNumbering()
    {
        var engine = Start(); Feed(engine, Block()); Show(engine); var before = engine.Snapshot(); Meta(engine); var after = engine.Snapshot();
        Check(before.StateVersion == after.StateVersion && before.EventSequence == after.EventSequence &&
            JsonSerializer.Serialize(before.TurnHistory) == JsonSerializer.Serialize(after.TurnHistory), "Previously ignored metadata changed existing state event or turn-history numbering.");
        Feed(engine, "BLOCK_END"); Complete(engine); Check(Memory(engine).Entries.Count == 1, "Preserving event anchors lost useful confirmed proof.");
    }
}
