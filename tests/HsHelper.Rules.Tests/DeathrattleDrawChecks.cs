using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DeathrattleDrawChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Fixed = new(ReadCards);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("deathrattle draw exact enchantment provenance differs from its spell button", () =>
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "deathrattle-draw-255790.json")));
            Check(fixture.RootElement.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
                fixture.RootElement.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8", "Fixed evidence lost its primary hashes.");
            var card = Fixed.Value["EDR_100t4e"];
            Check(card.DbfId == 113807 && card.Type == "ENCHANTMENT" && NativeCardDefinitions.IsAudited("255790", card.Id, card), "Enchantment identity lost full native audit.");
            var frame = Set(Frame(), 50, ("TAG_SCRIPT_DATA_NUM_6", "113800"));
            Check(!Eval(frame).ResolvesEffects, "A static gift button pointer was invented for this enchantment.");
            Check(!Eval(Set(Frame(), 50, ("IS_NIGHTMARE_BONUS", "0"))).ResolvesEffects, "Contradictory nightmare identity was ignored.");
        });
        yield return ("deathrattle full hand burns two without freeing a slot for the dying minion", () =>
        {
            var result = Eval(Frame(hand: 10, deck: 2));
            Check(result.ResolvesEffects && result.Kind == "DeathrattleOrdinary" && result.SourceEntityId == 30 && result.TriggerEntityId == 30 &&
                result.SourceCardId == "EDR_852" && result.EnchantmentEntityId == 50 && result.EnchantmentCardId == "EDR_100t4e", "Body and effect provenance were conflated.");
            Exact(result.RequestedDraws, 2); Exact(result.CardsRemovedFromDeck, 2); Exact(result.CardsAddedToHand, 0); Exact(result.CardsBurned, 2);
            Exact(result.HandCountAfter!, 10); Exact(result.DeckCountAfter!, 0); Exact(result.FatigueDamage, 0);
            Check(!result.CanEvaluateFollowupActions && result.IsDirectEffectOnly && !result.RuleCoverage.Contains("source-leaves-hand-before-draw"), "Deathrattle borrowed a played-body hand slot.");
        });
        yield return ("deathrattle one card and existing fatigue draw once then take the next fatigue packet", () =>
        {
            var result = Eval(Frame(hand: 9, deck: 1, fatigue: 2));
            Exact(result.CardsAddedToHand, 1); Exact(result.CardsBurned, 0); Exact(result.EmptyDeckAttempts, 1);
            Exact(result.FatigueDamage, 3); Exact(result.FatigueAfter!, 3); Exact(result.HeroHealthAfter!, 27); Exact(result.HandCountAfter!, 10);
            var armor = Eval(Frame(hand: 9, deck: 1, fatigue: 2, armor: 2));
            Exact(armor.HeroHealthAfter!, 29); Exact(armor.HeroArmorAfter!, 0);
        });
        yield return ("deathrattle fatigue respects immunity and stops after the first lethal packet", () =>
        {
            var frame = Frame(deck: 0, fatigue: 2, health: 1);
            var dead = Eval(frame); Exact(dead.EmptyDeckAttempts, 1); Exact(dead.FatigueDamage, 3); Exact(dead.FatigueAfter!, 3);
            Exact(dead.HeroHealthAfter!, 0); Check(dead.MayStopAtHeroDeath, "A second packet was forced after hero death.");
            var immune = Eval(Set(frame, 10, ("IMMUNE", "1"))); Exact(immune.EmptyDeckAttempts, 2);
            Exact(immune.FatigueDamage, 0); Exact(immune.FatigueAfter!, 4); Exact(immune.HeroHealthAfter!, 1);
            var prevented = Eval(Set(frame, 2, ("CANT_BE_FATIGUED", "1"))); Exact(prevented.FatigueAfter!, 2); Exact(prevented.EmptyDeckAttempts, 2);
        });
        yield return ("deathrattle missing or contradictory count fatigue and hero aliases retain ranges", () =>
        {
            var frame = Set(Frame(hand: 9), 2, ("NUM_CARDS_IN_HAND", "99"));
            var unknown = Eval(frame); Check(unknown.ResolvesEffects && unknown.HandCountAfter is null && !unknown.CardsAddedToHand.IsExact, "Contradictory counts became an exact hand capacity.");
            unknown = Eval(RemoveTag(Frame(hand: 9), 2, "MAXHANDSIZE")); Check(!unknown.CardsAddedToHand.IsExact, "A missing hand limit defaulted to ten.");
            unknown = Eval(Set(Frame(deck: 0, fatigue: 2), 2, ("22", "3"))); Check(unknown.FatigueDamage.Maximum is null, "A contradictory fatigue alias became a convenient exact counter.");
            unknown = Eval(Set(Frame(deck: 0), 10, ("45", "1"))); Check(unknown.HeroHealthAfter is null, "A conflicting hero health alias was repaired by the named value.");
            unknown = Eval(Set(Frame(deck: 0), 10, ("IMMUNE", "2"))); Check(!unknown.FatigueDamage.IsExact, "An invalid immunity value became true.");
        });
        yield return ("deathrattle requires a current friendly play body and coherent effect ownership and attachment", () =>
        {
            var frame = Frame();
            foreach (var (id, tag, value) in new[] { (30, "ZONE", "HAND"), (30, "ZONE", "GRAVEYARD"), (30, "CONTROLLER", "2"),
                (30, "CARDTYPE", "SPELL"), (30, "202", "5"), (30, "50", "2"), (30, "49", "3"), (30, "ENTITY_ID", "999"),
                (50, "CONTROLLER", "2"), (50, "50", "2"), (50, "CARDTYPE", "SPELL"), (50, "202", "4"), (50, "ATTACHED", "31"),
                (50, "40", "31"), (50, "ZONE", "SETASIDE"), (50, "49", "3"), (50, "ENTITY_ID", "30"), (50, "HEALTH", "0") })
                Check(!Eval(Set(frame, id, (tag, value))).ResolvesEffects, $"Unconfirmed source binding passed: {id}/{tag}.");
            Check(!Eval(Replace(frame, frame.Entities[50] with { CardId = "EDR_100t4" })).ResolvesEffects, "The spell button borrowed the enchantment's effect.");
            Check(!Eval(frame with { ClientBuild = "255791" }).ResolvesEffects && !Eval(frame with { IsSpectating = true }).ResolvesEffects, "Wrong build or spectator state passed.");
        });
        yield return ("deathrattle raw aliases corroborate actual tags without discarding contradictory evidence", () =>
        {
            var frame = Set(Frame(), 30, ("217", "1"), ("45", "1"), ("44", "0"), ("202", "4"));
            frame = Set(frame, 50, ("40", "30"), ("49", "1"), ("50", "1"), ("202", "6"), ("3567", "1"), ("330", "1"));
            Check(Eval(frame).ResolvesEffects && Eval(RemoveTag(frame, 30, "DEATHRATTLE")).ResolvesEffects, "A coherent observed numeric deathrattle lost its current meaning.");
            foreach (var value in new[] { "0", "2", "bad" }) Check(!Eval(Set(frame, 30, ("217", value))).ResolvesEffects, "A bad deathrattle alias was discarded.");
            Check(!Eval(Set(frame, 50, ("3567", "0"))).ResolvesEffects, "A bad nightmare alias was discarded.");
        });
        yield return ("deathrattle suppression duplicate reborn and native additional deathrattles remain unresolved", () =>
        {
            foreach (var (tag, value) in new[] { ("DEATHRATTLE", "0"), ("SILENCED", "1"), ("REBORN", "1"), ("DORMANT", "1"), ("DAMAGE", "1") })
                Check(!Eval(Set(Frame(), 30, (tag, value))).ResolvesEffects, "A suppressed or already dead body passed.");
            foreach (var tag in new[] { "EXTRA_MINION_DEATHRATTLES_BASE", "371", "EXTRA_DEATHRATTLES_BASE", "882", "EXTRA_DEATHRATTLES_ADDITIONAL", "1131", "CANT_TRIGGER_DEATHRATTLE", "1831" })
                foreach (var id in new[] { 2, 10, 30, 50 }) Check(!Eval(Set(Frame(), id, (tag, "1"))).ResolvesEffects, "Deathrattle modifier was ignored: " + tag);
            var duplicate = Entity(51, "EDR_100t4e", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "30"));
            Check(!Eval(Replace(Frame(), duplicate)).ResolvesEffects, "Two draw sources were collapsed into one activation.");
            Check(!Eval(Replace(Frame(), Frame().Entities[30] with { CardId = "EDR_227" })).ResolvesEffects, "A native extra deathrattle was treated as a single gifted source.");
        });
        yield return ("deathrattle complete metadata rejects raw changes IDs types and body provenance edits", () =>
        {
            foreach (var id in new[] { "EDR_100t4e", "EDR_852" })
            {
                var card = Fixed.Value[id];
                foreach (var bad in new[] { card with { DbfId = 1 }, card with { Type = "SPELL" }, card with { Name = "same-looking" },
                    card with { RawJson = "{}" }, card with { RawJson = "broken" }, card with { RawJson = new string(' ', 65537) } })
                {
                    var cards = Fixed.Value.ToDictionary(); cards[id] = bad; var result = Eval(Frame(), cards);
                    Check(!result.ResolvesEffects && result.EnchantmentEntityId is null && result.EnchantmentCardId is null,
                        "A partial or changed record borrowed a fixed effect or published an unaudited attachment binding.");
                }
                var raw = JsonNode.Parse(card.RawJson)!.AsObject(); raw["extraUnreviewedEffect"] = 1;
                var changed = Fixed.Value.ToDictionary(); changed[id] = card with { RawJson = raw.ToJsonString() };
                Check(!Eval(Frame(), changed).ResolvesEffects, "A coherent but unaudited raw extension passed.");
            }
        });
        yield return ("deathrattle unknown attachment draw damage and death order sources never upgrade a full preview", () =>
        {
            var frame = Frame();
            var attached = Entity(70, "UNKNOWN_ENCHANT", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "10"));
            Check(!Eval(Replace(frame, attached)).ResolvesEffects, "Unknown hero attachment could change fatigue after death.");
            Check(!Eval(Replace(frame, Entity(70, "UNKNOWN_BOARD", 2, "PLAY", "MINION", ("TRIGGER_VISUAL", "1")))).ResolvesEffects, "An unknown draw/death trigger was assumed inert.");
            foreach (var tag in new[] { "AURA", "TRIGGER_VISUAL" })
                Check(!Eval(Set(frame, 30, (tag, "1"))).ResolvesEffects, "An unknown trigger on the selected body was assumed inert: " + tag);
            Check(!Eval(Replace(frame, Entity(70, "EDR_480", 1, "PLAY", "MINION", ("AURA", "1"), ("362", "0")))).ResolvesEffects,
                "A known native identity repaired contradictory current aura evidence.");
            Check(!Eval(Replace(frame, Entity(70, null, 1, "SECRET", "SPELL"))).ResolvesEffects, "An unknown secret death trigger was assumed inert.");
            var overlay = State(frame) with { PriorResolutionPending = true };
            Check(!Eval(frame, overlay: overlay).ResolvesEffects, "An unobserved earlier resolution supplied exact later draw counts.");
            Check(!Eval(frame, overlay: State(frame) with { StateVersion = frame.StateVersion + 1 }).ResolvesEffects, "Future overlay counts were accepted.");
        });
        yield return ("deathrattle line allows the basic exchange and stops before any new card or later attack", () =>
        {
            var frame = Frame(hand: 10, deck: 2); var before = JsonSerializer.Serialize(frame);
            var first = Line(frame, new PlannedAction(ActionKind.Attack, 30, 40)); var preview = first.Result.DrawPreviews.Single();
            Check(first.AppliedActions == 1 && first.Result.DrawBoundaryReached && first.Verification == LineVerification.Conditional &&
                !first.IsLethal && !first.Result.Entities.Single(entity => entity.EntityId == 30).IsAlive, "The covered exchange did not stop at the draw boundary.");
            Exact(preview.CardsBurned, 2); Check(first.Result.Entities.Count(entity => entity.Zone == "HAND" && entity.ControllerId == 1) == 10, "A preview fabricated or removed a hand entity.");
            var later = Line(frame, new(ActionKind.Attack, 30, 40), new(ActionKind.Attack, 32, 11));
            Check(later.AppliedActions == 1 && later.Verification == LineVerification.Unsupported && later.UnsupportedMechanics.Contains("AWAIT_DRAW_SNAPSHOT") &&
                !later.IsLethal && later.Result.Entities.Single(entity => entity.EntityId == 11).RemainingHealth == 3, "A following lethal attack crossed the unresolved draw boundary.");
            Check(before == JsonSerializer.Serialize(frame), "Line preview rewrote the observed body or attachment.");
        });
        yield return ("deathrattle line hand capacity uses the actual prefix without a second source-hand subtraction", () =>
        {
            var frame = Frame(hand: 9, deck: 2);
            var hand = frame.Entities.Values.First(entity => entity.Zone == "HAND");
            frame = Replace(frame, hand with { CardId = "CS2_171" });
            frame = Set(frame, hand.Id, ("COST", "1"), ("HEALTH", "1"), ("ATK", "1"), ("DAMAGE", "0"));
            var result = Line(frame, new(ActionKind.PlayCard, hand.Id), new(ActionKind.Attack, 30, 40));
            Check(result.AppliedActions == 2 && result.Result.DrawBoundaryReached, "A covered source departure lost the later death boundary.");
            var preview = result.Result.DrawPreviews.Single(); Exact(preview.HandCountAfter!, 10); Exact(preview.CardsAddedToHand, 2); Exact(preview.CardsBurned, 0);
        });
        yield return ("deathrattle line fatigue starts from covered combat lifesteal and current armor", () =>
        {
            var frame = Set(Frame(deck: 0, fatigue: 0, health: 2), 30, ("ATK", "2"), ("LIFESTEAL", "1"));
            frame = Replace(frame, Entity(70, "EDR_100te", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "30"), ("TAG_SCRIPT_DATA_NUM_6", "113796"), ("IS_NIGHTMARE_BONUS", "1")));
            var result = Line(frame, new PlannedAction(ActionKind.Attack, 30, 40)); var preview = result.Result.DrawPreviews.Single();
            Check(result.AppliedActions == 1 && result.Result.DrawBoundaryReached && preview.ResolvesEffects, "Covered combat lost its direct deathrattle preview.");
            Exact(preview.FatigueDamage, 3); Exact(preview.HeroHealthAfter!, 1);
            Check(result.Result.Entities.Single(entity => entity.EntityId == 10).RemainingHealth == 4, "The before-draw entity projection did not preserve combat Lifesteal.");
        });
        yield return ("deathrattle reuses exact static gift audits on the same or unrelated current target", () =>
        {
            var frame = Set(Frame(), 30, ("CHARGE", "1"));
            var charge = Entity(70, "EDR_100t6e", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "30"), ("TAG_SCRIPT_DATA_NUM_6", "113802"), ("IS_NIGHTMARE_BONUS", "1"));
            frame = Replace(frame, charge);
            Check(Eval(frame).ResolvesEffects, "An audited static Charge source became an unknown draw/death trigger.");
            var line = Line(frame, new PlannedAction(ActionKind.Attack, 30, 40));
            Check(line.Result.DrawBoundaryReached && line.Result.DrawPreviews.Single().ResolvesEffects, "A shared observed gift body could not reach the direct draw boundary.");
            var other = Set(Frame(), 32, ("CHARGE", "1")); other = Replace(other, Entity(70, "EDR_100t6e", 1, "PLAY", "ENCHANTMENT", ("ATTACHED", "32"), ("TAG_SCRIPT_DATA_NUM_6", "113802"), ("IS_NIGHTMARE_BONUS", "1")));
            Check(Eval(other).ResolvesEffects, "A passive source on an unrelated observed body globally blocked the deathrattle.");
            Check(!Eval(Set(frame, 30, ("197", "0"))).ResolvesEffects, "A contradictory static grant borrowed its prior audit.");
            Check(!Eval(Set(frame, 70, ("CONTROLLER", "2"))).ResolvesEffects, "A wrong-owner static attachment passed the combined scope.");
            var bad = Fixed.Value.ToDictionary(); bad["EDR_100t6e"] = bad["EDR_100t6e"] with { RawJson = "{}" };
            Check(!Eval(frame, bad).ResolvesEffects, "A corrupted static attachment bypassed native metadata review.");
        });
        yield return ("deathrattle simultaneous other native deaths keep ordering unresolved regardless entity enumeration", () =>
        {
            var frame = Set(Frame(), 40, ("HEALTH", "1")); frame = Replace(frame, frame.Entities[40] with { CardId = "EDR_227" });
            foreach (var current in new[] { frame, frame with { Entities = frame.Entities.Reverse().ToFrozenDictionary() } })
            {
                var result = Line(current, new PlannedAction(ActionKind.Attack, 30, 40));
                Check(result.AppliedActions == 1 && result.Result.DrawBoundaryReached && !result.Result.DrawPreviews.Single().ResolvesEffects && !result.IsLethal,
                    "A guessed simultaneous death order upgraded one direct draw.");
            }
        });
        yield return ("deathrattle direct preview freezes its input and all projected collections", () =>
        {
            var frame = Frame(hand: 9, deck: 1, fatigue: 2); var before = JsonSerializer.Serialize(frame); var result = Eval(frame);
            foreach (var values in new[] { result.Unknowns, result.Assumptions, result.RuleCoverage })
                try { ((IList<string>)values).Add("changed"); throw new Exception("Draw result list remained mutable."); } catch (NotSupportedException) { }
            Check(before == JsonSerializer.Serialize(frame), "A hypothetical death or draw changed the original snapshot.");
            Check(!JsonSerializer.Serialize(result).Contains("PRIVATE_", StringComparison.Ordinal), "Unknown hand/deck identities escaped into the direct projection.");
        });
    }

    private static DrawResolutionOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord>? cards = null, DrawResolutionState? overlay = null) =>
        DrawResolutionRules.EvaluateDeathrattle(frame, 30, cards ?? Fixed.Value, overlay);
    private static LineEvaluation Line(GameSnapshot frame, params PlannedAction[] actions) => new LineEvaluator().EvaluateLine(frame, actions, Fixed.Value);
    private static DrawResolutionState State(GameSnapshot frame) => new(frame.MatchId, frame.StateVersion, frame.EventSequence,
        frame.Hand.Count(), 10, frame.Entities.Values.Count(entity => entity.Zone == "DECK"), frame.Entities[2].GetIntTag("FATIGUE"), frame.Entities[10].RemainingHealth, 0);
    private static GameSnapshot Frame(int hand = 1, int deck = 2, int fatigue = 0, int health = 30, int armor = 0)
    {
        var players = new EntitySnapshot[] { new(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"]="1",["HERO_ENTITY"]="10",["RESOURCES"]="10",["RESOURCES_USED"]="0",["NUM_CARDS_IN_HAND"]=hand.ToString(),["NUM_CARDS_IN_DECK"]=deck.ToString(),["MAXHANDSIZE"]="10",["FATIGUE"]=fatigue.ToString() }.ToFrozenDictionary()),
            new(3,null,EntityKind.Player,new Dictionary<string,string>{["PLAYER_ID"]="2",["HERO_ENTITY"]="11"}.ToFrozenDictionary()) };
        var entities = players.Concat(new[] { Entity(10,"HERO_05",1,"PLAY","HERO",("HEALTH","30"),("DAMAGE",(30-health).ToString()),("ARMOR",armor.ToString()),("CLASS","HUNTER")),
            Entity(11,"HERO_08",2,"PLAY","HERO",("HEALTH","3"),("DAMAGE","0"),("ARMOR","0"),("CLASS","MAGE")),
            Entity(30,"EDR_852",1,"PLAY","MINION",("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("NUM_TURNS_IN_PLAY","1"),("EXHAUSTED","0"),("DEATHRATTLE","1")),
            Entity(32,"CS2_172",1,"PLAY","MINION",("ATK","3"),("HEALTH","2"),("DAMAGE","0"),("NUM_TURNS_IN_PLAY","1"),("EXHAUSTED","0")),
            Entity(40,"CS2_172",2,"PLAY","MINION",("ATK","1"),("HEALTH","10"),("DAMAGE","0")),
            Entity(50,"EDR_100t4e",1,"PLAY","ENCHANTMENT",("ATTACHED","30")) }).ToList();
        for (var index=0;index<hand;index++) entities.Add(Entity(100+index,"PRIVATE_HAND",1,"HAND","MINION",("COST","0")));
        for (var index=0;index<deck;index++) entities.Add(Entity(200+index,null,1,"DECK","MINION"));
        return new("death-draw",10,100,5,1,1,GamePhase.Main,entities.ToFrozenDictionary(entity=>entity.Id),new(true,true,true,true,0,[])){ClientBuild="255790"};
    }
    private static EntitySnapshot Entity(int id,string? card,int owner,string zone,string type,params (string Name,string Value)[] values)
    { var tags=new Dictionary<string,string>{["CONTROLLER"]=owner.ToString(),["ZONE"]=zone,["CARDTYPE"]=type};foreach(var(name,value)in values)tags[name]=value;return new(id,card,EntityKind.Card,tags.ToFrozenDictionary()); }
    private static GameSnapshot Replace(GameSnapshot frame,EntitySnapshot entity)
    { var entities=frame.Entities.ToDictionary();entities[entity.Id]=entity;return frame with{Entities=entities.ToFrozenDictionary()}; }
    private static GameSnapshot Set(GameSnapshot frame,int id,params (string Name,string Value)[] values)
    { var tags=frame.Entities[id].Tags.ToDictionary();foreach(var(name,value)in values)tags[name]=value;return Replace(frame,frame.Entities[id] with{Tags=tags.ToFrozenDictionary()}); }
    private static GameSnapshot RemoveTag(GameSnapshot frame,int id,string name)
    { var tags=frame.Entities[id].Tags.ToDictionary();tags.Remove(name);return Replace(frame,frame.Entities[id] with{Tags=tags.ToFrozenDictionary()}); }
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","deathrattle-draw-255790.json")));
        return fixture.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(raw=>raw.GetProperty("id").GetString()!,raw=>new CardRecord(raw.GetProperty("id").GetString()!,raw.GetProperty("dbfId").GetInt32(),raw.GetProperty("name").GetString()!,raw.TryGetProperty("text",out var text)?text.GetString()!:"",raw.GetProperty("cardClass").GetString()!,raw.GetProperty("set").GetString()!,raw.GetProperty("type").GetString()!,raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,raw.TryGetProperty("collectible",out var collectible)&&collectible.GetBoolean(),[],[],raw.GetRawText()),StringComparer.Ordinal);
    }
    private static void Exact(DrawNumberRange range,long expected) => Check(range.Minimum==expected&&range.Maximum==expected,$"Expected [{expected},{expected}], got [{range.Minimum},{range.Maximum}].");
    private static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
}
