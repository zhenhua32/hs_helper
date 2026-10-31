using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class MagmaSplitChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Magma audited two-target split enumerates all three distinct two-packet outcomes", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, State(frame));
            Check(result.TriggerActive == true && result.ResolvesBranches && result.IsEnumerationComplete && result.RequestedPackets == 2 &&
                result.DamagePerPacket == 1 && result.Branches.Count == 3 && result.Branches.All(branch => branch.IsComplete && branch.PacketsApplied == 2 && branch.PacketsRemaining == 0) &&
                result.RequiresObservation && !result.CanEvaluateFollowupActions && !result.HasAuthoritativeNextState && result.IsLegal is null,
                "A real multi-target split was omitted, counted as a probability or made authoritative.");
            Bounds(Target(result, 11).Damage, 0, 2); Bounds(Target(result, 11).RemainingHealth, 8, 10);
            Bounds(Target(result, 20).RemainingHealth, 6, 8);
        });
        yield return ("Magma one Goldrinn doubles each packet while keeping the source Attack packet count", () =>
        {
            var frame = Add(Frame(), Card(40, "EDR_480", 1, 9, 9)); var result = Eval(frame, cards, State(frame));
            Check(result.IsEnumerationComplete && result.RequestedPackets == 2 && result.DamagePerPacket == 2 && result.Branches.Count == 3,
                "Goldrinn doubled the number of random packets or lost a complete split."); Bounds(Target(result, 11).Damage, 0, 4);
            frame = Add(frame, Card(41, "EDR_480", 1, 9, 9)); result = Eval(frame, cards, State(frame));
            Check(result.DamagePerPacket == 4 && result.RequestedPackets == 2, "Multiple native Goldrinns did not multiply each packet.");
        });
        yield return ("Magma source native Beast race fallback never overwrites explicit current race contradictions", () =>
        {
            var frame = Add(Frame(), Card(40, "EDR_480", 1, 9, 9)); var state = State(frame);
            state = Replace(state, Remove(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "CARDRACE"));
            Check(Eval(frame, cards, state).DamagePerPacket == 2, "Audited native Beast subtype could not support absent current race tags.");
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "CARDRACE", "15"));
            Check(Eval(frame, cards, state).DamagePerPacket == 1, "Explicit current non-Beast race regained Beast from print.");
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "200", "20"));
            Check(!Eval(frame, cards, state).IsEnumerationComplete && Eval(frame, cards, state).DamagePerPacket is null, "Conflicting race aliases acquired an exact multiplier.");
        });
        yield return ("Magma divine shields consume one packet before damage and are preserved on unhit branches", () =>
        {
            var frame = Frame(); var state = Replace(State(frame), Tag(State(frame).EntitiesAfterCombat.Single(entity => entity.Id == 20), "DIVINE_SHIELD", "1"));
            var result = Eval(frame, cards, state); Check(result.IsEnumerationComplete && result.Branches.Count == 3, "Shield split lost distinct terminal states.");
            Bounds(Target(result, 20).Damage, 0, 1);
            Check(result.Branches.Any(branch => branch.Targets.Single(target => target.EntityId == 20).DivineShield) &&
                result.Branches.Any(branch => !branch.Targets.Single(target => target.EntityId == 20).DivineShield), "Shield was consumed globally before random target selection.");
        });
        yield return ("Magma armor absorbs packets without changing damage accounting or inventing hero health loss", () =>
        {
            var frame = Frame(); var state = Replace(State(frame), Tag(frame.Entities[11], "ARMOR", "2")); var result = Eval(frame, cards, state);
            Check(result.IsEnumerationComplete, "Ordinary armor made the split unknown."); Bounds(Target(result, 11).RemainingHealth, 10, 10);
            Bounds(Target(result, 11).RemainingArmor, 0, 2); Bounds(Target(result, 11).Damage, 0, 2);
        });
        yield return ("Magma removes normal killed minions before the next packet and never spends a later packet on their corpse", () =>
        {
            var frame = Frame(targetHealth: 3); var result = Eval(frame, cards, State(frame));
            Check(result.IsEnumerationComplete && result.Branches.Count == 2 && result.Branches.All(branch =>
                branch.Targets.Single(target => target.EntityId == 20).RemainingHealth >= 0), "A corpse remained eligible for a second packet.");
            Bounds(Target(result, 20).Damage, 0, 1); Bounds(Target(result, 11).Damage, 1, 2);
        });
        yield return ("Magma poisonous positive packets kill minions but do not poison heroes", () =>
        {
            var frame = Tag(Frame(), 30, "POISONOUS", "1"); var result = Eval(frame, cards, State(frame));
            Check(result.IsEnumerationComplete && result.Branches.Count == 2, "Poison did not remove a hit minion from subsequent choices.");
            Bounds(Target(result, 20).Damage, 0, 1); Bounds(Target(result, 11).Damage, 1, 2);
            Check(result.Branches.Any(branch => branch.Targets.Single(target => target.EntityId == 20).RemainingHealth == 0), "Poison did not kill the damaged minion.");
        });
        yield return ("Magma lifesteal heals packet damage including armor with current max-health and no-healing limits", () =>
        {
            var frame = Tag(Frame(), 30, "LIFESTEAL", "1"); var state = State(frame); var result = Eval(frame, cards, state);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.SourceHeroHealthAfter == 22 && branch.LifestealHealing == 2), "Lifesteal did not match per-packet accepted damage.");
            state = Replace(state, Tag(frame.Entities[10], "DAMAGE", "1")); result = Eval(frame, cards, state);
            Check(result.Branches.All(branch => branch.SourceHeroHealthAfter == 30 && branch.LifestealHealing == 1), "Lifesteal exceeded the current maximum.");
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 10), "CANT_BE_HEALED", "1"));
            Check(Eval(frame, cards, state).Branches.All(branch => branch.LifestealHealing == 0), "CANT_BE_HEALED was ignored.");
        });
        yield return ("Magma enemy-hero death stops remaining packets and any later lifesteal", () =>
        {
            var frame = Tag(Frame(heroHealth: 1), 30, "LIFESTEAL", "1"); var result = Eval(frame, cards, State(frame));
            Check(result.IsEnumerationComplete && result.Branches.Any(branch => branch.StopReason == "HeroDied" && branch.PacketsApplied == 1 &&
                branch.PacketsRemaining == 1 && branch.SourceHeroHealthAfter == 21), "A dead hero received later damage or healed the source beyond the terminal packet.");
            Check(Target(result, 11).Survives is null, "A favorable hero-death branch became a guaranteed lethal result.");
        });
        yield return ("Magma native deathrattle and granted Reborn terminate only their affected branches without global invented bounds", () =>
        {
            var frame = Add(Frame(targetHealth: 3), Card(20, "EDR_227", 2, 1, 3)); var result = Eval(frame, cards, State(frame));
            Check(!result.IsEnumerationComplete && result.Branches.Any(branch => branch.StopReason == "DeathrattleOrReborn" && !branch.IsComplete) &&
                result.Branches.Any(branch => branch.IsComplete) && result.Targets.Count == 0, "Unknown death aftermath was ignored or bounded by an invalid packet-only ceiling.");
            frame = Tag(Frame(targetHealth: 3), 20, "REBORN", "1"); result = Eval(frame, cards, State(frame));
            Check(!result.IsEnumerationComplete && result.Targets.Count == 0, "Reborn created an invented replacement or finite final-health bounds.");
        });
        yield return ("Magma unmarked nonempty card text with mechanics empty is never treated as a fully audited pure target", () =>
        {
            var modified = cards.ToDictionary(); var native = cards["CS2_172"]; var raw = JsonNode.Parse(native.RawJson)!.AsObject();
            raw["text"] = "每当本随从受到伤害时，召唤一个随从。"; raw["mechanics"] = new JsonArray();
            modified[native.Id] = native with { Text = raw["text"]!.GetValue<string>(), RawJson = raw.ToJsonString() };
            var frame = Frame(); var result = Eval(frame, modified, State(frame));
            Check(!result.IsEnumerationComplete && result.Branches.Any(branch => branch.StopReason == "UncoveredTargetEffect") && result.Targets.Count == 0,
                "Unknown empty-mechanics text was treated as a pure minion or got fabricated global bounds.");
        });
        yield return ("Magma audited Macaw battlecry body has no damage trigger while altered native text still stops its branch", () =>
        {
            var frame = Add(Frame(), Card(20, "CORE_UNG_912", 2, 1, 10));
            Check(Eval(frame, cards, State(frame)).IsEnumerationComplete, "An audited enter-play-only Battlecry body was mistaken for a damage trigger.");
            var modified = cards.ToDictionary(); var native = cards["CORE_UNG_912"]; var raw = JsonNode.Parse(native.RawJson)!.AsObject();
            raw["text"] = "受到伤害后，召唤一个新随从。"; raw["mechanics"] = new JsonArray();
            modified[native.Id] = native with { Text = raw["text"]!.GetValue<string>(), RawJson = raw.ToJsonString() };
            var result = Eval(frame, modified, State(frame)); Check(!result.IsEnumerationComplete && result.Targets.Count == 0,
                "A same-ID altered body borrowed the native no-damage-trigger exception.");
        });
        yield return ("Magma immune multi-target eligibility remains unknown rather than consuming or excluding missiles", () =>
        {
            var frame = Tag(Frame(), 11, "IMMUNE", "1"); var result = Eval(frame, cards, State(frame));
            Check(!result.IsEnumerationComplete && result.Branches is [{ StopReason: "ImmuneTargetPolicyUnknown", PacketsApplied: 0 }] &&
                !result.SingleHeroStateAudited, "Unknown immune target selection became an exact distribution."); Bounds(Target(result, 11).Damage, 0, 0);
        });
        yield return ("Magma current and original silence immunity shield lifesteal aliases cannot be repaired by an overlay", () =>
        {
            foreach (var (named, numeric) in new[] { ("SILENCED", "188"), ("POISONOUS", "363"), ("LIFESTEAL", "685"), ("IMMUNE", "240"), ("DIVINE_SHIELD", "194"), ("IMMUNE_WHILE_ATTACKING", "373") })
            {
                var frame = Tag(Tag(Frame(), 30, named, "1"), 30, numeric, "0"); var state = Replace(State(frame), Remove(State(frame).EntitiesAfterCombat.Single(entity => entity.Id == 30), numeric));
                Check(!Eval(frame, cards, state).IsEnumerationComplete && !Eval(frame, cards, state).SingleHeroStateAudited, "Overlay repaired a conflicting original source flag.");
            }
            var targetFrame = Tag(Tag(Frame(), 20, "SILENCED", "1"), 20, "188", "0");
            Check(!Eval(targetFrame, cards, State(targetFrame)).IsEnumerationComplete, "Unknown target silence suppressed its printed keywords as definitely absent.");
        });
        yield return ("Magma prior unresolved exchange aftermath does not use the removed corpse as a settled target pool", () =>
        {
            var frame = Frame(); var result = Eval(frame, cards, State(frame) with { PriorResolutionPending = true });
            Check(result.TriggerActive is null && result.PriorResolutionPending && result.RequiresObservation && !result.SingleHeroStateAudited &&
                result.Branches.Count == 0 && result.Targets.Count == 0, "Pending earlier effects were skipped before random allocation.");
        });
        yield return ("Magma missing or stale exchange overlays cannot be inferred from an attack-before snapshot", () =>
        {
            var frame = Frame(); Check(!Eval(frame, cards).ResolvesBranches, "Default helper duplicated the attack engine."); var state = State(frame);
            foreach (var bad in new[] { state with { MatchId = "PRIVATE_OTHER_MATCH" }, state with { StateVersion = 11 }, state with { EventSequence = 101 },
                state with { SourceEntityId = 31 }, state with { AttackedEntityId = 21 } })
                Check(!Eval(frame, cards, bad).ResolvesBranches && !JsonSerializer.Serialize(Eval(frame, cards, bad)).Contains("PRIVATE_OTHER", StringComparison.Ordinal), "Unbound overlay was used or echoed.");
        });
        yield return ("Magma participant completeness and identity guards reject missing duplicate new and hidden entities", () =>
        {
            var frame = Frame(); var state = State(frame);
            foreach (var bad in new[] { state with { EntitiesAfterCombat = state.EntitiesAfterCombat.Where(entity => entity.Id != 20).ToArray() },
                state with { EntitiesAfterCombat = state.EntitiesAfterCombat.Append(state.EntitiesAfterCombat[0]).ToArray() },
                state with { EntitiesAfterCombat = state.EntitiesAfterCombat.Append(Card(90, "PRIVATE_MISSING", 2, 1, 1)).ToArray() },
                Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 20), "ZONE", "HAND")),
                Replace(state, state.EntitiesAfterCombat.Single(entity => entity.Id == 20) with { CardId = "PRIVATE_NEW_IDENTITY" }) })
            { var result = Eval(frame, cards, bad); Check(!result.ResolvesBranches && !JsonSerializer.Serialize(result).Contains("PRIVATE_", StringComparison.Ordinal), "Incomplete/private participants created a fabricated target pool."); }
        });
        yield return ("Magma native source half version DBF and text audit rejects substitutions before trigger activation", () =>
        {
            var frame = Frame(); foreach (var card in new[] { cards["FIR_953"] with { DbfId = 1 }, cards["FIR_953"] with { Text = "Split" }, cards["FIR_953"] with { RawJson = "[]" } })
            { var modified = cards.ToDictionary(); modified["FIR_953"] = card; Check(!MagmaSplitRules.IsAuditedSource(card) && Eval(frame, modified, State(frame)).TriggerActive is null, "Unreviewed source executed the native trigger."); }
            foreach (var key in new[] { "4572", "4573", "CRAFTABLE_FIRST_HALF_DBID", "CRAFTABLE_SECOND_HALF_DBID" })
            { var custom = Tag(frame, 30, key, "1"); Check(Eval(custom, cards, State(custom)).TriggerActive is null, "Assembled source reused native FIR953 text."); }
            Check(!Eval(frame with { ClientBuild = "255791" }, cards, State(frame)).ResolvesBranches, "Wrong version executed fixed native split.");
        });
        yield return ("Magma source death silence dormancy and attacks on heroes give a confirmed zero native trigger", () =>
        {
            var frame = Frame(); var state = State(frame);
            foreach (var entity in new[] { Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "DAMAGE", "8"),
                Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "SILENCED", "1"), Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "DORMANT", "1") })
            { var result = Eval(frame, cards, Replace(state, entity)); Check(result.TriggerActive == false && result.IsEnumerationComplete && result.RequestedPackets == 0, "Inactive native source still emitted random packets."); }
            var face = MagmaSplitRules.Evaluate(frame, 30, 11, cards, state with { AttackedEntityId = 11 }); Check(face.TriggerActive == false, "A hero attack triggered Magma's minion-only text.");
        });
        yield return ("Magma public opposing sources use their own hero for lifesteal and the other controller for random targets", () =>
        {
            var frame = Tag(Tag(Frame(), 30, "CONTROLLER", "2"), 20, "CONTROLLER", "1"); frame = Tag(frame, 30, "LIFESTEAL", "1");
            var result = Eval(frame, cards, State(frame)); Check(result.IsEnumerationComplete && result.Targets.Any(target => target.EntityId == 10) &&
                !result.Targets.Any(target => target.EntityId == 11) && result.Branches.All(branch => branch.SourceHeroHealthAfter == 12), "Forced public opposing source used local/enemy hero roles backwards.");
        });
        yield return ("Magma single-hero audit gates remain available for closed-form large Attack and immunity without guessing their packet protocol", () =>
        {
            var frame = Frame(); var state = State(frame); state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 20), "ZONE", "GRAVEYARD"));
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 30), "ATK", int.MaxValue.ToString()));
            Check(Eval(frame, cards, state).SingleHeroStateAudited, "A fully observed huge Attack lost the safe single-hero audit gate.");
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 11), "IMMUNE", "1"));
            Check(Eval(frame, cards, state).SingleHeroStateAudited, "Unique immune hero could not retain its zero-damage audit gate.");
            state = Replace(state, Tag(state.EntitiesAfterCombat.Single(entity => entity.Id == 11), "240", "0"));
            Check(!Eval(frame, cards, state).SingleHeroStateAudited, "Contradictory immune aliases enabled the old fast path.");
        });
        yield return ("Magma packet cap and terminal-state cap never promote an enumerated subset into complete finite extrema", () =>
        {
            var frame = Frame(attack: 17, targetHealth: 100, heroHealth: 100); var result = Eval(frame, cards, State(frame));
            Check(!result.IsEnumerationComplete && result.Branches.Single().StopReason == "PacketLimit" && Target(result, 11).Damage.Maximum == 17, "Packet cap became a sampled exact answer.");
            frame = Frame(attack: 8, targetHealth: 100, heroHealth: 100);
            for (var id = 21; id <= 26; id++) frame = Add(frame, Card(id, "CS2_172", 2, 1, 100));
            result = Eval(frame, cards, State(frame));
            Check(!result.IsEnumerationComplete && result.Branches.Count <= MagmaSplitRules.MaximumStates &&
                result.Targets.Count == 8 && result.Targets.All(target => target.Damage.Minimum == 0 && target.Damage.Maximum == 8),
                "Terminal-only overflow silently dropped branches or used their narrower extrema as the full distribution.");
        });
        yield return ("Magma raw inputs and explanatory collections stay immutable and spectator identities are not projected", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var sourceCards = JsonSerializer.Serialize(cards); var list = State(frame).EntitiesAfterCombat.ToList();
            var state = new MagmaSplitState(frame.MatchId, 10, 100, 30, 20, list); list.Clear(); var result = Eval(frame, cards, state);
            Check(result.IsEnumerationComplete && before == JsonSerializer.Serialize(frame) && sourceCards == JsonSerializer.Serialize(cards), "Caller mutation or evaluation changed frozen inputs.");
            var spectator = Eval(frame with { IsSpectating = true }, cards, state); Check(!spectator.ResolvesBranches && spectator.Targets.Count == 0, "Spectator received private target data.");
            try { ((IList<MagmaSplitBranch>)result.Branches).Clear(); throw new Exception("Mutable Magma branches."); } catch (NotSupportedException) { }
        });
    }

    private static MagmaSplitOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string,CardRecord> cards, MagmaSplitState? state = null) => MagmaSplitRules.Evaluate(frame, 30, 20, cards, state);
    private static MagmaTargetRange Target(MagmaSplitOutcome result, int id) => result.Targets.Single(target => target.EntityId == id);
    private static GameSnapshot Frame(int attack = 2, int targetHealth = 10, int heroHealth = 10)
    {
        var entities = new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Hero(10, 1, 20), Hero(11, 2, heroHealth), Card(30, "FIR_953", 1, attack, 8), Card(20, "CS2_172", 2, 1, targetHealth) };
        return new("magma-fixture", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static MagmaSplitState State(GameSnapshot frame) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, 30, 20,
        frame.Entities.Values.Where(entity => entity.Kind == EntityKind.Card).Select(entity => entity.Id == 30 ? Tag(entity, "DAMAGE", "1") :
            entity.Id == 20 ? Tag(entity, "DAMAGE", "2") : entity).ToArray());
    private static EntitySnapshot Hero(int id, int owner, int health) => new(id, "HERO_" + owner, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = "PLAY", ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "HERO", ["HEALTH"] = Math.Max(30, health).ToString(),
            ["DAMAGE"] = (Math.Max(30, health) - health).ToString(), ["ARMOR"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Card(int id, string card, int owner, int attack, int health) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["ZONE"] = "PLAY", ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION", ["CARDRACE"] = "20", ["ATK"] = attack.ToString(),
            ["HEALTH"] = health.ToString(), ["DAMAGE"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static EntitySnapshot Remove(EntitySnapshot entity, string key)
    { var tags = entity.Tags.ToDictionary(); tags.Remove(key); return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string value) => Add(frame, Tag(frame.Entities[id], key, value));
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static MagmaSplitState Replace(MagmaSplitState state, EntitySnapshot entity) => state with { EntitiesAfterCombat = state.EntitiesAfterCombat.Select(value => value.Id == entity.Id ? entity : value).ToArray() };
    private static IReadOnlyDictionary<string,CardRecord> Cards()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "magma-split-255790.json")));
        Check(document.RootElement.GetProperty("build").GetString() == "255790", "Unpinned Magma fixture.");
        return document.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
            raw.TryGetProperty("text", out var text) ? text.GetString()! : "", raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Bounds(MagmaNumberRange range, long min, long? max) => Check(range.Minimum == min && range.Maximum == max, $"Expected {min}..{max}, got {range.Minimum}..{range.Maximum}.");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
