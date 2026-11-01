using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class AssembledRuntimeChecks
{
    private const string First = "CRAFTABLE_FIRST_HALF_DBID", Second = "CRAFTABLE_SECOND_HALF_DBID";
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Data = new(Cards);
    private static readonly LineEvaluator Rules = new();
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("assembled Runtime executes every 301 audited base in a complete handbuff charge attack chain", CompleteFamily);
        yield return ("assembled Runtime current attributes and donor roles remain per entity through two plays", CurrentAndPerEntity);
        yield return ("assembled Runtime respects ordinary single suppression and raw repeat evidence", TriggerEvidence);
        yield return ("assembled Runtime validates every hand target and all arithmetic before payment", HandBoundaries);
        yield return ("assembled Runtime never restores replaced native keywords damage listeners or end-turn effects", NativeReplacement);
        yield return ("assembled Runtime searches the known handbuff enabler and validates the same lethal chain", Search);
        yield return ("assembled Runtime keeps actual on-play quest and raptor boundaries after its own effect", OnPlay);
        yield return ("assembled Runtime current additional listeners and aliases cannot become a verified pure script", CurrentListeners);
        yield return ("assembled Runtime evidence is deeply frozen bound and absent for legacy native lines", Evidence);
    }
    private static void CompleteFamily()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assembled-scripts-255790.json")));
        var bases = fixture.RootElement.GetProperty("observedMinionBaseIds").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Check(bases.Length == 301, "Runtime family is not the complete audited body set.");
        foreach (var id in bases)
        {
            var frame = Replace(Frame(), Recipe(30, id, 113822, 121051)); var before = JsonSerializer.Serialize(frame);
            var result = Eval(frame, Play(30), Play(31), Attack(31));
            Check(result.Verification == LineVerification.Verified && result.IsLethal && result.Result.AssembledEffects is [{ ExecutedCount: 1 }] &&
                Entity(result, 31).Attack == 2 && Entity(result, 30).Attack == 3 && Entity(result, 30).MaximumHealth == 5 &&
                result.Result.DiscoveryPreviews.Count == 0 && result.Result.GenerationPreviews.Count == 0 && result.Result.DrawPreviews.Count == 0 &&
                result.Result.ImbueCountAfter == result.Result.ImbueCountBefore && JsonSerializer.Serialize(frame) == before,
                "A body reused printed stats/native scripts or blocked a fully known composed chain: " + id + "; " + result.Verification + "; " + string.Join(',', result.UnsupportedMechanics));
        }
    }
    private static void CurrentAndPerEntity()
    {
        var frame = Replace(Frame(), Recipe(32, "EDR_102", 0, 66858), Change(Boar(), "HEALTH", "8"));
        frame = Tag(frame, 31, "DAMAGE", "2"); var result = Eval(frame, Play(30), Play(32));
        Check(result.Verification == LineVerification.Verified && result.Result.AssembledEffects is { Count: 2 } effects &&
            effects[0].SourceEntityId == 30 && effects[0].EffectCardId == "TIME_100" && effects[1].SourceEntityId == 32 && effects[1].EffectCardId == "CS3_025" &&
            Entity(result, 31).Attack == 3 && Entity(result, 31).MaximumHealth == 10 && Entity(result, 31).RemainingHealth == 8 &&
            Entity(result, 32).Attack == 4 && Entity(result, 30).Attack == 3 && Entity(result, 31).Cost == 1,
            "Two same-ID recipes shared a profile or re-buffed their already played source, repaired damage or changed cost.");
        foreach (var (body, first, second) in new[] { ("TIME_100", 123623, 0), ("EDR_102", 69637, 66858) })
            Check(Eval(Replace(Frame(), Recipe(30, body, first, second)), Play(30)).Result.AssembledEffects is [{ ExecutedCount: 1 }],
                "A proven weapon/hero trigger donor or retained effect was incorrectly treated as the body type.");
    }
    private static void TriggerEvidence()
    {
        var ordinary = Eval(Frame(), Play(30)); Check(ordinary.Verification == LineVerification.Verified && ordinary.Result.AssembledEffects![0].ExecutedCount == 1,
            "The normal recipe required an extra count-one or BATTLECRY marker.");
        foreach (var (name, value) in new[] { ("BATTLECRY", "0"), ("SILENCED", "1"), ("218", "0"), ("188", "1") })
        {
            var result = Eval(Tag(Frame(), 30, name, value), Play(30));
            Check(result.Verification == LineVerification.Verified && result.Result.AssembledEffects is [{ ExecutedCount: 0, IsTriggerActive: false }] &&
                Entity(result, 31).Attack == 1 && Entity(result, 30).Zone == "PLAY", "Explicit suppression was ignored or blocked the physical play.");
        }
        foreach (var name in new[] { "EXTRA_BATTLECRIES_BASE", "EXTRA_MINION_BATTLECRIES_BASE", "EXTRA_BATTLECRIES_ADDITIONAL", "411", "1112", "1126" })
        {
            var result = Eval(Tag(Frame(), 2, name, "1"), Play(30));
            Check(result.Verification != LineVerification.Verified && Entity(result, 30).Zone == "HAND" && result.Result.AvailableMana == 10 && Entity(result, 31).Attack == 1,
                "A repeat modifier silently became one actual execution: " + name);
            Check(Eval(Tag(Frame(), 2, name, "0"), Play(30)).Verification == LineVerification.Verified, "An expired zero modifier blocked ordinary one-time play.");
        }
        var conflict = Eval(Tag(Tag(Frame(), 30, "BATTLECRY", "0"), 30, "218", "1"), Play(30));
        Check(conflict.Verification != LineVerification.Verified && Entity(conflict, 30).Zone == "HAND", "Conflicting suppression was chosen favorably.");
        foreach (var id in new[] { "EDR_100t7e", "EDR_100t7e1" })
        {
            var enchantment = Change(Change(Minion(50, id, "PLAY", 0, 1, 0), "CARDTYPE", "ENCHANTMENT"), "ATTACHED", "30");
            var result = Eval(Replace(Frame(), enchantment), Play(30));
            Check(result.Verification != LineVerification.Verified && Entity(result, 30).Zone == "HAND" &&
                result.Result.AvailableMana == 10 && result.Result.AssembledEffects is [{ ExecutedCount: null }],
                "An attached double-Battlecry observation without aggregate counters was declared one execution.");
        }
    }
    private static void HandBoundaries()
    {
        foreach (var frame in new[] { Remove(Frame(), 31, "ATK"), Remove(Frame(), 31, "HEALTH"), Remove(Frame(), 31, "DAMAGE"),
            Remove(Frame(), 31, "CARDTYPE"), Tag(Frame(), 31, "CARDTYPE", "INVALID"), Tag(Frame(), 31, "HEALTH", "0"),
            Tag(Frame(), 31, "DAMAGE", "1"), Tag(Frame(), 2, "NUM_CARDS_IN_HAND", "3"),
            Replace(Frame(), Boar() with { CardId = null }), Tag(Frame(), 31, "CARDTYPE", "SPELL"), Remove(Frame(), 30, "COST") })
        {
            var result = Eval(frame, Play(30));
            Check(result.Verification != LineVerification.Verified && result.Result.AvailableMana == 10 && Entity(result, 30).Zone == "HAND",
                "Missing or contradictory current hand facts were filled or silently skipped before applying the whole-set buff.");
        }
        var overflow = Replace(Frame(), Minion(32, "CS2_171", "HAND", int.MaxValue, 1, 1));
        var failed = Eval(overflow, Play(30));
        Check(failed.Verification == LineVerification.Unsupported && failed.AppliedActions == 0 && failed.Result.AvailableMana == 10 &&
            Entity(failed, 30).Zone == "HAND" && Entity(failed, 31).Attack == 1, "Overflow paid or applied an earlier target before rejecting the whole effect.");
        var empty = Frame() with { Entities = Frame().Entities.Where(pair => pair.Key != 31).ToFrozenDictionary() };
        Check(Eval(empty, Play(30)).Result.AssembledEffects is [{ ExecutedCount: 1, Targets.Count: 0 }], "A genuinely empty remaining hand invented a target or failed its completed trigger.");
    }
    private static void NativeReplacement()
    {
        foreach (var id in new[] { "FIR_953", "CORE_TTN_866", "BE_207" })
        {
            var frame = Replace(Frame(), Change(Recipe(30, id, 113822, 121051), "CHARGE", "1"), Minion(20, "CS2_172", "PLAY", 1, 20, 0, 2));
            var result = Eval(frame, Play(30), new(ActionKind.Attack, 30, 20), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.Result.MagmaSplitPreviews.Count == 0 && result.Result.TerrorEndTurnPreviews.Count == 0 &&
                result.Result.ForcedAttacks.Count == 0 && Entity(result, 30).RemainingHealth == 4 && Entity(result, 20).RemainingHealth == 17,
                "A replaced native attack/end-turn listener or passive keyword revived by CardId: " + id + "; " + string.Join(',', result.UnsupportedMechanics));
        }
        var terror = Replace(Frame(), Recipe(30, "CORE_TTN_866", 113822, 121051));
        var rejected = Eval(terror, Play(30), Attack(30));
        Check(rejected.Verification == LineVerification.Invalid && Entity(rejected, 30).AttacksUsed == 0,
            "A replaced body gained readiness or a printed keyword without an actual current observation.");
    }
    private static void Search()
    {
        var result = Rules.FindLethal(Tag(Frame(), 2, "RESOURCES", "5"), Data.Value, 3, 1000);
        Check(result.VerifiedLethalFound && result.Evaluation is { Verification: LineVerification.Verified, IsLethal: true } &&
            result.Actions.Count == 3 && result.Actions[0].SourceEntityId == 30 && result.Actions[1].SourceEntityId == 31 &&
            Eval(Tag(Frame(), 2, "RESOURCES", "5"), result.Actions.ToArray()).IsLethal, "Search did not use the same audited non-Charge handbuff enabler as manual/final validation.");
    }
    private static void OnPlay()
    {
        var raptor = Minion(40, "EDR_849", "PLAY", 2, 3, 0);
        var result = Eval(Replace(Frame(), raptor), Play(30), Play(31));
        Check(result.Verification != LineVerification.Verified && result.Result.AssembledEffects is [{ ExecutedCount: 1 }] && result.Result.RaptorBoundaryReached &&
            Entity(result, 31).Zone == "HAND" && Entity(result, 31).Attack == 2, "The composed battlecry swallowed or crossed an actual random after-play boundary.");
        var quest = Change(Change(Change(Minion(50, "TLC_EVENT_400", "SECRET", 0, 1, 1), "CARDTYPE", "SPELL"), "QUEST_PROGRESS", "0"), "QUEST_PROGRESS_TOTAL", "3");
        quest = Change(quest, "SIDE_QUEST", "1");
        result = Eval(Tag(Replace(Frame(), quest), 30, "CARDRACE", "DEMON"), Play(30), Play(31));
        Check(result.Result.QuestProgressPreviews.Count == 2 && result.Result.QuestProgressPreviews[0].PlayedCardQualifies == false &&
            result.Result.QuestProgressPreviews[1].ResultingProgress == 1, "Audited composed body lost ordinary on-play classification or post-effect quest progress.");
    }
    private static void CurrentListeners()
    {
        foreach (var tag in new[] { "TRIGGER_VISUAL", "AURA", "DEATHRATTLE", "REBORN", "32", "362", "217", "1085" })
        {
            var result = Eval(Tag(Frame(), 30, tag, "1"), Play(30));
            Check(result.Verification != LineVerification.Verified && !result.IsLethal, "Single-step play ignored current additional listener evidence: " + tag);
        }
        foreach (var tag in new[] { "CHARGE", "SILENCED", "BATTLECRY", "TAUNT" })
            Check(Eval(Tag(Frame(), 30, tag, "invalid"), Play(30)).Verification != LineVerification.Verified, "Malformed current flags disappeared before native fallback: " + tag);
    }
    private static void Evidence()
    {
        var original = Frame(); var before = JsonSerializer.Serialize(original); var result = Eval(original, Play(30)); var proof = result.Result.AssembledEffects!.Single();
        Check(proof.MatchId == original.MatchId && proof.StateVersion == original.StateVersion && proof.EventSequence == original.EventSequence && proof.CardBuild == "255790" &&
            proof.SourceEntityId == 30 && proof.FirstHalfDbfId == 0 && proof.SecondHalfDbfId == 121051 && proof.Targets.Single().EntityId == 31 &&
            proof.Targets[0] is { AttackBefore: 1, AttackAfter: 2, MaximumHealthBefore: 1, MaximumHealthAfter: 2 } && JsonSerializer.Serialize(original) == before,
            "Applied effect evidence lost its exact recipe state binding, real target changes or immutable input.");
        try { ((IList<AssembledEffectTarget>)proof.Targets).Clear(); throw new Exception("Mutable target proof."); } catch (NotSupportedException) { }
        try { ((IList<AssembledEffectOutcome>)result.Result.AssembledEffects!).Clear(); throw new Exception("Mutable effect proof."); } catch (NotSupportedException) { }
        var native = Eval(Replace(Frame(), Boar() with { Id = 30 }), Play(30));
        Check(!JsonSerializer.Serialize(native.Result).Contains("AssembledEffects"), "A legacy native line invented a composed execution ledger.");
    }
    private static GameSnapshot Frame() => new("assembled-runtime", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(2, null, EntityKind.Player, Tags(("PLAYER_ID", "1"), ("CONTROLLER", "1"), ("RESOURCES", "10"), ("RESOURCES_USED", "0"), ("HERO_ENTITY", "10"))),
            new EntitySnapshot(3, null, EntityKind.Player, Tags(("PLAYER_ID", "2"), ("CONTROLLER", "2"), ("HERO_ENTITY", "11"))),
            Hero(10, 1, 30), Hero(11, 2, 2), Recipe(30, "EDR_102", 0, 121051), Boar() }.ToFrozenDictionary(entity => entity.Id),
        new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Recipe(int id, string body, int first, int second) => Change(Change(Minion(id, body, "HAND", 3, 5, 4), First, first.ToString()), Second, second.ToString());
    private static EntitySnapshot Boar() => Change(Change(Minion(31, "CS2_171", "HAND", 1, 1, 1), "CHARGE", "1"), "CARDRACE", "BEAST");
    private static EntitySnapshot Minion(int id, string card, string zone, int attack, int health, int cost, int owner = 1) => new(id, card, EntityKind.Card,
        Tags(("CARDTYPE", "MINION"), ("CONTROLLER", owner.ToString()), ("ZONE", zone), ("ATK", attack.ToString()), ("HEALTH", health.ToString()), ("DAMAGE", "0"), ("COST", cost.ToString())));
    private static EntitySnapshot Hero(int id, int owner, int health) => new(id, "HERO_0" + owner, EntityKind.Card,
        Tags(("CARDTYPE", "HERO"), ("CONTROLLER", owner.ToString()), ("ZONE", "PLAY"), ("HEALTH", "30"), ("DAMAGE", (30 - health).ToString()), ("ATK", "0"), ("ARMOR", "0")));
    private static FrozenDictionary<string, string> Tags(params (string Key, string Value)[] tags) => tags.ToFrozenDictionary(pair => pair.Key, pair => pair.Value);
    private static PlannedAction Play(int id) => new(ActionKind.PlayCard, id);
    private static PlannedAction Attack(int id) => new(ActionKind.Attack, id, 11);
    private static LineEvaluation Eval(GameSnapshot frame, params PlannedAction[] actions) => Rules.EvaluateLine(frame, actions, Data.Value);
    private static SimulatedEntity Entity(LineEvaluation result, int id) => result.Result.Entities.Single(entity => entity.EntityId == id);
    private static EntitySnapshot Change(EntitySnapshot entity, string key, string value) => entity with { Tags = entity.Tags.Concat(new[] { new KeyValuePair<string, string>(key, value) }).GroupBy(pair => pair.Key).ToFrozenDictionary(group => group.Key, group => group.Last().Value) };
    private static GameSnapshot Replace(GameSnapshot frame, params EntitySnapshot[] entities) { var values = frame.Entities.ToDictionary(); foreach (var entity in entities) values[entity.Id] = entity; return frame with { Entities = values.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string value) => Replace(frame, Change(frame.Entities[id], key, value));
    private static GameSnapshot Remove(GameSnapshot frame, int id, string key) => Replace(frame, frame.Entities[id] with { Tags = frame.Entities[id].Tags.Where(pair => pair.Key != key).ToFrozenDictionary() });
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        var values = new Dictionary<string, CardRecord>();
        foreach (var name in new[] { "native-definitions-255790.json", "beast-hunter-255790.json", "lethal-search-255790.json", "dark-gift-static-255790.json", "terror-end-turn-255790.json", "assembled-scripts-255790.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
            foreach (var raw in fixture.RootElement.GetProperty("cards").EnumerateArray())
            {
                var id = raw.GetProperty("id").GetString()!;
                string Text(string key) => raw.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
                string[] List(string key) => raw.TryGetProperty(key, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
                values[id] = new(id, raw.GetProperty("dbfId").GetInt32(), Text("name"), Text("text"), Text("cardClass"), Text("set"), Text("type"),
                    raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), List("classes"), List("entourage"), raw.GetRawText());
            }
        }
        return values;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
