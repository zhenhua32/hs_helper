using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DeathrattleRuntimeChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string,CardRecord>> Cards=new(ReadCards);
    private static readonly LineEvaluator Rules=new();
    public static IEnumerable<(string Name,Action Run)> Cases()
    {
        yield return ("repeat Deathrattle native Imbue is a necessary one-death four-action search and replay lethal",Imbue);
        yield return ("repeat Deathrattle Seismopod applies two current hand buffs with retained cost and damage before a necessary lethal",Dino);
        yield return ("repeat Deathrattle hand entry preserves current enabled and zero Deathrattle observations",HandEntry);
        yield return ("repeat Deathrattle RLK acquired draw retains real effect provenance and stops at first unknown identity",GrantedDraw);
        yield return ("repeat Deathrattle RLK native absence cannot erase an observed but unproven acquired script",UnknownAcquired);
        yield return ("repeat Deathrattle native plus acquired draw never selects a favorable script order",MixedScripts);
        yield return ("repeat Deathrattle simultaneous known native effects commute across both controllers without another death",IndependentBatch);
        yield return ("repeat Deathrattle a batch with unknown DINO targets cannot choose an arbitrary native order",UnknownBatch);
        yield return ("repeat Deathrattle first unknown hidden deck or target attributes stop before the second DINO buff",UnknownDino);
        yield return ("repeat Deathrattle malformed multiplicity and current aliases never yield a verified continuation",UnknownSources);
        yield return ("repeat Deathrattle all planned DINO arithmetic is checked before a partial buff",Overflow);
        yield return ("repeat Deathrattle unknown listener reborn and terminal hero death do not start a false second activation",StopConditions);
        yield return ("repeat Deathrattle evidence stays deeply immutable frame action batch and effect bound while ordinary nondeath JSON stays absent",Evidence);
    }
    private static void Imbue()
    {
        var frame=Frame();var before=JsonSerializer.Serialize(frame);var actions=Line(false);var result=Eval(frame,actions);var proof=Proof(result);
        Check(result.Verification==LineVerification.Verified&&result.IsLethal&&result.AppliedActions==4&&result.Result.AvailableMana==0&&
            result.Result.ImbueCountAfter==4&&result.Result.HeroPowerChanges.Count==2&&E(result,31).Attack==5&&E(result,31).Cost==1&&
            E(result,30).Zone=="GRAVEYARD"&&E(result,40).RemainingHealth==1&&proof is {PlannedActivations:2,StartedActivations:2,CompletedActivations:2,IsComplete:true}&&
            proof.Activations.Select(step=>step.ActivationOrdinal).SequenceEqual([1,2])&&JsonSerializer.Serialize(frame)==before,"Full native Imbue continuation failed: "+Describe(result));
        var search=Rules.FindLethal(frame,Cards.Value,4,4000);
        Check(search.VerifiedLethalFound&&search.Actions.Count==4&&search.Actions[0]==actions[0]&&Eval(frame,search.Actions.ToArray()).IsLethal,
            "Search bypassed the necessary death or failed to enumerate its Wolf continuation.");
    }
    private static void Dino()
    {
        foreach(var wounds in new[]{0,2})
        {
            var frame=Set(Set(Frame(true),31,"HEALTH","3"),31,"DAMAGE",wounds.ToString());var result=wounds==0?Eval(frame,Line(true)):Eval(frame,Attack(30,40));
            Check(result.Verification==LineVerification.Verified&&(wounds!=0||result.IsLethal)&&result.AppliedActions==(wounds==0?3:1)&&result.Result.AvailableMana==(wounds==0?0:1)&&
                E(result,31).Attack==7&&E(result,31).MaximumHealth==9&&E(result,31).RemainingHealth==9-wounds&&E(result,31).Cost==1&&
                E(result,30).Attack==10&&E(result,40).RemainingHealth==1&&Proof(result).CompletedActivations==2,
                "DINO repeated current stats, +2 Attack or an existing wound incorrectly: "+Describe(result));
        }
        var search=Rules.FindLethal(Frame(true),Cards.Value,3,4000);
        Check(search.VerifiedLethalFound&&search.Actions.Count==3&&search.Actions[0]==Line(true)[0],"DINO search did not require its death/buff enabler.");
    }
    private static void HandEntry()
    {
        var frame=Set(Set(Set(Frame(),30,"ZONE","HAND"),30,"COST","0"),2,"NUM_CARDS_IN_HAND","2");var actions=new[]{Play(30),Attack(30,40),new PlannedAction(ActionKind.HeroPower,12),Play(31),Attack(31,11)};
        var result=Eval(frame,actions);
        Check(result.Verification==LineVerification.Verified&&result.IsLethal&&Proof(result).ActionOrdinal==2&&Proof(result).CompletedActivations==2,
            "A real hand-to-play source lost its observed modifier or Deathrattle during Summon: "+Describe(result));
        foreach(var flag in new[]{("DEATHRATTLE","0"),("217","0"),("SILENCED","1"),("188","1"),("CANT_TRIGGER_DEATHRATTLE","1"),("1831","1")})
        {
            var silence=flag.Item1 is "SILENCED" or "188";
            var zero=flag.Item1=="217"?Remove(frame,30,"DEATHRATTLE"):silence?Frame():frame;
            result=silence?Eval(Set(zero,30,flag.Item1,flag.Item2),Attack(30,40)):Eval(Set(zero,30,flag.Item1,flag.Item2),Play(30),Attack(30,40));
            Check(result.Verification==LineVerification.Verified&&result.Result.ImbueCountAfter==2&&result.Result.HeroPowerChanges.Count==0&&
                Proof(result) is {PlannedActivations:0,StartedActivations:0,CompletedActivations:0,IsComplete:true},
                "Entry restored a currently disabled native script: "+flag+"; "+Describe(result));
            if(silence)
            {
                var entry=Eval(Set(frame,30,flag.Item1,flag.Item2),Play(30));
                Check(entry.Verification==LineVerification.Verified&&E(entry,30).Flags["SILENCED"]==1&&E(entry,30).Zone=="PLAY"&&entry.Result.HeroPowerChanges.Count==0,
                    "Entry restored a silenced native behavior or granted immediate Rush permission.");
            }
        }
        var passive=Replace(Frame(),M(30,"RLK_912",1,"HAND",1,3,1));
        passive=Set(passive with{Entities=passive.Entities.Where(pair=>pair.Key!=50).ToFrozenDictionary()},2,"NUM_CARDS_IN_HAND","2");
        var entered=Eval(passive,Play(30));
        Check(entered.Verification==LineVerification.Verified&&entered.Result.AvailableMana==2&&E(entered,30).Zone=="PLAY"&&
            entered.Result.HeroPowerChanges.Count==0&&entered.Result.DeathrattleExecutions is null,
            "An audited acquired-only passive body could not enter normally, or invented a native Deathrattle: "+Describe(entered));
        var acquired=Set(Set(Set(Set(Granted(),30,"ZONE","HAND"),30,"COST","1"),30,"CHARGE","1"),2,"NUM_CARDS_IN_HAND","2");
        acquired=Replace(acquired,M(70,"CS2_172",1,"DECK",1,1,0),M(71,"CS2_172",1,"DECK",1,1,0));
        var afterEntry=Eval(acquired,Play(30),Attack(30,40));
        Check(afterEntry.AppliedActions==2&&afterEntry.Result.DrawBoundaryReached&&afterEntry.Result.DrawPreviews is [{ResolvesEffects:true}]&&
            afterEntry.Result.DrawPreviews[0].RequestedDraws is {Minimum:2,Maximum:2}&&Proof(afterEntry) is
                {ActionOrdinal:2,EffectSourceEntityId:51,PlannedActivations:2,StartedActivations:1,CompletedActivations:0,RemainingActivations:2},
            "A valid HAND-to-PLAY acquired source was rejected as still in hand or its first draw invented completion: "+Describe(afterEntry));
    }
    private static void GrantedDraw()
    {
        var frame=Granted();var direct=DrawResolutionRules.EvaluateDeathrattle(frame,30,Cards.Value);
        Check(direct.ResolvesEffects&&direct.DeathrattleMultiplicity is {Activations:2,EffectSourceEntityId:51,IsAcquiredEffect:true}&&
            direct.IsSingleDeathrattleActivationPreview==true&&direct.RequestedDraws is {Minimum:2,Maximum:2},"Direct acquired count was treated as native or completed Draw4.");
        foreach(var flag in new[]{"AURA","TRIGGER_VISUAL"})
            Check(!DrawResolutionRules.EvaluateDeathrattle(Set(frame,30,flag,"1"),30,Cards.Value).ResolvesEffects,
                "RLK acquired-only metadata erased a current extra listener in the direct draw context: "+flag);
        var result=Eval(frame,Attack(30,40),Play(31));var proof=Proof(result);
        Check(result.Verification!=LineVerification.Verified&&result.AppliedActions==1&&result.Result.DrawBoundaryReached&&E(result,31).Zone=="HAND"&&
            result.Result.DrawPreviews.Single().RequestedDraws is {Minimum:2,Maximum:2}&&proof is {EffectKind:"ObservedEnchantmentDraw",EffectSourceEntityId:51,EffectEnchantmentEntityId:51,
                PlannedActivations:2,StartedActivations:1,CompletedActivations:0,RemainingActivations:2,IsComplete:false}&&proof.MultiplierSourceEntityIds.SequenceEqual([30]),
            "The first acquired draw completed later activations or allowed an invented followup: "+Describe(result));
    }
    private static void MixedScripts()
    {
        var frame=Replace(Frame(),Gift(51,"EDR_100t4e",30));var result=Eval(frame,Attack(30,40),Play(31));
        Check(result.Verification!=LineVerification.Verified&&result.AppliedActions==1&&result.Result.HeroPowerChanges.Count==0&&E(result,31).Zone=="HAND"&&
            result.Result.DeathrattleExecutions is {Count:2}&&result.Result.DeathrattleExecutions.All(proof=>proof.StartedActivations==0&&!proof.IsComplete&&!proof.IsBatchOrderIndependent)&&
            result.Result.DrawPreviews is [{ResolvesEffects:false}],"Mixed native and granted scripts were silently ordered to get a favorable effect first: "+Describe(result));
    }
    private static void UnknownAcquired()
    {
        var frame=Replace(Set(Set(Frame(true),31,"ATK","5"),11,"DAMAGE","25"),SetE(M(30,"RLK_912",1,"PLAY",1,1,0),"DEATHRATTLE","1"),M(40,"CS2_172",2,"PLAY",1,2,0));
        frame=frame with{Entities=frame.Entities.Where(pair=>pair.Key!=50).ToFrozenDictionary()};
        var result=Eval(frame,Attack(30,40),Play(31),Attack(31,11));
        Check(result.Verification!=LineVerification.Verified&&!result.IsLethal&&result.AppliedActions==1&&E(result,31).Zone=="HAND"&&
            result.Result.DeathrattleExecutions is [{EffectKind:"Unknown",IsMultiplicityResolved:false,CompletedActivations:0}],
            "RLK's no-native-DR count zero erased its current unresolved acquired Deathrattle and enabled a false lethal: "+Describe(result));
        var disabled=Eval(Set(frame,30,"CANT_TRIGGER_DEATHRATTLE","1"),Attack(30,40),Play(31),Attack(31,11));
        Check(disabled.Verification==LineVerification.Verified&&disabled.IsLethal&&Proof(disabled) is {PlannedActivations:0,StartedActivations:0,IsComplete:true},
            "An explicit source-wide DR suppression could not disable an otherwise unknown acquired script: "+Describe(disabled));
    }
    private static void IndependentBatch()
    {
        var frame=BothHunters();var result=Eval(frame,Attack(30,40));
        Check(result.Verification==LineVerification.Verified&&!result.IsLethal&&E(result,12).Flags["TAG_SCRIPT_DATA_NUM_1"]==4&&
            E(result,13).Flags["TAG_SCRIPT_DATA_NUM_1"]==4&&result.Result.ImbueCountAfter==4&&result.Result.HeroPowerChanges.Count==4&&
            result.Result.HeroPowerChanges.Count(change=>change.ControllerId==2)==2&&result.Result.DeathrattleExecutions is {Count:2}&&
            result.Result.DeathrattleExecutions.All(proof=>proof.IsBatchOrderIndependent&&proof.CompletedActivations==2&&proof.DeathBatchEntityIds.SequenceEqual([30,40]))&&E(result,31).Attack==1,
            "A known independent batch lost source LKI, imposed a false order or mutated the wrong controller: "+Describe(result));
    }
    private static void UnknownBatch()
    {
        var frame=Set(Replace(Frame(true),M(40,"EDR_227",2,"PLAY",1,2,0),Gift(60,"EDR_531e",40,2),Power(13,2)),2,"NUM_CARDS_IN_DECK","30");
        frame=Set(frame,11,"CLASS","3");var result=Eval(frame,Attack(30,40));
        Check(result.Verification!=LineVerification.Verified&&result.Result.DeathrattleExecutions is {Count:2}&&
            result.Result.DeathrattleExecutions.All(proof=>!proof.IsBatchOrderIndependent&&proof.StartedActivations==0)&&E(result,31).Attack==1&&E(result,13).Flags["TAG_SCRIPT_DATA_NUM_1"]==2,
            "An unknown DINO target set selected an arbitrary native order within a shared death batch: "+Describe(result));
    }
    private static void UnknownDino()
    {
        foreach(var frame in new[]{Set(Frame(true),2,"NUM_CARDS_IN_DECK","30"),Remove(Frame(true),31,"ATK"),Set(Frame(true),31,"TRIGGER_VISUAL","1")})
        {
            var result=Eval(frame,Attack(30,40),Play(31));var proof=Proof(result);
            Check(result.Verification!=LineVerification.Verified&&result.AppliedActions==1&&E(result,31).Zone=="HAND"&&proof.StartedActivations==1&&
                proof.CompletedActivations==0&&proof.RemainingActivations==2&&!proof.IsComplete,
                "Unknown hidden quantity/current stats completed a first or second DINO activation: "+Describe(result));
        }
    }
    private static void UnknownSources()
    {
        var frame=Frame();
        foreach(var bad in new[]{Set(frame,2,"882","1"),Set(frame,30,"47","8"),Set(Set(frame,50,"ZONE","GRAVEYARD"),50,"49","1"),
            Replace(frame,Gift(60,"EDR_531e",30)),Set(frame,50,"ATTACHED","31"),Set(frame,50,"UNREVIEWED_SCRIPT_FLAG","1")})
        {
            var result=Eval(bad,Line(false));Check(result.Verification!=LineVerification.Verified&&!result.IsLethal,"Unreviewed multiplier/state became exact: "+Describe(result));
        }
        var extraBattlecry=Eval(Set(Frame(true),31,"BATTLECRY","1"),Line(true));
        Check(extraBattlecry.Verification!=LineVerification.Verified&&!extraBattlecry.IsLethal,
            "A buffed simple minion's extra current Battlecry vanished during entry and created a false verified lethal: "+Describe(extraBattlecry));
    }
    private static void Overflow()
    {
        var frame=Set(Frame(true),31,"ATK",(int.MaxValue-4).ToString());var result=Eval(frame,Attack(30,40));
        Check(result.Verification==LineVerification.Unsupported&&!result.IsLethal&&E(result,31).Attack==int.MaxValue-4&&
            Proof(result) is {StartedActivations:0,CompletedActivations:0,IsComplete:false,StopReason:"EvaluationLimit"}&&Proof(result).Activations.Count==0,
            "A predictable second DINO buff overflow occurred after a partial first target mutation: "+Describe(result));
    }
    private static void StopConditions()
    {
        foreach(var bad in new[]{Set(Frame(),30,"REBORN","1"),Set(Frame(),30,"TRIGGER_VISUAL","1"),Set(Replace(Frame(),M(40,"CS2_172",2,"PLAY",2,2,0)),40,"DEATHRATTLE","1")})
        {
            var result=Eval(bad,Attack(30,40));Check(result.Verification!=LineVerification.Verified&&result.Result.DeathrattleExecutions!.All(proof=>!proof.IsComplete&&proof.CompletedActivations==0),
                "Unknown listener/reborn/death script was discarded to complete two native activations: "+Describe(result));
        }
        var terminal=BothHunters();terminal=Set(Set(terminal,10,"ATK","2"),10,"DAMAGE","29");
        var ended=Eval(terminal,Attack(10,40));
        Check(!ended.IsLethal&&E(ended,10).RemainingHealth<=0&&ended.Result.HeroPowerChanges.Count==0&&ended.Result.DeathrattleExecutions is {Count:1}&&
            ended.Result.DeathrattleExecutions!.All(proof=>proof.StartedActivations==0&&proof.StopReason=="GameEnded"),"Game end still started a queued Deathrattle.");
    }
    private static void Evidence()
    {
        var frame=Frame();var before=JsonSerializer.Serialize(frame);var result=Eval(frame,Line(false));var proof=Proof(result);
        Check(proof.MatchId==frame.MatchId&&proof.StateVersion==frame.StateVersion&&proof.EventSequence==frame.EventSequence&&proof.CardBuild==frame.ClientBuild&&
            proof.ActionOrdinal==1&&proof.DeathBatchOrdinal==1&&proof.SourceEntityId==30&&proof.SourceCardId=="EDR_227"&&proof.EffectSourceEntityId==30&&
            proof.EffectSourceCardId=="EDR_227"&&proof.EnchantmentEntityIds.SequenceEqual([50])&&JsonSerializer.Serialize(frame)==before,"Execution proof lost its source or original frame/action binding.");
        try{((IList<int>)proof.DeathBatchEntityIds).Clear();throw new Exception("Mutable batch");}catch(NotSupportedException){}
        try{((IList<int>)proof.MultiplierSourceEntityIds).Clear();throw new Exception("Mutable multiplier");}catch(NotSupportedException){}
        try{((IList<DeathrattleActivationOutcome>)proof.Activations).Clear();throw new Exception("Mutable activations");}catch(NotSupportedException){}
        try{((IList<int>)proof.Activations[0].AffectedEntityIds).Clear();throw new Exception("Mutable targets");}catch(NotSupportedException){}
        try{((IList<DeathrattleExecutionOutcome>)result.Result.DeathrattleExecutions!).Clear();throw new Exception("Mutable ledger");}catch(NotSupportedException){}
        var plain=Replace(Frame(),M(30,"CS2_172",1,"PLAY",1,3,0),M(40,"CS2_172",2,"PLAY",0,3,0));plain=plain with{Entities=plain.Entities.Where(pair=>pair.Key!=50).ToFrozenDictionary()};
        Check(!JsonSerializer.Serialize(Eval(plain,Attack(30,40)).Result).Contains("DeathrattleExecutions"),"A nondeath action invented a legacy JSON death ledger.");
    }
    private static GameSnapshot Frame(bool dino=false)=>new("death-repeat-runtime-"+dino,12,120,5,1,1,GamePhase.Main,new[]{
        new EntitySnapshot(2,null,EntityKind.Player,Tags(("PLAYER_ID","1"),("CONTROLLER","1"),("HERO_ENTITY","10"),("RESOURCES",dino?"1":"3"),("RESOURCES_USED","0"),("NUM_CARDS_IN_HAND","1"),("NUM_CARDS_IN_DECK","0"),("MAXHANDSIZE","10"),("FATIGUE","0"),("3527","2"))),
        new EntitySnapshot(3,null,EntityKind.Player,Tags(("PLAYER_ID","2"),("CONTROLLER","2"),("HERO_ENTITY","11"))),Hero(10,1,30,"3"),Hero(11,2,dino?7:5,"4"),Power(12,1),
        SetE(SetE(M(30,dino?"DINO_421":"EDR_227",1,"PLAY",dino?10:7,dino?8:2,0),"DAMAGE",dino?"7":"0"),"DEATHRATTLE","1"),
        SetE(SetE(M(31,"CS2_171",1,"HAND",1,1,dino?1:5),"CHARGE","1"),"CARDRACE","20"),M(40,"CS2_172",2,"PLAY",dino?1:2,dino?11:8,0),Gift(50,"EDR_531e",30)
    }.Select(entity=>entity.Id==30?SetE(entity,"CANT_ATTACK_HEROES","1"):entity).ToFrozenDictionary(entity=>entity.Id),new(true,true,true,true,0,[])){ClientBuild="255790"};
    private static GameSnapshot Granted()
    {
        var frame=Replace(Frame(),SetE(M(30,"RLK_912",1,"PLAY",1,1,0),"DEATHRATTLE","1"),M(40,"CS2_172",2,"PLAY",1,2,0),Gift(51,"EDR_100t4e",30));
        frame=frame with{Entities=frame.Entities.Where(pair=>pair.Key!=50).ToFrozenDictionary()};return Set(Replace(Set(frame,2,"NUM_CARDS_IN_DECK","2"),
            new EntitySnapshot(70,null,EntityKind.Card,Tags(("CONTROLLER","1"),("ZONE","DECK"),("CARDTYPE","MINION"))),
            new EntitySnapshot(71,null,EntityKind.Card,Tags(("CONTROLLER","1"),("ZONE","DECK"),("CARDTYPE","MINION")))),30,"882","1");
    }
    private static GameSnapshot BothHunters()=>Replace(Set(Frame(),11,"CLASS","3"),SetE(M(40,"EDR_227",2,"PLAY",2,2,0),"DEATHRATTLE","1"),Gift(60,"EDR_531e",40,2),Power(13,2));
    private static EntitySnapshot M(int id,string card,int owner,string zone,int attack,int health,int cost)=>new(id,card,EntityKind.Card,
        Tags(("CARDTYPE","MINION"),("CONTROLLER",owner.ToString()),("ZONE",zone),("ATK",attack.ToString()),("HEALTH",health.ToString()),("DAMAGE","0"),("COST",cost.ToString()),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1")));
    private static EntitySnapshot Gift(int id,string card,int target,int owner=1)=>new(id,card,EntityKind.Card,Tags(("CARDTYPE","ENCHANTMENT"),("CONTROLLER",owner.ToString()),("ZONE","PLAY"),("ATTACHED",target.ToString())));
    private static EntitySnapshot Power(int id,int owner)=>new(id,"EDR_850p",EntityKind.Card,Tags(("CARDTYPE","HERO_POWER"),("CONTROLLER",owner.ToString()),("ZONE","PLAY"),("COST","2"),("EXHAUSTED","0"),("TAG_SCRIPT_DATA_NUM_1","2"),("TAG_SCRIPT_DATA_NUM_2","2")));
    private static EntitySnapshot Hero(int id,int owner,int health,string heroClass)=>new(id,owner==1?"HERO_05":"HERO_08",EntityKind.Card,Tags(("CARDTYPE","HERO"),("CLASS",heroClass),("CONTROLLER",owner.ToString()),("ZONE","PLAY"),("HEALTH","30"),("DAMAGE",(30-health).ToString()),("ARMOR","0"),("ATK","0")));
    private static FrozenDictionary<string,string> Tags(params(string Key,string Value)[] tags)=>tags.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value);
    private static EntitySnapshot SetE(EntitySnapshot entity,string key,string value)=>entity with{Tags=entity.Tags.Where(pair=>pair.Key!=key).Append(new(key,value)).ToFrozenDictionary()};
    private static GameSnapshot Replace(GameSnapshot frame,params EntitySnapshot[] entities){var map=frame.Entities.ToDictionary();foreach(var entity in entities)map[entity.Id]=entity;return frame with{Entities=map.ToFrozenDictionary()};}
    private static GameSnapshot Set(GameSnapshot frame,int id,string key,string value)=>Replace(frame,SetE(frame.Entities[id],key,value));
    private static GameSnapshot Remove(GameSnapshot frame,int id,string key)=>Replace(frame,frame.Entities[id] with{Tags=frame.Entities[id].Tags.Where(pair=>pair.Key!=key).ToFrozenDictionary()});
    private static PlannedAction Play(int id)=>new(ActionKind.PlayCard,id);
    private static PlannedAction Attack(int id,int target)=>new(ActionKind.Attack,id,target);
    private static PlannedAction[] Line(bool dino)=>dino?[Attack(30,40),Play(31),Attack(31,11)]:[Attack(30,40),new(ActionKind.HeroPower,12),Play(31),Attack(31,11)];
    private static LineEvaluation Eval(GameSnapshot frame,params PlannedAction[] actions)=>Rules.EvaluateLine(frame,actions,Cards.Value);
    private static SimulatedEntity E(LineEvaluation result,int id)=>result.Result.Entities.Single(entity=>entity.EntityId==id);
    private static DeathrattleExecutionOutcome Proof(LineEvaluation result)=>result.Result.DeathrattleExecutions!.First();
    private static string Describe(LineEvaluation result)=>result.Verification+"; "+string.Join(',',result.UnsupportedMechanics)+"; applied="+result.AppliedActions;
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        var map=new Dictionary<string,CardRecord>();foreach(var file in new[]{"native-definitions-255790.json","deathrattle-repeat-255790.json"})
        {using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",file)));foreach(var raw in doc.RootElement.GetProperty("cards").EnumerateArray())
        {string T(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";string[] L(string key)=>raw.TryGetProperty(key,out var value)?value.EnumerateArray().Select(v=>v.GetString()!).ToArray():[];
            var card=new CardRecord(T("id"),raw.GetProperty("dbfId").GetInt32(),T("name"),T("text"),T("cardClass"),T("set"),T("type"),raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,
                raw.TryGetProperty("collectible",out var c)&&c.GetBoolean(),L("classes"),L("entourage"),raw.GetRawText());map[card.Id]=card;}}return map;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
