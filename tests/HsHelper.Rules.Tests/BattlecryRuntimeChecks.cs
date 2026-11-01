using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class BattlecryRuntimeChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Cards = new(ReadCards);
    private static readonly LineEvaluator Rules = new();
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("repeat Battlecry native Imbue closes the four-action five-mana chain and search", ImbueChain);
        yield return ("repeat Battlecry Archer aliases resolve shield poison lifesteal and fixed target packets", ArcherPackets);
        yield return ("repeat Battlecry Archer dead original target and self target fizzle without reentry", ArcherFizzle);
        yield return ("repeat Battlecry game end preserves a truthful unstarted remainder without losing lethal", HeroEnds);
        yield return ("repeat Battlecry unknown death or extra listener stops the joint activation prefix", UnknownDeath);
        yield return ("repeat Battlecry all 301 audited assembled bodies execute two buffs in the same complete chain", AssembledFamily);
        yield return ("repeat Battlecry recipes stay entity-local and total overflow is rejected before payment", RecipesAndOverflow);
        yield return ("repeat Battlecry after-play quests and native Raptor count the single card use once", PlayTriggers);
        yield return ("repeat Battlecry draw generation discovery and payment boundaries never complete a false second round", Boundaries);
        yield return ("repeat Battlecry known zero effects repeat completely and actual suppression executes zero", ZeroEffects);
        yield return ("repeat Battlecry execution evidence is immutable action-bound and absent for ordinary non-Battlecry play", Evidence);
    }

    private static void ImbueChain()
    {
        foreach (var id in new[] { "EDR_852", "EDR_800" })
        {
            var frame = Replace(Frame(), Minion(30,id,"HAND",2,3,2)); var before=JsonSerializer.Serialize(frame);
            var actions = new[] {Play(30),new PlannedAction(ActionKind.HeroPower,12),Play(31),Attack(31,11)};
            var result=Eval(frame,actions); var cry=Cry(result);
            Check(result.Verification==LineVerification.Verified && result.IsLethal && result.Result.AvailableMana==0 &&
                result.Result.HeroPowerChanges.Count==2 && result.Result.ImbueCountAfter==4 && Entity(result,31).Attack==5 && Entity(result,31).Cost==1 &&
                cry is {PlannedActivations:2,StartedActivations:2,CompletedActivations:2,RemainingActivations:0,IsComplete:true,ActionOrdinal:1} &&
                cry.EnchantmentEntityIds.SequenceEqual([50]) && JsonSerializer.Serialize(frame)==before,
                "Native repeated Imbue failed its actual complete four-action chain: "+id+"; "+result.Verification+"; "+string.Join(',',result.UnsupportedMechanics));
            var search=Rules.FindLethal(frame,Cards.Value,4,4000);
            Check(search.VerifiedLethalFound && search.Actions.Count==4 && search.Actions[0].SourceEntityId==30 && Eval(frame,search.Actions.ToArray()).IsLethal,
                "Search did not enumerate the repeated Imbue and unique-Beast hero-power enabler.");
        }
    }
    private static void ArcherPackets()
    {
        foreach(var id in new[]{"CS2_189","CORE_CS2_189"})
        {
            var frame=Replace(Frame(),Minion(30,id,"HAND",1,1,1),Tag(Minion(40,"CS2_172","PLAY",0,4,0,2),"DIVINE_SHIELD","1"));
            frame=Set(frame,30,"POISONOUS","1"); frame=Set(frame,30,"LIFESTEAL","1"); frame=Set(frame,10,"DAMAGE","5");
            var result=Eval(frame,Play(30,40));
            Check(result.Verification==LineVerification.Verified && !Entity(result,40).IsAlive && Entity(result,10).RemainingHealth==26 &&
                Cry(result) is {StartedActivations:2,CompletedActivations:2,IsComplete:true} && Cry(result).Activations.All(step=>step.Status=="Completed"),
                "Two Archer activations became a single two-damage packet, missed Poison/Lifesteal, or lost an alias.");
        }
        foreach(var race in new[]{"20","BEAST","PET","ALL","26"})
        {
            var beast=Set(Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1),Minion(45,"EDR_480","PLAY",3,3,0)),30,"CARDRACE",race);
            var amplified=Eval(beast,Play(30,11));
            Check(amplified.Verification==LineVerification.Verified && Entity(amplified,11).RemainingHealth==1,
                "Each Archer packet did not use current Beast/Goldrinn damage semantics: "+race+"; "+amplified.Verification+"; HP="+Entity(amplified,11).RemainingHealth+"; "+string.Join(',',amplified.UnsupportedMechanics));
            Check(Eval(Set(beast,30,"200",race is "ALL" or "26"?"26":"20"),Play(30,11)).Verification==LineVerification.Verified,
                "Equivalent current race aliases became a contradiction.");
            foreach(var bad in new[]{Set(beast,30,"200","15"),Set(beast,30,"CARDRACE","unknown"),Set(beast,30,"CARDRACE","999")})
                Check(Eval(bad,Play(30,11)).Verification!=LineVerification.Verified,"An explicit unknown/conflicting current race borrowed a favorable fallback.");
        }
        foreach(var keyword in new[]{"LIFESTEAL","POISONOUS"})
        {
            var malformed=Set(Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1)),30,keyword,"2");
            var rejected=Eval(malformed,Play(30,11));
            Check(rejected.Verification!=LineVerification.Verified && Entity(rejected,30).Zone=="HAND" && Entity(rejected,11).RemainingHealth==5,
                "A malformed Battlecry damage keyword became true: "+keyword);
        }
    }
    private static void ArcherFizzle()
    {
        var frame=Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1),Minion(40,"CS2_172","PLAY",0,1,0,2));
        var result=Eval(frame,Play(30,40)); var cry=Cry(result);
        Check(result.Verification==LineVerification.Verified && Entity(result,11).RemainingHealth==5 &&
            cry is {StartedActivations:2,CompletedActivations:2,IsComplete:true} && cry.Activations[1].Status=="Fizzled",
            "A dead original target was hit again or replaced by the hero.");
        frame=Set(Replace(frame,Tag(Minion(31,"CS2_171","HAND",1,1,1),"CHARGE","1")),11,"DAMAGE","29");
        result=Eval(frame,Play(30,30),Play(31),Attack(31,11));
        Check(result.Verification==LineVerification.Verified && result.IsLethal && Entity(result,30).Zone=="GRAVEYARD" &&
            Cry(result).Activations[1].Status=="Fizzled" && Entity(result,31).AttacksUsed==1,
            "The source's queued self-target either paid/entered twice or left its audited attachment active after death.");
    }
    private static void HeroEnds()
    {
        var frame=Set(Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1)),11,"DAMAGE","29");
        var result=Eval(frame,Play(30,11)); var cry=Cry(result);
        Check(result.IsLethal && result.Verification==LineVerification.Verified && cry is
            {PlannedActivations:2,StartedActivations:1,CompletedActivations:1,RemainingActivations:1,IsComplete:true,StopReason:"GameEnded"},
            "A terminal first activation either fabricated a second packet or downgraded a proved lethal.");
        result=Eval(Set(frame,10,"DAMAGE","29"),Play(30,10));
        Check(!result.IsLethal && Entity(result,10).RemainingHealth==0 && Cry(result).StartedActivations==1,
            "Own hero death still started the remaining activation.");
    }
    private static void UnknownDeath()
    {
        var frame=Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1),Tag(Minion(40,"CS2_172","PLAY",0,1,0,2),"DEATHRATTLE","1"));
        var result=Eval(frame,Play(30,40));
        Check(result.Verification!=LineVerification.Verified && Cry(result) is {StartedActivations:1,CompletedActivations:0,IsComplete:false} &&
            Cry(result).Activations[0].Status=="AwaitingObservation" && Entity(result,11).RemainingHealth==5,
            "An unknown first death was declared completed or bypassed by fizzling the second target.");
        var self=Set(Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1)),30,"DEATHRATTLE","1");
        result=Eval(self,Play(30,30));
        Check(result.Verification!=LineVerification.Verified && Cry(result).StartedActivations==1 && !Cry(result).IsComplete,
            "Summon erased the source's current unknown Deathrattle before the self-target packet killed it.");
    }
    private static void AssembledFamily()
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","assembled-scripts-255790.json")));
        foreach(var card in fixture.RootElement.GetProperty("observedMinionBaseIds").EnumerateArray())
        {
            var id=card.GetString()!; var frame=Set(Replace(Frame(),Recipe(30,id),Boar(1)),11,"DAMAGE","27");
            var result=Eval(frame,Play(30),Play(31),Attack(31,11));
            Check(result.Verification==LineVerification.Verified && result.IsLethal && result.Result.AssembledEffects is [{ExecutedCount:2}] &&
                result.Result.AssembledEffects[0].Targets.Single(target=>target.EntityId==31) is {AttackBefore:1,AttackAfter:3,MaximumHealthAfter:3} &&
                Cry(result) is {StartedActivations:2,CompletedActivations:2} && Entity(result,30).Attack==5,
                "An audited assembled base failed actual two-activation play/buff/attack: "+id+"; "+result.Verification+"; "+string.Join(',',result.UnsupportedMechanics));
        }
    }
    private static void RecipesAndOverflow()
    {
        var frame=Set(Replace(Frame(9),Recipe(30,"EDR_102"),SetEntity(Recipe(32,"EDR_102"),"CRAFTABLE_SECOND_HALF_DBID","66858"),Boar(1)),11,"DAMAGE","26");
        var result=Eval(frame,Play(30),Play(32),Play(31),Attack(31,11));
        Check(result.IsLethal && result.Result.AssembledEffects!.Select(effect=>effect.ExecutedCount).SequenceEqual(new int?[]{2,1}) &&
            result.Result.BattlecryExecutions!.Select(effect=>effect.PlannedActivations).SequenceEqual(new int?[]{2,1}) &&
            result.Result.BattlecryExecutions![1].ActionOrdinal==2 && Entity(result,31).Attack==4,
            "Same-ID recipes shared the gift or aggregate state across different source entities.");
        var overflow=Set(Replace(Frame(),Recipe(30,"EDR_102"),Boar(1)),31,"ATK",(int.MaxValue-1).ToString());
        result=Eval(overflow,Play(30));
        Check(result.Verification==LineVerification.Unsupported && result.Result.AvailableMana==5 && Entity(result,30).Zone=="HAND" && Entity(result,31).Attack==int.MaxValue-1,
            "The second activation overflow occurred after payment or a partial first buff.");
    }
    private static void PlayTriggers()
    {
        var raptor=Minion(45,"EDR_849","PLAY",2,3,0); var result=Eval(Replace(Frame(),raptor),Play(30));
        Check(result.Result.HeroPowerChanges.Count==2 && result.Result.RaptorPreviews.Single().TriggerCount is {Minimum:1,Maximum:1} &&
            Cry(result).CompletedActivations==2 && result.Result.RaptorBoundaryReached,
            "Repeated Battlecry duplicated or preceded the single native after-play trigger.");
        var quest=new EntitySnapshot(60,"TLC_EVENT_400",EntityKind.Card,Tags(("CONTROLLER","1"),("ZONE","SECRET"),("CARDTYPE","SPELL"),("SIDE_QUEST","1"),("QUEST_PROGRESS","0"),("QUEST_PROGRESS_TOTAL","3")));
        var frame=Set(Replace(Frame(),SetEntity(Recipe(30,"FIR_953"),"CARDRACE","BEAST"),Boar(1),quest),11,"DAMAGE","27");
        result=Eval(frame,Play(30),Play(31),Attack(31,11));
        Check(result.IsLethal && result.Result.QuestProgressPreviews.Count==2 && result.Result.QuestProgressPreviews[0].ResultingProgress==1 &&
            result.Result.QuestProgressPreviews[1].ResultingProgress==2,
            "Two activations counted as two card plays or restored the assembled body's native Magma trigger.");
    }
    private static void Boundaries()
    {
        foreach(var id in new[]{"FIR_921","EDR_226","CORE_UNG_912","EDR_888","EDR_489"})
        {
            var frame=Replace(Frame(),Minion(30,id,"HAND",2,3,1));
            var result=Eval(frame,Play(30)); var cry=Cry(result);
            Check(result.Verification!=LineVerification.Verified && result.AppliedActions==1 && Entity(result,30).Zone=="PLAY" &&
                cry.PlannedActivations==2 && cry.CompletedActivations<2 && !cry.IsComplete &&
                (id=="EDR_489" ? cry is {StartedActivations:2,CompletedActivations:1} : cry.StartedActivations==1),
                "A pending native effect was advertised as all repeat activations complete: "+id+"; "+result.Verification);
            var stopped=Eval(frame,Play(30),Play(31)); Check(stopped.AppliedActions==1 && Entity(stopped,31).Zone=="HAND","A later action crossed an unfinished repeat boundary.");
        }
        var direct=Replace(Frame(),Minion(30,"CORE_UNG_912","HAND",1,1,1));
        var generated=GenerationResolutionRules.Evaluate(direct,30,Cards.Value);
        Check(generated.BattlecryMultiplicity is {Activations:2} && generated.IsSingleBattlecryActivationPreview==true && generated.RequestedCards is {Minimum:1,Maximum:1},
            "Standalone generation silently called one activation the full repeated total.");
    }
    private static void ZeroEffects()
    {
        var frame=Set(Set(Replace(Frame(),Minion(30,"FIR_921","HAND",2,3,3),Boar(1)),2,"3527","1"),11,"DAMAGE","29");
        var result=Eval(frame,Play(30),Play(31),Attack(31,11));
        Check(result.IsLethal && result.Verification==LineVerification.Verified && !result.Result.DrawBoundaryReached &&
            result.Result.DrawPreviews.Count==2 && result.Result.DrawPreviews.All(draw=>draw.RequestedDraws.Maximum==0) && Cry(result).CompletedActivations==2,
            "An already false conditional draw was blocked despite both zero effects being known.");
        foreach(var flag in new[]{("BATTLECRY","0"),("SILENCED","1"),("218","0"),("188","1")})
        {
            result=Eval(Set(Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1)),30,flag.Item1,flag.Item2),Play(30));
            Check(result.Verification==LineVerification.Verified && Entity(result,11).RemainingHealth==5 && Entity(result,30).Zone=="PLAY" &&
                Cry(result) is {PlannedActivations:0,StartedActivations:0,CompletedActivations:0,RemainingActivations:0,IsComplete:true},
                "Current suppression either required a target, caused damage or gained a repeated activation.");
        }
    }
    private static void Evidence()
    {
        var frame=Replace(Frame(),Recipe(30,"EDR_102"),Boar(1)); var before=JsonSerializer.Serialize(frame);
        var result=Eval(frame,Play(30)); var proof=Cry(result);
        Check(proof.MatchId==frame.MatchId && proof.StateVersion==frame.StateVersion && proof.EventSequence==frame.EventSequence && proof.CardBuild==frame.ClientBuild &&
            proof.SourceEntityId==30 && proof.SourceCardId=="EDR_102" && proof.ActionOrdinal==1 && JsonSerializer.Serialize(frame)==before,
            "Execution evidence lost immutable original state/action/source provenance.");
        try{((IList<int>)proof.EnchantmentEntityIds).Clear();throw new Exception("Mutable enchantment proof.");}catch(NotSupportedException){}
        try{((IList<BattlecryActivationOutcome>)proof.Activations).Clear();throw new Exception("Mutable activation proof.");}catch(NotSupportedException){}
        try{((IList<int>)proof.Activations[0].AffectedEntityIds).Clear();throw new Exception("Mutable affected proof.");}catch(NotSupportedException){}
        try{((IList<BattlecryExecutionOutcome>)result.Result.BattlecryExecutions!).Clear();throw new Exception("Mutable execution ledger.");}catch(NotSupportedException){}
        var ordinary=Frame() with{Entities=Frame().Entities.Where(pair=>pair.Key is not(30 or 50)).ToFrozenDictionary()};
        ordinary=Replace(ordinary,Boar(1));
        Check(!JsonSerializer.Serialize(Eval(ordinary,Play(31)).Result).Contains("BattlecryExecutions"),"An ordinary non-Battlecry play changed legacy JSON with an invented ledger.");
        var conflict=Replace(Frame(),Minion(30,"CS2_189","HAND",1,1,1),
            SetEntity(SetEntity(Minion(31,"CS2_171","PLAY",1,1,1),"EXHAUSTED","0"),"NUM_TURNS_IN_PLAY","1"),Hero(10,1,2),Hero(11,2,1));
        conflict=Set(Set(conflict,50,"ZONE","GRAVEYARD"),50,"49","1");
        var rejected=Eval(conflict,Play(30,10),Attack(31,11));
        Check(!rejected.IsLethal && rejected.Verification!=LineVerification.Verified && rejected.AppliedActions==0 &&
            Entity(rejected,30).Zone=="HAND" && Entity(rejected,10).RemainingHealth==2 && Cry(rejected).PlannedActivations is null,
            "A contradictory active/graveyard attachment became a one-packet self-hit and a false surviving lethal chain.");
    }
    private static GameSnapshot Frame(int mana=5)=>new("repeat-runtime",12,120,5,1,1,GamePhase.Main,
        new[]{new EntitySnapshot(2,null,EntityKind.Player,Tags(("PLAYER_ID","1"),("CONTROLLER","1"),("RESOURCES",mana.ToString()),("RESOURCES_USED","0"),("HERO_ENTITY","10"),("HERO_POWER_ENTITY","12"),("MAXHANDSIZE","10"),("3527","2"))),
            new EntitySnapshot(3,null,EntityKind.Player,Tags(("PLAYER_ID","2"),("CONTROLLER","2"),("HERO_ENTITY","11"))),Hero(10,1,30),Hero(11,2,5),
            new EntitySnapshot(12,"EDR_850p",EntityKind.Card,Tags(("CONTROLLER","1"),("ZONE","PLAY"),("CARDTYPE","HERO_POWER"),("COST","2"),("EXHAUSTED","0"),("TAG_SCRIPT_DATA_NUM_1","2"),("TAG_SCRIPT_DATA_NUM_2","2"))),
            Minion(30,"EDR_852","HAND",2,3,2),Boar(5),Gift()}.ToFrozenDictionary(entity=>entity.Id),new(true,true,true,true,0,[]))
        {ClientBuild="255790"};
    private static EntitySnapshot Minion(int id,string card,string zone,int attack,int health,int cost,int owner=1)=>new(id,card,EntityKind.Card,
        Tags(("CONTROLLER",owner.ToString()),("ZONE",zone),("CARDTYPE","MINION"),("ATK",attack.ToString()),("HEALTH",health.ToString()),("DAMAGE","0"),("COST",cost.ToString()),("DORMANT","0")));
    private static EntitySnapshot Recipe(int id,string card)=>SetEntity(SetEntity(Minion(id,card,"HAND",5,4,4),"CRAFTABLE_FIRST_HALF_DBID","113822"),"CRAFTABLE_SECOND_HALF_DBID","121051");
    private static EntitySnapshot Boar(int cost)=>SetEntity(SetEntity(Minion(31,"CS2_171","HAND",1,1,cost),"CHARGE","1"),"CARDRACE","BEAST");
    private static EntitySnapshot Gift()=>new(50,"EDR_100t7e",EntityKind.Card,Tags(("CONTROLLER","1"),("ZONE","PLAY"),("CARDTYPE","ENCHANTMENT"),("ATTACHED","30")));
    private static EntitySnapshot Hero(int id,int owner,int health)=>new(id,"HERO_0"+owner,EntityKind.Card,Tags(("CONTROLLER",owner.ToString()),("ZONE","PLAY"),("CARDTYPE","HERO"),("CLASS",owner==1?"HUNTER":"MAGE"),("HEALTH","30"),("DAMAGE",(30-health).ToString()),("ATK","0"),("ARMOR","0")));
    private static FrozenDictionary<string,string> Tags(params(string Key,string Value)[] values)=>values.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value);
    private static EntitySnapshot Tag(EntitySnapshot entity,string key,string value)=>SetEntity(entity,key,value);
    private static EntitySnapshot SetEntity(EntitySnapshot entity,string key,string value)=>entity with{Tags=entity.Tags.Where(pair=>pair.Key!=key).Append(new(key,value)).ToFrozenDictionary()};
    private static GameSnapshot Replace(GameSnapshot frame,params EntitySnapshot[] entities){var values=frame.Entities.ToDictionary();foreach(var entity in entities)values[entity.Id]=entity;return frame with{Entities=values.ToFrozenDictionary()};}
    private static GameSnapshot Set(GameSnapshot frame,int id,string key,string value)=>Replace(frame,SetEntity(frame.Entities[id],key,value));
    private static PlannedAction Play(int id,int? target=null)=>new(ActionKind.PlayCard,id,target);
    private static PlannedAction Attack(int id,int target)=>new(ActionKind.Attack,id,target);
    private static LineEvaluation Eval(GameSnapshot frame,params PlannedAction[] actions)=>Rules.EvaluateLine(frame,actions,Cards.Value);
    private static SimulatedEntity Entity(LineEvaluation result,int id)=>result.Result.Entities.Single(entity=>entity.EntityId==id);
    private static BattlecryExecutionOutcome Cry(LineEvaluation result)=>result.Result.BattlecryExecutions!.First();
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        var result=new Dictionary<string,CardRecord>();
        foreach(var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"Fixtures"),"*.json"))
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path)); if(!doc.RootElement.TryGetProperty("cards",out var list))continue;
            foreach(var raw in list.EnumerateArray())
            {
                string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";
                string[] List(string key)=>raw.TryGetProperty(key,out var value)?value.EnumerateArray().Select(item=>item.GetString()!).ToArray():[];
                var id=Text("id");result[id]=new(id,raw.GetProperty("dbfId").GetInt32(),Text("name").Length==0?id:Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),
                    raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,raw.TryGetProperty("collectible",out var collect)&&collect.GetBoolean(),List("classes"),List("entourage"),raw.GetRawText());
            }
        }
        return result;
    }
    private static void Check(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
}
