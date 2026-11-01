using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using HsHelper.Core;

public static class PublicHandMemoryChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("public hand memory follows PLAY and GRAVEYARD identities while private entities remain concealed", PublicReturns);
        yield return ("public hand memory handles ZONE then HIDE and repeated HIDE without duplicate rows", HideOrders);
        yield return ("public hand memory survives ordinary hand position changes without exposing a position", PositionChanges);
        yield return ("secret return needs a prior public identity and never trusts a private SHOW", SecretAndPrivateShow);
        yield return ("private HAND DECK SETASIDE and attachment definitions never become hand memory", PrivateSources);
        yield return ("public hand memory invalidates CHANGE FULL updates and reused entity creation", Rebuilds);
        yield return ("copy and creator references never manufacture a public hand identity", Copies);
        yield return ("public hand memory drops departures deck shuffles and control changes", Departures);
        yield return ("public hand memory clears old observers spectator transitions and new games", Observers);
        yield return ("a changed or newly confirmed observer can reobserve a still-public body without another SHOW", PublicAfterPerspectiveChange);
        yield return ("public hand memory clears gap facts but accepts newly observed public identities after settlement", Gaps);
        yield return ("unmatched block end clears existing hand memory and preserves the gap warning and version transition", BlockUnderflowMemory);
        yield return ("unmatched block end invalidates public sources before a later ordinary update and return", BlockUnderflowSource);
        yield return ("new actual public identity after block underflow can establish incomplete hand memory", BlockUnderflowRecovery);
        yield return ("balanced nested blocks preserve public identity through settlement", BalancedBlocks);
        yield return ("legacy saved underflow memory is unknown while ordinary legacy records remain readable", LegacyUnderflowMemory);
        yield return ("midgame logs retain newly observed public returns without inventing a game start", MidgamePublicProof);
        yield return ("public hand memory cannot revive a gap source from stale raw identity or attribute changes", NoGapResurrection);
        yield return ("public hand memory does not depend on the bounded recent-event buffer", EventBuffer);
        yield return ("public hand memory does not publish unsettled or open-block observations", Settlement);
        yield return ("public hand memory rejects changed categories and attachments before a hidden return", ClassificationChanges);
        yield return ("unrelated incomplete attachment state does not erase independently settled hand evidence", DerivedQuality);
        yield return ("public hand memory snapshots remain deeply readonly across later reveals", Immutable);
        yield return ("public hand memory null omission preserves legacy representation with readonly codec options", Serialization);
        yield return ("public hand memory consumer gate rejects wrong bindings future or duplicate evidence", InvalidBindings);
        yield return ("public hand memory consumer gate rejects hidden attributes category attachment and unsafe card tokens", InvalidEntities);
    }

    private static PowerLogStateEngine Start(int? local = 1, bool hasGameStart = true)
    {
        var engine = new PowerLogStateEngine(local, "public-hand-memory");
        if (hasGameStart) Feed(engine, "CREATE_GAME");
        Feed(engine, "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=1", "tag=STEP value=MAIN_ACTION",
            "Player EntityID=2 PlayerID=1", "tag=CURRENT_PLAYER value=1", "Player EntityID=3 PlayerID=2", "tag=CURRENT_PLAYER value=0");
        Complete(engine); return engine;
    }
    private static void Feed(PowerLogStateEngine engine, params string[] lines)
    { foreach (var line in lines) { engine.ProcessLine(line); _ = engine.Snapshot(); } }
    private static void Complete(PowerLogStateEngine engine) => engine.ProcessLine("PowerProcessor.EndCurrentTaskList() - complete");
    private static void Card(PowerLogStateEngine engine, int id = 30, string zone = "PLAY", string card = "PUBLIC_CARD", int owner = 2, string type = "MINION") =>
        Feed(engine, $"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", $"tag=CARDTYPE value={type}",
            $"tag=ZONE value={zone}", "tag=COST value=9", "tag=ATK value=7", "tag=HEALTH value=8", "tag=ZONE_POSITION value=3");
    private static void Bounce(PowerLogStateEngine engine, int id = 30)
    { Feed(engine, $"HIDE_ENTITY - Entity={id} tag=ZONE value=HAND"); Complete(engine); }
    private static PublicHandMemorySnapshot Memory(PowerLogStateEngine engine) =>
        PublicHandMemoryVisibility.Confirmed(engine.Snapshot()) ?? throw new Exception("Expected a current reliable memory ledger.");
    private static void Hidden(GameSnapshot snapshot, int id = 30)
    {
        var entity = snapshot.Entities[id];
        Check(entity.CardId is null && entity.Tags.Keys.All(tag => tag is "ZONE" or "CONTROLLER" or "ENTITY_ID" or "ZONE_POSITION"),
            "Remembered identity or its dynamic attributes entered a private entity.");
    }
    private static void Empty(PowerLogStateEngine engine) => Check(Memory(engine).Entries.Count == 0, "An unproven or invalidated identity survived in current hand memory.");

    private static void PublicReturns()
    {
        foreach (var zone in new[] { "PLAY", "GRAVEYARD" })
        {
            var engine = Start(); Card(engine, zone: zone); var publicAt = engine.Snapshot().EventSequence; Bounce(engine);
            var frame = engine.Snapshot(); var memory = Memory(engine); var entry = memory.Entries.Single();
            Check(entry is { EntityId: 30, CardId: "PUBLIC_CARD", ControllerId: 2 } && entry.FromZone == zone &&
                entry.PublicAtSequence <= publicAt && entry.PublicAtSequence > 0 && entry.EnteredHandAtSequence > entry.PublicAtSequence &&
                memory.StateVersion == frame.StateVersion && memory.EventSequence == frame.EventSequence && memory.LocalPlayerId == 1 && memory.IsComplete,
                "Public source, transition sequence or current binding was lost."); Hidden(frame);
        }
    }
    private static void HideOrders()
    {
        var engine = Start(); Card(engine);
        Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE value=HAND"); Complete(engine);
        var first = Memory(engine).Entries.Single();
        Feed(engine, "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND", "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND"); Complete(engine);
        Check(Memory(engine).Entries.SequenceEqual([first]), "Normal concealment or duplicate HIDE changed the remembered same entity."); Hidden(engine.Snapshot());
    }
    private static void PositionChanges()
    {
        var engine = Start(); Card(engine); Bounce(engine); var first = Memory(engine).Entries.Single();
        Feed(engine, "TAG_CHANGE Entity=30 tag=ZONE_POSITION value=1", "TAG_CHANGE Entity=30 tag=ZONE_POSITION value=2"); Complete(engine);
        Check(Memory(engine).Entries.SequenceEqual([first]) && !JsonSerializer.Serialize(first).Contains("Position", StringComparison.Ordinal),
            "Ordinary hand compaction invalidated identity or inferred a hand slot.");
    }
    private static void SecretAndPrivateShow()
    {
        var engine = Start(); Card(engine, type: "SPELL");
        Feed(engine, "HIDE_ENTITY - Entity=30 tag=ZONE value=SECRET"); Bounce(engine);
        Check(Memory(engine).Entries.Single().FromZone == "PLAY", "An intermediate SECRET was incorrectly claimed as a public source.");
        var hidden = Start(); Card(hidden, zone: "SECRET", type: "SPELL", card: "SECRET_PROBE");
        Feed(hidden, "SHOW_ENTITY - Updating Entity=30 CardID=SECRET_PROBE"); Bounce(hidden); Empty(hidden);
        var hand = Start(); Card(hand, zone: "HAND", card: "HAND_PROBE");
        Feed(hand, "SHOW_ENTITY - Updating Entity=30 CardID=HAND_PROBE"); Complete(hand); Empty(hand); Hidden(hand.Snapshot());
    }
    private static void PrivateSources()
    {
        foreach (var zone in new[] { "HAND", "DECK", "SETASIDE" })
        {
            var engine = Start(); Card(engine, zone: zone, card: "PRIVATE_PROBE"); Bounce(engine); Empty(engine);
        }
        var attachment = Start(); Card(attachment, 40, "HAND", "PRIVATE_TARGET");
        Card(attachment, 30, "PLAY", "PRIVATE_ATTACHED", type: "ENCHANTMENT");
        Feed(attachment, "TAG_CHANGE Entity=30 tag=ATTACHED value=40"); Bounce(attachment); Empty(attachment);
    }
    private static void Rebuilds()
    {
        foreach (var line in new[] { "CHANGE_ENTITY - Updating Entity=30 CardID=PRIVATE_NEW", "FULL_ENTITY - Updating Entity=30 CardID=PUBLIC_CARD", "FULL_ENTITY - Creating ID=30 CardID=PUBLIC_CARD" })
        {
            var engine = Start(); Card(engine); Bounce(engine); Feed(engine, line); Complete(engine); Empty(engine); Hidden(engine.Snapshot());
        }
    }
    private static void Copies()
    {
        var engine = Start(); Card(engine); Bounce(engine); Card(engine, 31, "HAND", "PUBLIC_CARD");
        Feed(engine, "TAG_CHANGE Entity=31 tag=COPIED_FROM_ENTITY_ID value=30", "TAG_CHANGE Entity=31 tag=CREATOR value=30"); Complete(engine);
        Check(Memory(engine).Entries.Select(entry => entry.EntityId).SequenceEqual([30]), "A copied or created private entity inherited a source's public proof."); Hidden(engine.Snapshot(), 31);
        Card(engine, 32, card: "PUBLIC_CARD"); Bounce(engine, 32);
        Check(Memory(engine).Entries.Count == 2 && Memory(engine).Entries.All(entry => entry.CardId == "PUBLIC_CARD") &&
            Memory(engine).Entries.Select(entry => entry.EntityId).Order().SequenceEqual([30, 32]),
            "Two separately observed same-name public returns were merged by CardId or a private copy was included.");
    }
    private static void Departures()
    {
        foreach (var zone in new[] { "DECK", "SETASIDE", "REMOVEDFROMGAME" })
        {
            var engine = Start(); Card(engine); Bounce(engine);
            Feed(engine, $"TAG_CHANGE Entity=30 tag=ZONE value={zone}", "TAG_CHANGE Entity=30 tag=ZONE value=HAND"); Complete(engine); Empty(engine);
        }
        var control = Start(); Card(control); Bounce(control);
        Feed(control, "TAG_CHANGE Entity=30 tag=CONTROLLER value=1", "TAG_CHANGE Entity=30 tag=CONTROLLER value=2"); Complete(control); Empty(control);
    }
    private static void Observers()
    {
        var engine = Start(); Card(engine); Bounce(engine);
        engine.SetLocalPlayerId(2); engine.SetLocalPlayerId(1); Complete(engine); Empty(engine);
        Card(engine, 31); Bounce(engine, 31);
        Feed(engine, "Begin Spectating", "End Spectator"); Complete(engine); Empty(engine);
        engine.ResetSession(); Check(engine.Snapshot().PublicHandMemory is null, "Session reset retained a previous match ledger.");
        var fresh = Start(); Card(fresh); Bounce(fresh); Feed(fresh, "CREATE_GAME");
        Check(fresh.Snapshot().PublicHandMemory is null, "CREATE_GAME retained old entities or observer memory.");
    }
    private static void Gaps()
    {
        var engine = Start(); Card(engine); Bounce(engine); engine.MarkDataGap(); Complete(engine);
        Check(engine.Snapshot().PublicHandMemory!.Entries.Count == 0, "A gap retained old known hand identities.");
        Card(engine, 31, card: "AFTER_GAP"); Bounce(engine, 31); var partial = Memory(engine);
        Check(!partial.IsComplete && partial.IsSettled && partial.Warnings.Count > 0 && partial.Entries.Single().CardId == "AFTER_GAP" &&
            !engine.Snapshot().Quality.IsStable, "A new reliable post-gap public identity was permanently suppressed or represented as complete.");
    }
    private static void PublicAfterPerspectiveChange()
    {
        foreach (var unknown in new[] { false, true })
        {
            var engine = Start(unknown ? null : 1); Card(engine);
            if (!unknown) { engine.SetLocalPlayerId(2); _ = engine.Snapshot(); }
            engine.SetLocalPlayerId(1); _ = engine.Snapshot(); Bounce(engine);
            Check(Memory(engine).Entries.Single().CardId == "PUBLIC_CARD", "A current publicly visible ordinary body needed an unnecessary new identity log after perspective confirmation.");
        }
        var gap = Start(); Card(gap); gap.MarkDataGap(); gap.SetLocalPlayerId(2); _ = gap.Snapshot(); gap.SetLocalPlayerId(1);
        Bounce(gap); Empty(gap);
    }
    private static void NoGapResurrection()
    {
        var engine = Start(); Card(engine); engine.MarkDataGap();
        Feed(engine, "TAG_CHANGE Entity=30 tag=ATK value=10"); Bounce(engine); Empty(engine);
        Feed(engine, "SHOW_ENTITY - Updating Entity=30 CardID=NEW_PUBLIC", "tag=CARDTYPE value=MINION", "tag=ZONE value=PLAY"); Bounce(engine);
        Check(Memory(engine).Entries.Single().CardId == "NEW_PUBLIC", "Actual new public identity failed to reanchor after a gap.");
    }
    private static void BlockUnderflowMemory()
    {
        var engine = Start(); Card(engine); Bounce(engine); var before = engine.Snapshot();
        Check(engine.ProcessLine("BLOCK_END"), "Unmatched block end did not report a changed observation.");
        var gap = engine.Snapshot();
        Check(gap.PublicHandMemory!.Entries.Count == 0, "An unmatched block end retained an existing hand identity across lost input.");
        Check(!gap.Quality.IsComplete && !gap.Quality.IsStable && gap.Quality.OpenBlockCount == 0 &&
            gap.Quality.Warnings.Contains("结算块结束缺少对应开始") && gap.StateVersion == before.StateVersion + 1 &&
            gap.EventSequence == before.EventSequence + 1, "Unmatched block end changed its quality, warning or single-event version semantics.");
        Complete(engine); Check(!Memory(engine).IsComplete, "Input loss was presented as complete hand history after settlement."); Empty(engine);
        var repeated = engine.Snapshot(); engine.ProcessLine("BLOCK_END");
        Check(engine.Snapshot().StateVersion == repeated.StateVersion + 1, "A repeated underflow with the same warning failed to advance the state once.");
    }
    private static void BlockUnderflowSource()
    {
        var engine = Start(); Card(engine);
        Feed(engine, "BLOCK_END", "TAG_CHANGE Entity=30 tag=ATK value=10"); Bounce(engine);
        Empty(engine);
        Check(!Memory(engine).IsComplete, "A stale pre-underflow public source regained complete-history status.");
    }
    private static void BlockUnderflowRecovery()
    {
        var engine = Start(); Card(engine); Feed(engine, "BLOCK_END", "SHOW_ENTITY - Updating Entity=30 CardID=NEW_PUBLIC_AFTER_UNDERFLOW"); Bounce(engine);
        var memory = Memory(engine);
        Check(!memory.IsComplete && memory.IsSettled && memory.Warnings.Count > 0 &&
            memory.Entries.Single().CardId == "NEW_PUBLIC_AFTER_UNDERFLOW" &&
            memory.ContinuityPolicy == PublicHandMemoryVisibility.CurrentContinuityPolicy,
            "A new actual public proof after underflow could not reestablish independently reliable incomplete memory with its audited policy.");
    }
    private static void BalancedBlocks()
    {
        var engine = Start(); Card(engine);
        Feed(engine, "BLOCK_START BlockType=POWER Entity=30", "BLOCK_START BlockType=TRIGGER Entity=30",
            "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND", "BLOCK_END");
        Check(engine.Snapshot().Quality.OpenBlockCount == 1 && PublicHandMemoryVisibility.Confirmed(engine.Snapshot()) is null,
            "Closing only the inner block published an unsettled hand identity.");
        Feed(engine, "BLOCK_END"); Complete(engine);
        Check(Memory(engine).IsComplete && Memory(engine).Entries.Single().CardId == "PUBLIC_CARD" &&
            !engine.Snapshot().Quality.Warnings.Contains("结算块结束缺少对应开始"), "Balanced nested blocks were treated as lost input.");
    }
    private static void LegacyUnderflowMemory()
    {
        var engine = Start(); Card(engine); Bounce(engine); var frame = engine.Snapshot(); var memory = Memory(engine);
        var legacyMemory = new PublicHandMemorySnapshot(memory.MatchId, memory.StateVersion, memory.EventSequence,
            memory.LocalPlayerId, memory.PerspectiveSource, memory.Entries, memory.IsComplete, memory.Warnings, memory.IsSettled);
        var legacy = frame with { PublicHandMemory = legacyMemory };
        Check(PublicHandMemoryVisibility.Confirmed(legacy)!.Entries.Single().CardId == "PUBLIC_CARD", "A normal legacy ledger was made unreadable.");
        var underflow = legacy with { Quality = legacy.Quality with { IsComplete = false, IsStable = false,
            Warnings = ["结算块结束缺少对应开始"] } };
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true,
            Converters = { new JsonStringEnumConverter(), new FrozenEntitiesConverter(), new FrozenTagsConverter() } };
        var json = JsonSerializer.Serialize(underflow, options);
        Check(!json.Contains("ContinuityPolicy", StringComparison.Ordinal), "Fixture does not represent the missing-policy legacy format.");
        var restored = JsonSerializer.Deserialize<GameSnapshot>(json, options)!;
        Check(restored.PublicHandMemory!.ContinuityPolicy is null, "Legacy deserialization promoted missing continuity evidence to a new policy.");
        Check(PublicHandMemoryVisibility.Confirmed(restored) is null, "A legacy saved underflow snapshot exposed its stale hand memory without an audited continuity policy.");
        Check(JsonSerializer.Serialize(underflow, options) == json && underflow.PublicHandMemory!.Entries.Count == 1,
            "Consumer filtering rewrote the legacy raw evidence instead of deriving unknown visibility.");
        var unrelatedPartial = legacy with { Quality = legacy.Quality with { IsComplete = false, IsStable = false, Warnings = ["unrelated incomplete observation"] } };
        var confirmedPartial = PublicHandMemoryVisibility.Confirmed(unrelatedPartial);
        Check(confirmedPartial is { IsComplete: false, ContinuityPolicy: null } && confirmedPartial.Entries.Single().CardId == "PUBLIC_CARD",
            "An ordinary incomplete legacy record was blocked or its missing policy was upgraded during derived projection.");
        var unknownPolicy = new PublicHandMemorySnapshot(legacyMemory.MatchId, legacyMemory.StateVersion, legacyMemory.EventSequence,
            legacyMemory.LocalPlayerId, legacyMemory.PerspectiveSource, legacyMemory.Entries, legacyMemory.IsComplete,
            legacyMemory.Warnings, legacyMemory.IsSettled, "unreviewed-policy");
        Check(PublicHandMemoryVisibility.Confirmed(underflow with { PublicHandMemory = unknownPolicy }) is null,
            "An unreviewed continuity label authorized stale underflow evidence.");
    }
    private static void MidgamePublicProof()
    {
        foreach (var zone in new[] { "PLAY", "GRAVEYARD" })
        {
            var engine = Start(hasGameStart: false); Card(engine, zone: zone); Bounce(engine);
            var frame = engine.Snapshot(); var memory = Memory(engine);
            Check(!frame.Quality.HasGameStart && !frame.Quality.IsComplete && !frame.Quality.IsStable &&
                !memory.IsComplete && memory.IsSettled && memory.Warnings.Count > 0 && memory.Entries.Single().FromZone == zone,
                "A new midgame public identity was suppressed or turned into evidence of a complete game start.");
            Hidden(frame);
        }
        var privateOnly = Start(hasGameStart: false); Card(privateOnly, zone: "HAND", card: "PRIVATE_MIDGAME");
        Feed(privateOnly, "SHOW_ENTITY - Updating Entity=30 CardID=PRIVATE_MIDGAME"); Complete(privateOnly); Empty(privateOnly);
        var unknown = Start(local: null, hasGameStart: false); Card(unknown); Bounce(unknown);
        Check(PublicHandMemoryVisibility.Confirmed(unknown.Snapshot()) is null, "Midgame identity bypassed an unconfirmed observer.");
        var spectator = Start(hasGameStart: false); Feed(spectator, "Begin Spectating"); Card(spectator); Bounce(spectator);
        Check(PublicHandMemoryVisibility.Confirmed(spectator.Snapshot()) is null, "Midgame identity bypassed spectator privacy.");
    }
    private static void EventBuffer()
    {
        var engine = Start(); Card(engine); Bounce(engine); var source = Memory(engine).Entries.Single();
        for (var index = 0; index < 100; index++) Feed(engine, $"TAG_CHANGE Entity=GameEntity tag=TURN value={index + 2}");
        Complete(engine);
        Check(engine.Snapshot().EventHistoryTruncatedThrough > source.EnteredHandAtSequence && Memory(engine).Entries.SequenceEqual([source]),
            "Discarding redundant recent events erased the engine's continuously observed same-entity memory.");
    }
    private static void Settlement()
    {
        var engine = Start(); Card(engine); Feed(engine, "BLOCK_START BlockType=POWER Entity=30", "HIDE_ENTITY - Entity=30 tag=ZONE value=HAND");
        Check(PublicHandMemoryVisibility.Confirmed(engine.Snapshot()) is null, "An unsettled open-block hand identity was published.");
        Feed(engine, "BLOCK_END"); Complete(engine); Check(Memory(engine).Entries.Count == 1, "Settlement lost the raw public transition proof.");
    }
    private static void ClassificationChanges()
    {
        foreach (var change in new[] { "CARDTYPE value=UNKNOWN", "CARDTYPE value=ENCHANTMENT", "ATTACHED value=999", "40 value=999" })
        {
            var engine = Start(); Card(engine); Feed(engine, "TAG_CHANGE Entity=30 tag=" + change); Bounce(engine); Empty(engine);
        }
        var ordinary = Start(); Card(ordinary); Feed(ordinary, "TAG_CHANGE Entity=30 tag=CARDTYPE value=SPELL"); Bounce(ordinary);
        Check(Memory(ordinary).Entries.Count == 1, "A new explicit ordinary public category was unnecessarily denied.");
    }
    private static void Immutable()
    {
        var engine = Start(); Card(engine); Bounce(engine); var frame = engine.Snapshot(); var before = JsonSerializer.Serialize(frame);
        Feed(engine, "CHANGE_ENTITY - Updating Entity=30 CardID=OTHER"); Complete(engine);
        Check(JsonSerializer.Serialize(frame) == before && frame.PublicHandMemory!.Entries.Single().CardId == "PUBLIC_CARD", "A later reveal rewrote an earlier memory snapshot.");
        try { ((IList<PublicHandMemoryEntry>)frame.PublicHandMemory!.Entries).Clear(); throw new Exception("Mutable memory entries."); } catch (NotSupportedException) { }
        try { ((IList<string>)frame.PublicHandMemory!.Warnings).Add("changed"); throw new Exception("Mutable memory warnings."); } catch (NotSupportedException) { }
    }
    private static void Serialization()
    {
        var engine = Start(); Card(engine); Bounce(engine); var frame = engine.Snapshot();
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true, Converters = { new JsonStringEnumConverter(), new FrozenEntitiesConverter(), new FrozenTagsConverter() } };
        var json = JsonSerializer.Serialize(frame, options); var restored = JsonSerializer.Deserialize<GameSnapshot>(json, options)!;
        Check(PublicHandMemoryVisibility.Confirmed(restored)!.Entries.Single() == frame.PublicHandMemory!.Entries.Single(), "Readonly memory fields were lost by production-like serialization.");
        var legacy = frame with { PublicHandMemory = null }; var old = JsonSerializer.Serialize(legacy, options);
        Check(!old.Contains("PublicHandMemory", StringComparison.Ordinal) && JsonSerializer.Deserialize<GameSnapshot>(old, options)!.PublicHandMemory is null,
            "An absent old ledger was serialized as a new null field or an invented empty ledger.");
    }
    private static PublicHandMemorySnapshot Alter(PublicHandMemorySnapshot memory, string? match = null, long? version = null, long? sequence = null,
        int? local = null, string? perspective = null, IReadOnlyList<PublicHandMemoryEntry>? entries = null) =>
        new(match ?? memory.MatchId, version ?? memory.StateVersion, sequence ?? memory.EventSequence, local ?? memory.LocalPlayerId,
            perspective ?? memory.PerspectiveSource, entries ?? memory.Entries, memory.IsComplete, memory.Warnings, memory.IsSettled, memory.ContinuityPolicy);
    private static void InvalidBindings()
    {
        var engine = Start(); Card(engine); Bounce(engine); var frame = engine.Snapshot(); var memory = Memory(engine); var entry = memory.Entries.Single();
        foreach (var bad in new[] { Alter(memory, match: "other"), Alter(memory, version: memory.StateVersion + 1), Alter(memory, sequence: memory.EventSequence + 1),
            Alter(memory, local: 2), Alter(memory, perspective: "unconfirmed"), Alter(memory, entries: [entry, entry]),
            Alter(memory, entries: [entry with { PublicAtSequence = 0 }]), Alter(memory, entries: [entry with { EnteredHandAtSequence = frame.EventSequence + 1 }]),
            Alter(memory, entries: [entry with { FromZone = "DECK" }]) })
            Check(PublicHandMemoryVisibility.Confirmed(frame with { PublicHandMemory = bad }) is null, "A mismatched, duplicate or future ledger was accepted.");
    }
    private static void InvalidEntities()
    {
        var engine = Start(); Card(engine); Bounce(engine); var frame = engine.Snapshot(); var memory = Memory(engine); var entry = memory.Entries.Single();
        foreach (var probe in new[] { "SECRET KEY", "PUBLIC_CARD\0", "https://private", "中文探针" })
            Check(PublicHandMemoryVisibility.Confirmed(frame with { PublicHandMemory = Alter(memory, entries: [entry with { CardId = probe }]) }) is null, "An unsafe card identity token was accepted.");
        foreach (var tag in new[] { "COST", "CARDTYPE", "202", "ATTACHED", "40", "COPIED_FROM_ENTITY_ID" })
        {
            var entities = frame.Entities.ToDictionary(); var entity = entities[30];
            entities[30] = entity with { Tags = entity.Tags.Append(new KeyValuePair<string, string>(tag, "1")).ToFrozenDictionary() };
            Check(PublicHandMemoryVisibility.Confirmed(frame with { Entities = entities.ToFrozenDictionary() }) is null, "Hidden current attributes or attachment/category counterevidence were accepted.");
        }
    }
    private static void DerivedQuality()
    {
        var engine = Start(); Card(engine); Bounce(engine); var original = Memory(engine).Entries.Single();
        Card(engine, 80, "PLAY", "UNBOUND_ATTACHMENT", type: "ENCHANTMENT"); Complete(engine);
        var frame = engine.Snapshot();
        Check(!frame.Quality.IsComplete && !frame.Quality.IsStable && frame.PublicHandMemory!.IsComplete,
            "Fixture did not reach the derived attachment-quality boundary.");
        var confirmed = Memory(engine);
        Check(!confirmed.IsComplete && confirmed.IsSettled && confirmed.Warnings.Count > 0 && confirmed.Entries.SequenceEqual([original]) &&
            confirmed.ContinuityPolicy == frame.PublicHandMemory!.ContinuityPolicy && confirmed.ContinuityPolicy == PublicHandMemoryVisibility.CurrentContinuityPolicy,
            "An unrelated unresolved attachment erased independently observed public hand memory or falsely declared complete context.");
    }
    private sealed class FrozenEntitiesConverter : JsonConverter<FrozenDictionary<int, EntitySnapshot>>
    {
        public override FrozenDictionary<int, EntitySnapshot> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<int, EntitySnapshot>>(ref reader, options)!.ToFrozenDictionary();
        public override void Write(Utf8JsonWriter writer, FrozenDictionary<int, EntitySnapshot> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToDictionary(), options);
    }
    private sealed class FrozenTagsConverter : JsonConverter<FrozenDictionary<string, string>>
    {
        public override FrozenDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options)!.ToFrozenDictionary(StringComparer.Ordinal);
        public override void Write(Utf8JsonWriter writer, FrozenDictionary<string, string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToDictionary(), options);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
