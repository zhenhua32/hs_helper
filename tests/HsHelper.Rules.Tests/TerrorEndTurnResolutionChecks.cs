using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class TerrorEndTurnResolutionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Terror enumerates correlated attack-order terminal states with real Lifesteal", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame);
            Check(result.TriggerActive == true && result.ResolvesBranches && result.IsEnumerationComplete && result.Branches.Count == 2 &&
                result.RequiresObservation && !result.CanEvaluateFollowupActions && !result.HasAuthoritativeNextState && result.IsLegal is null,
                "Multi-attacker end turn did not enumerate finite conditional correlated terminal states.");
            foreach (var branch in result.Branches)
                Check(branch.IsComplete && Row(branch, 21).RemainingHealth == 1 && !Row(branch, 30).IsAlive &&
                    (branch.Combats.Count == 1 && branch.OwnHeroHealthAfter == 24 && Row(branch, 22).RemainingHealth == 4 ||
                     branch.Combats.Count == 2 && branch.OwnHeroHealthAfter == 28 && !Row(branch, 22).IsAlive), "Independent marginal outcomes were combined into a fictitious board.");
            Check(JsonSerializer.Serialize(frame) == before && frame.Entities[30].GetIntTag("DAMAGE") == 0, "Preview changed the observed frame.");
        });
        yield return ("Terror merges equivalent full terminal states and keeps one witness rather than treating ID order as actual", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 20), Card(21, "CS2_172", 2, 2, 9), Card(22, "CORE_CS2_231", 2, 3, 9));
            var result = Eval(frame); var branch = result.Branches.Single();
            Check(result.IsEnumerationComplete && branch.Combats.Count == 2 && branch.OwnHeroHealthAfter == 28 && Row(branch, 30).RemainingHealth == 15 &&
                Row(branch, 21).RemainingHealth == 5 && Row(branch, 22).RemainingHealth == 5 && result.Assumptions.Any(value => value.Contains("见证", StringComparison.Ordinal)), "Merged terminal state lost correlated damage or witness semantics.");
        });
        yield return ("Terror seven attackers stay bounded and an eighth is not silently discarded", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 1, 100));
            frame = Replace(frame, Enumerable.Range(21, 7).Select(id => Card(id, "CS2_172", 2, 1, 10)).ToArray());
            var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.Count == 1 && result.Branches[0].Combats.Count == 7 && Row(result.Branches[0], 30).RemainingHealth == 93,
                "Seven interchangeable attackers were expanded into unbounded duplicate orders or omitted.");
            var invalid = Eval(Replace(frame, Card(28, "CS2_172", 2, 1, 10)));
            Check(!invalid.ResolvesBranches && invalid.Branches.Count == 0 && invalid.Unknowns.Count > 0, "An eighth attacker was dropped.");
        });
        yield return ("Terror inactive death silence dormancy and departure never execute a native trigger", () =>
        {
            foreach (var frame in new[] { Tag(Frame(), 30, "SILENCED", "1"), Tag(Frame(), 30, "DORMANT", "1"),
                Tag(Frame(), 30, "DAMAGE", "5"), Tag(Frame(), 30, "ZONE", "GRAVEYARD") })
            {
                var result = Eval(frame);
                Check(result.TriggerActive == false && result.IsEnumerationComplete && result.Branches.All(branch => branch.Combats.Count == 0) && !result.RequiresObservation,
                    "An inactive Terror forced attacks or invented a nonzero result.");
            }
        });
        yield return ("Terror alias conflicts and missing original observations cannot be repaired by a host overlay", () =>
        {
            foreach (var (id, tag, alias, value) in new[] { (30, "SILENCED", "188", "0"), (21, "IMMUNE", "240", "0"),
                (22, "LIFESTEAL", "685", "0"), (21, "HEALTH", "45", "5"), (30, "CARDRACE", "200", "15") })
            {
                var frame = Tag(Tag(Frame(), id, tag, value), id, alias, "1"); var overlay = State(Tag(frame, id, alias, value));
                Check(!TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, overlay).ResolvesBranches, "Overlay repaired raw " + tag + " evidence.");
            }
            var missing = Remove(Frame(), 21, "DAMAGE");
            var repaired = State(Tag(missing, 21, "DAMAGE", "0"));
            Check(TerrorEndTurnResolutionRules.Evaluate(missing, 30, cards, repaired).Branches.Count == 0, "Overlay filled an unobserved old body's damage with zero.");
        });
        yield return ("Terror requires actual current health damage attack and hero armor instead of printed defaults", () =>
        {
            foreach (var (id, tag) in new[] { (30, "ATK"), (21, "HEALTH"), (22, "DAMAGE"), (10, "ARMOR"), (11, "DAMAGE") })
                Check(Eval(Remove(Frame(), id, tag)).Branches.Count == 0, "Missing current " + tag + " was defaulted or printed.");
            Check(Eval(Tag(Frame(), 22, "DAMAGE", "bad")).Branches.Count == 0, "Invalid damage became zero.");
        });
        yield return ("Terror overlay binds the full same-frame entity collection and rejects omissions new IDs stale and future frames", () =>
        {
            var frame = Frame();
            foreach (var state in new[] { State(frame) with { StateVersion = frame.StateVersion - 1 }, State(frame) with { EventSequence = frame.EventSequence + 1 },
                State(frame) with { EntitiesAtEndTurn = frame.Entities.Values.Where(entity => entity.Id != 22).ToArray() },
                State(frame) with { EntitiesAtEndTurn = frame.Entities.Values.Append(Card(99, "CS2_172", 2, 1, 1)).ToArray() } })
                Check(!TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, state).ResolvesBranches, "An unbound or incomplete overlay was accepted.");
        });
        yield return ("Terror may use a bound successful native hand summon without inventing its hand damage or printed stats", () =>
        {
            var frame = Remove(Tag(Frame(), 30, "ZONE", "HAND"), 30, "DAMAGE");
            var after = Tag(Tag(frame, 30, "ZONE", "PLAY"), 30, "DAMAGE", "0");
            var result = TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, State(after));
            Check(result.IsEnumerationComplete && result.Branches.Count == 2, "A trustworthy native hand summon did not reach end-turn branches.");
            var unknownHand = Remove(frame, 30, "ATK");
            Check(!TerrorEndTurnResolutionRules.Evaluate(unknownHand, 30, cards, State(after)).ResolvesBranches, "Printed hand Attack repaired unknown original source values.");
        });
        yield return ("Terror pending prior resolution retains no falsely complete numeric end-turn branches", () =>
        {
            var frame = Frame(); var result = TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, State(frame) with { PriorResolutionPending = true });
            Check(result.TriggerActive == true && result.PriorResolutionPending && !result.ResolvesBranches && result.Branches.Count == 0 && result.RequiresObservation,
                "An unsettled prior effect was treated as an authoritative end-turn starting frame.");
        });
        yield return ("Terror shields poison immunity and armor are applied to each actual exchange", () =>
        {
            var frame = Replace(Frame(), Card(21, "CS2_172", 2, 8, 3), Card(22, "CORE_CS2_231", 2, 1, 3));
            frame = Tag(frame, 30, "DIVINE_SHIELD", "1"); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.Count == 2 && result.Branches.All(branch => branch.OwnHeroHealthAfter == 28 && !Row(branch, 21).IsAlive && !Row(branch, 22).IsAlive) &&
                result.Branches.Select(branch => Row(branch, 30).RemainingHealth).Order().SequenceEqual(new long[] { -3, 4 }), "The first shield consumed damage from a chosen fixed attacker or did not affect correlated survival.");
            frame = Tag(Tag(Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 100)), 21, "POISONOUS", "1"), 21, "IMMUNE", "1"); result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => !Row(branch, 30).IsAlive && Row(branch, 21).RemainingHealth == 5 &&
                branch.Combats.Single(combat => combat.AttackerEntityId == 21).DamageToAttacker == 0), "Poison or attacking immune recipient used ordinary health subtraction.");
            frame = Tag(Tag(Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 20), Card(21, "CS2_172", 2, 1, 10), Card(22, "CS2_172", 2, 1, 10)), 21, "ARMOR", "2"), 30, "ARMOR", "1"); result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => Row(branch, 30).RemainingHealth == 19 && Row(branch, 21).RemainingHealth == 8 && Row(branch, 21).RemainingArmor == 0), "Current armor was ignored in the actual combat path.");
        });
        yield return ("Terror Lifesteal heals the correct current heroes and respects caps and cannot-heal", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 20), Card(21, "CS2_172", 2, 3, 10), Card(22, "CS2_172", 2, 0, 10), Hero(11, 2, 20));
            frame = Tag(frame, 21, "LIFESTEAL", "1"); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.OwnHeroHealthAfter == 28 && branch.OpponentHeroHealthAfter == 23 && branch.LifestealHealing == 11), "Lifesteal healed the wrong hero or counted an overcap amount.");
            result = Eval(Tag(frame, 10, "CANT_BE_HEALED", "1"));
            Check(result.Branches.All(branch => branch.OwnHeroHealthAfter == 20 && branch.OpponentHeroHealthAfter == 23 && branch.LifestealHealing == 3), "Cannot-heal was ignored.");
        });
        yield return ("Terror native dual race gains Goldrinn doubling without overriding a changed current race", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 20), Card(21, "CS2_172", 2, 1, 20), Card(22, "CS2_172", 2, 1, 20), Card(40, "EDR_480", 1, 9, 9), Hero(10, 1, 1));
            frame = Tag(frame, 30, "CARDRACE", "15"); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.OwnHeroHealthAfter == 17 && Row(branch, 21).RemainingHealth == 12 && Row(branch, 22).RemainingHealth == 12), "Native Demon/Beast Terror lost its audited Beast race.");
            result = Eval(Tag(frame, 30, "CARDRACE", "14"));
            Check(result.Branches.All(branch => branch.OwnHeroHealthAfter == 9 && Row(branch, 21).RemainingHealth == 16), "A changed current race regained Beast from static races.");
        });
        yield return ("Terror uses Goldrinn before simultaneous death and removes its aura for later exchanges", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 30), Card(21, "EDR_480", 2, 1, 4), Card(22, "CS2_172", 2, 2, 10));
            frame = Tag(frame, 22, "CARDRACE", "20"); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.Select(branch => Row(branch, 30).RemainingHealth).Order().SequenceEqual(new long[] { 24, 26 }) &&
                result.Branches.All(branch => !Row(branch, 21).IsAlive && Row(branch, 22).RemainingHealth == 6), "Goldrinn was removed before its simultaneous hit or stayed active after death.");
        });
        yield return ("Terror unknown native deathrattles and reborn keep actual exchange prefixes without invented continuations", () =>
        {
            foreach (var frame in new[] { Replace(Frame(), Card(21, "EDR_227", 2, 1, 4)), Tag(Replace(Frame(), Card(21, "CS2_172", 2, 1, 4)), 21, "REBORN", "1") })
            {
                var result = Eval(frame);
                Check(!result.IsEnumerationComplete && !result.ResolvesBranches && result.RequiresObservation && result.Branches.Count > 0 &&
                    result.Branches.All(branch => !branch.IsComplete && branch.StopReason == "DeathOrRebornRequiresObservation" && branch.Combats.Count > 0 && !Row(branch, 21).IsAlive), "Unknown death effects lost direct damage or acquired a completed continuation.");
            }
        });
        yield return ("Terror a surviving enemy Magma Hound stops at its actual after-attack random boundary", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 20), Card(21, "FIR_953", 2, 2, 20)); var result = Eval(frame);
            Check(!result.IsEnumerationComplete && result.Branches.Count > 0 && result.Branches.All(branch => !branch.IsComplete && branch.StopReason == "AfterAttackRequiresObservation" &&
                branch.Combats.Any(combat => combat.AttackerEntityId == 21) && Row(branch, 21).RemainingHealth == 16), "A surviving Hound's random split was ignored or used as exact followup damage.");
        });
        yield return ("Terror replacement auras damage listeners and Lifesteal conversion refuse an unmodified numeric prefix", () =>
        {
            foreach (var card in new[] { "AT_124", "CORE_GVG_021", "DMF_230", "EX1_007", "ICC_031" })
            {
                var result = Eval(Replace(Frame(), Card(40, card, 1, 1, 9)));
                Check(!result.ResolvesBranches && result.Branches.Count == 0 && result.Unknowns.Count > 0, "Unclassified active " + card + " was ignored until after fabricated damage/healing.");
            }
        });
        yield return ("Terror unknown ownership type custom text and an additional live Terror cannot be omitted", () =>
        {
            foreach (var frame in new[] { Remove(Frame(), 21, "CONTROLLER"), Tag(Frame(), 22, "202", "SPELL"), Tag(Frame(), 21, "4573", "97376"),
                Replace(Frame(), Card(40, "CORE_TTN_866", 1, 4, 10)) })
                Check(!Eval(frame).ResolvesBranches, "A conflicting participant or extra end-turn source disappeared from the branch set.");
        });
        yield return ("Terror private opponent zones spectator and uncertain perspective never supply public combat identities", () =>
        {
            var privateCard = Card(90, "PRIVATE_TERROR_TEST", 2, 99, 99); privateCard = Change(privateCard, "ZONE", "HAND");
            var frame = Replace(Frame(), privateCard); var result = Eval(frame);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.Entities.All(entity => entity.EntityId != 90)) && !JsonSerializer.Serialize(result).Contains("PRIVATE_TERROR_TEST", StringComparison.Ordinal), "A hidden hand entered public combat branches.");
            foreach (var hidden in new[] { frame with { IsSpectating = true }, frame with { Quality = frame.Quality with { HasLocalPerspective = false } }, frame with { ClientBuild = "255789" } })
                Check(TerrorEndTurnResolutionRules.Evaluate(hidden, 30, cards).Branches.Count == 0, "Private or unaudited perspective produced combat branches.");
        });
        yield return ("Terror already dead heroes stop before any forced exchange", () =>
        {
            foreach (var id in new[] { 10, 11 })
            {
                var result = Eval(Tag(Frame(), id, "DAMAGE", "30"));
                Check(result.Branches.Count == 1 && result.Branches[0].StopReason == "HeroDied" && result.Branches[0].Combats.Count == 0, "A hero-dead state continued combat.");
            }
        });
        yield return ("Terror host collections are deep frozen and branch count obeys published resource limits", () =>
        {
            var frame = Frame(); var list = frame.Entities.Values.ToList(); var state = new TerrorEndTurnState(frame.MatchId, frame.StateVersion, frame.EventSequence, 30, list);
            list.Clear(); Check(state.EntitiesAtEndTurn.Count == frame.Entities.Count && TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards, state).IsEnumerationComplete, "Overlay aliased a caller list.");
            frame = Replace(frame, Card(30, "CORE_TTN_866", 1, 1, 65));
            frame = Tag(frame, 30, "DIVINE_SHIELD", "1");
            frame = Replace(frame, Enumerable.Range(0, 7).Select(index => Card(21 + index, "CS2_172", 2, 1 << index, 100)).ToArray());
            var result = Eval(frame);
            Check(result.Branches.Count <= TerrorEndTurnResolutionRules.MaximumBranches && result.Branches.All(branch => branch.Combats.Count <= 7) &&
                (result.IsEnumerationComplete || !result.ResolvesBranches && result.Unknowns.Count > 0), "Resource-limited output claimed a complete branch set or expanded past its cap.");
        });
        yield return ("Terror normal active hero powers and known stat enchants do not block complete native end-turn branches", () =>
        {
            foreach (var card in cards.Values.Where(card => card.Type == "HERO_POWER"))
            {
                var power = Change(Change(Card(50, card.Id, 1, 0, 1), "CARDTYPE", "HERO_POWER"), "COST", "2");
                Check(Eval(Replace(Frame(), power)).IsEnumerationComplete, "A fully audited active power blocked unrelated forced combat: " + card.Id);
            }
            foreach (var id in new[] { "EDR_850pe1", "EDR_850pe5", "DINO_421e" })
            {
                var enchant = Change(Change(Card(50, id, 1, 0, 1), "CARDTYPE", "ENCHANTMENT"), "ATTACHED", "30");
                var frame = Tag(Replace(Frame(), enchant), 30, "COST", "7");
                foreach (var zone in new[] { "PLAY", "SETASIDE" })
                    Check(Eval(Tag(frame, 50, "ZONE", zone)).IsEnumerationComplete,
                        "A fixed stat-only attached enchant blocked combat: " + id);
            }
        });
        yield return ("Terror only the known own Trap and sidequest are harmless secret-zone contexts for this native trigger", () =>
        {
            foreach (var id in new[] { "CORE_EX1_610", "TLC_EVENT_400" })
            {
                var secret = Change(Change(Card(50, id, 1, 0, 1), "CARDTYPE", "SPELL"), "ZONE", "SECRET");
                Check(Eval(Replace(Frame(), secret)).IsEnumerationComplete, "Known own hero-attack/after-play Secret context blocked unrelated Terror combat.");
                Check(!Eval(Replace(Frame(), Change(secret, "CONTROLLER", "2"))).ResolvesBranches, "An opposing Secret with potential death triggers was ignored.");
            }
        });
        yield return ("Terror numeric modifiers target restrictions and original modifier evidence cannot be silently removed", () =>
        {
            foreach (var tag in new[] { "274", "337", "236", "1675", "1", "326", "357" })
                Check(Eval(Tag(Frame(), 30, tag, "1")).Branches.Count == 0, "An explicit numeric damage/healing modifier was ignored: " + tag);
            foreach (var (name, alias) in new[] { ("IGNORE_DAMAGE", "1"), ("HEALING_DOES_DAMAGE", "326"), ("HEALING_DOUBLE", "357") })
                Check(Eval(Tag(Tag(Frame(), 30, name, "0"), 30, alias, "1")).Branches.Count == 0, "A named-zero modifier discarded its active numeric alias.");
            foreach (var (tag, alias) in new[] { ("UNTOUCHABLE", "448"), ("STEALTH", "191"), ("CANT_BE_ATTACKED", "245") })
                Check(Eval(Tag(Tag(Frame(), 30, tag, "0"), 30, alias, "1")).Branches.Count == 0, "A conflicting target restriction was collapsed to false: " + tag);
            var raw = Tag(Frame(), 2, "HEALTH_MINIMUM", "1");
            Check(!TerrorEndTurnResolutionRules.Evaluate(raw, 30, cards, State(Remove(raw, 2, "HEALTH_MINIMUM"))).ResolvesBranches, "Overlay erased a raw player damage modifier.");
            var enchant = Change(Change(Change(Card(50, "EDR_850pe1", 1, 0, 1), "CARDTYPE", "ENCHANTMENT"), "ATTACHED", "30"), "ZONE", "SETASIDE");
            Check(Eval(Tag(Replace(Frame(), enchant), 50, "HEALTH_MINIMUM", "1")).Branches.Count == 0, "An attached SETASIDE enchant's explicit modifier was omitted.");
            raw = Tag(Replace(Frame(), enchant), 50, "HEALTH_MINIMUM", "1");
            Check(!TerrorEndTurnResolutionRules.Evaluate(raw, 30, cards, State(Remove(raw, 50, "HEALTH_MINIMUM"))).ResolvesBranches,
                "Overlay deleted an original active SETASIDE enchant's damage modifier.");
            Check(Eval(Tag(Tag(Frame(), 2, "EXTRA_END_TURN_EFFECT", "0"), 30, "EXTRA_TURN_END_TRIGGERS", "0")).IsEnumerationComplete,
                "Explicitly expired zero end-turn modifiers still blocked ordinary combat.");
        });
        yield return ("Terror cannot revive an originally dead hero or skip a visible unresolved dead deathrattle", () =>
        {
            var raw = Tag(Frame(), 10, "DAMAGE", "30");
            Check(!TerrorEndTurnResolutionRules.Evaluate(raw, 30, cards, State(Tag(raw, 10, "DAMAGE", "0"))).ResolvesBranches, "Overlay revived a terminal hero.");
            var dead = Tag(Replace(Frame(), Card(40, "EDR_227", 1, 1, 1)), 40, "DAMAGE", "1");
            var result = Eval(dead); Check(!result.ResolvesBranches && result.PriorResolutionPending && result.Branches.Count == 0, "An already dying PLAY body lost its queued deathrattle.");
            var conflict = Tag(Tag(Frame(), 2, "CURRENT_PLAYER", "1"), 3, "CURRENT_PLAYER", "1");
            Check(!Eval(conflict).ResolvesBranches, "Contradictory current-player tags launched a turn boundary.");
        });
        yield return ("Terror known passive ordinary weapons remain unspent and never grant their keywords to forced minion attackers", () =>
        {
            foreach (var id in new[] { "CS2_106", "CORE_CS2_106" })
            {
                var weapon = Change(Change(Card(50, id, 2, 99, 2), "CARDTYPE", "WEAPON"), "DAMAGE", "1");
                weapon = Change(weapon, "POISONOUS", "1");
                var frame = Replace(Frame(), weapon); var before = JsonSerializer.Serialize(frame); var result = Eval(frame);
                Check(result.IsEnumerationComplete && result.Branches.Count == 2 && result.Branches.Any(branch => branch.OwnHeroHealthAfter == 28) &&
                    result.Branches.All(branch => branch.Entities.All(entity => entity.EntityId != 50)) && JsonSerializer.Serialize(frame) == before,
                    "An unrelated passive enemy weapon blocked combat, spent durability or gave Poisonous to its minion.");
                Check(!Eval(Tag(frame, 50, "DEATHRATTLE", "1")).ResolvesBranches, "An unobserved weapon deathrattle context was treated as passive.");
            }
        });
        yield return ("Terror positively observed Beast race survives missing metadata on a silenced body", () =>
        {
            var frame = Replace(Frame(), Card(30, "CORE_TTN_866", 1, 4, 100), Card(21, "CORE_CS2_231", 2, 5, 20),
                Card(22, "CS2_172", 2, 0, 20), Card(40, "EDR_480", 2, 0, 100));
            frame = Tag(Tag(frame, 21, "SILENCED", "1"), 21, "CARDRACE", "20");
            var missing = cards.Where(pair => pair.Key != "CORE_CS2_231").ToDictionary();
            var result = TerrorEndTurnResolutionRules.Evaluate(frame, 30, missing);
            Check(result.IsEnumerationComplete && result.Branches.All(branch => branch.Combats.Single(combat => combat.AttackerEntityId == 21).DamageToTerror == 10),
                "A missing printed definition erased an explicitly observed Beast type and halved its current Goldrinn damage.");
            var unknown = Remove(frame, 21, "CARDRACE");
            Check(!TerrorEndTurnResolutionRules.Evaluate(unknown, 30, missing).ResolvesBranches, "Missing definition plus missing current race became definitely non-Beast.");
        });
        TerrorEndTurnOutcome Eval(GameSnapshot frame) => TerrorEndTurnResolutionRules.Evaluate(frame, 30, cards);
    }

    private static TerrorEntityResult Row(TerrorEndTurnBranch branch, int id) => branch.Entities.Single(entity => entity.EntityId == id);
    private static TerrorEndTurnState State(GameSnapshot frame) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, 30, frame.Entities.Values.ToArray());
    private static GameSnapshot Frame() => new("terror-end-turn-check", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { Player(2, 1, 10), Player(3, 2, 11), Hero(10, 1, 20), Hero(11, 2, 30), Card(30, "CORE_TTN_866", 1, 4, 5),
            Card(21, "CS2_172", 2, 5, 5), Card(22, "CORE_CS2_231", 2, 1, 4) }.ToFrozenDictionary(entity => entity.Id),
        new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Player(int id, int owner, int hero) => new(id, null, EntityKind.Player,
        new Dictionary<string, string> { ["PLAYER_ID"] = owner.ToString(), ["CONTROLLER"] = owner.ToString(), ["HERO_ENTITY"] = hero.ToString() }.ToFrozenDictionary());
    private static EntitySnapshot Hero(int id, int owner, int health) => new(id, "HERO_0" + owner, EntityKind.Card,
        new Dictionary<string, string> { ["CARDTYPE"] = "HERO", ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["HEALTH"] = "30", ["DAMAGE"] = (30 - health).ToString(), ["ARMOR"] = "0", ["ATK"] = "0" }.ToFrozenDictionary());
    private static EntitySnapshot Card(int id, string cardId, int owner, int attack, int health) => new(id, cardId, EntityKind.Card,
        new Dictionary<string, string> { ["CARDTYPE"] = "MINION", ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["HEALTH"] = health.ToString(), ["DAMAGE"] = "0", ["ATK"] = attack.ToString() }.ToFrozenDictionary());
    private static GameSnapshot Replace(GameSnapshot frame, params EntitySnapshot[] entities)
    { var values = frame.Entities.ToDictionary(); foreach (var entity in entities) values[entity.Id] = entity; return frame with { Entities = values.ToFrozenDictionary() }; }
    private static EntitySnapshot Change(EntitySnapshot entity, string key, string value)
    { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string value) => Replace(frame, Change(frame.Entities[id], key, value));
    private static GameSnapshot Remove(GameSnapshot frame, int id, string key)
    { var tags = frame.Entities[id].Tags.ToDictionary(); tags.Remove(key); return Replace(frame, frame.Entities[id] with { Tags = tags.ToFrozenDictionary() }); }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "AGENTS.md"))) workspace = workspace.Parent;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace!.FullName, "tests", "HsHelper.Rules.Tests", "Fixtures", "terror-end-turn-255790.json")));
        Check(document.RootElement.GetProperty("build").GetString() == "255790", "Terror fixture is not fixed255790.");
        return document.RootElement.GetProperty("cards").EnumerateArray().Select(card => new CardRecord(card.GetProperty("id").GetString()!, card.GetProperty("dbfId").GetInt32(),
            card.GetProperty("name").GetString()!, card.TryGetProperty("text", out var text) ? text.GetString()! : "", card.GetProperty("cardClass").GetString()!,
            card.GetProperty("set").GetString()!, card.GetProperty("type").GetString()!, card.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null,
            card.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], card.GetRawText())).ToDictionary(card => card.Id);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
