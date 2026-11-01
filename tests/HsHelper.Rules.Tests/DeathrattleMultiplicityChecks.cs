using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DeathrattleMultiplicityChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string,CardRecord>> Cards = new(ReadCards);
    public static IEnumerable<(string Name,Action Run)> Cases()
    {
        yield return ("Deathrattle multiplicity ordinary native scripts need no count marker and one actual siphon attachment gives two without regranting Attack", Ordinary);
        yield return ("Deathrattle multiplicity pins whole carrier and acquired-only body definitions instead of names or count tags", Definitions);
        yield return ("Deathrattle multiplicity requires current attachment topology stats aliases and consistent expiry", Attachment);
        yield return ("Deathrattle multiplicity distinguishes a real acquired script from native absence and refuses unknown stacking or global scope", AcquiredAndGlobals);
        yield return ("Deathrattle multiplicity is immutable frame effect and controller scoped and does not grant another death or Battlecry", Scope);
    }
    private static void Ordinary()
    {
        Check(Eval(Frame(false)) is {IsResolved:true,IsEnabled:true,Activations:1,MultiplierSourceEntityIds.Count:0},"Ordinary native DR needed a fabricated once counter.");
        var frame=Frame(); var before=JsonSerializer.Serialize(frame); var result=Eval(frame);
        Check(result is {IsResolved:true,IsEnabled:true,Activations:2,IsAcquiredEffect:false,EffectSourceEntityId:30} &&
            result.EnchantmentEntityIds.SequenceEqual([50]) && result.MultiplierSourceEntityIds.SequenceEqual([50]) &&
            JsonSerializer.Serialize(frame)==before && frame.Entities[30].GetIntTag("ATK")==7,"Actual single self attachment failed or reapplied +2 Attack.");
        foreach(var pair in new[]{("DEATHRATTLE","0"),("217","0"),("SILENCED","1"),("188","1"),("CANT_TRIGGER_DEATHRATTLE","1"),("1831","1")})
            Check(Eval(Set(pair.Item1=="217"?Remove(frame,30,"DEATHRATTLE"):frame,30,pair.Item1,pair.Item2)) is {IsResolved:true,IsEnabled:false,Activations:0},"An explicit current source gate was ignored: "+pair);
        foreach(var pair in new[]{("DEATHRATTLE","2"),("217","-1"),("SILENCED","invalid"),("1831","1.0")})
            Check(!Eval(Set(frame,30,pair.Item1,pair.Item2)).IsResolved,"Malformed raw source boolean borrowed a favorable fallback: "+pair);
        Check(!Eval(Set(Set(frame,30,"DEATHRATTLE","0"),30,"217","1")).IsResolved,"Conflicting current DR aliases were resolved favorably.");
        Check(Eval(Set(Frame(false),30,"CARDTYPE","SPELL")).IsResolved==false,"Contradictory native type was repaired from printed metadata.");
    }
    private static void Definitions()
    {
        Check(DeathrattleMultiplicityRules.IsAuditedEnchantment("255790",Cards.Value["EDR_531e"]) &&
            DeathrattleMultiplicityRules.IsAuditedSelfDoubler("255790",Cards.Value["RLK_912"]),"Complete exact definitions failed fixed fingerprints.");
        foreach(var id in new[]{"EDR_531e","RLK_912"})
        {
            var original=Cards.Value[id];
            foreach(var bad in new[]{original with {DbfId=1},original with {Name="same looking"},original with {Text="twice"},
                original with {Type="SPELL"},original with {CardClass="HUNTER"},original with {RawJson="{}"},original with {RawJson="broken"},original with {RawJson=new string(' ',65537)}})
            {
                var cards=Cards.Value.ToDictionary();cards[id]=bad;
                var frame=id=="RLK_912"?Granted():Frame();
                Check(!Eval(frame,cards,id=="RLK_912"?51:null).IsResolved,"A partial carrier/body record inherited repeat semantics: "+id);
            }
            var raw=JsonNode.Parse(original.RawJson)!.AsObject();raw["unreviewedScript"]=1;
            var altered=Cards.Value.ToDictionary();altered[id]=Read(JsonSerializer.SerializeToElement(raw));
            Check(!Eval(id=="RLK_912"?Granted():Frame(),altered,id=="RLK_912"?51:null).IsResolved,"A coherent full-source mutation passed by familiar ID.");
        }
        Check(!DeathrattleMultiplicityRules.IsAuditedEnchantment("255791",Cards.Value["EDR_531e"]) &&
            !DeathrattleMultiplicityRules.IsAuditedEnchantment("255790",Cards.Value["EDR_531"]),"Mother spell or another build became the actual attachment.");
        var wrongKey=JsonNode.Parse(Cards.Value["CS2_172"].RawJson)!.AsObject();wrongKey["id"]="DIFFERENT_BODY";
        var mismatched=Cards.Value.ToDictionary();mismatched["CS2_172"]=Read(JsonSerializer.SerializeToElement(wrongKey));
        Check(!DeathrattleMultiplicityRules.TryAuditAttachment(Replace(Frame(),Body(30,"CS2_172")),50,mismatched,out _),
            "A coherent unregistered definition under a different dictionary key supplied the observed body's identity.");
    }
    private static void Attachment()
    {
        foreach(var item in new[]{(50,"40","31"),(50,"50","2"),(50,"49","4"),(50,"202","4"),(50,"53","999"),
            (50,"DEATHRATTLE","1"),(50,"IS_NIGHTMARE_BONUS","1"),(50,"TAG_SCRIPT_DATA_NUM_1","1"),(50,"2921","1"),
            (50,"EXTRA_DEATHRATTLES_BASE","1"),(50,"2","1"),(50,"1179","1"),(50,"UNREVIEWED_SCRIPT_FLAG","1"),
            (30,"47","8"),(30,"45","3"),(30,"44","1"),(30,"DORMANT","1"),(30,"202","SPELL")})
        {
            var frame=Set(Frame(),item.Item1,item.Item2,item.Item3);
            Check(!DeathrattleMultiplicityRules.TryAuditAttachment(frame,50,Cards.Value,out _) && !Eval(frame).IsResolved,
                "Bad current attachment/subject evidence became two DRs: "+item);
        }
        foreach(var key in new[]{"ATTACHED","CARDTYPE","CONTROLLER","ZONE"})
            Check(!DeathrattleMultiplicityRules.TryAuditAttachment(Remove(Frame(),50,key),50,Cards.Value,out _),"Missing relation supplied from the ID: "+key);
        var numeric=Replace(Frame(),Gift(50,"EDR_531e",30) with {Tags=Tags(("202","6"),("ZONE","PLAY"),("CONTROLLER","1"),("40","30"))});
        Check(Eval(numeric) is {IsResolved:true,Activations:2},"Valid numeric type and attachment observations were rejected.");
        var creator=Set(Frame(),50,"CREATOR","31");
        Check(Eval(creator) is {IsResolved:true,Activations:2}&&Eval(Set(creator,50,"313","31")) is {IsResolved:true,Activations:2},
            "Known CREATOR/313 provenance was treated as an extra script or used instead of actual ATTACHED.");
        Check(!Eval(Set(creator,50,"313","32")).IsResolved,"Conflicting provenance aliases were silently repaired.");
        Check(Eval(Set(Set(Frame(),50,"ZONE","GRAVEYARD"),50,"49","4")) is {IsResolved:true,Activations:1},"Consistent expired attachment still repeats.");
        Check(!Eval(Set(Set(Frame(),50,"ZONE","GRAVEYARD"),50,"49","1")).IsResolved,"Expiry name hid a contradictory active raw zone.");
        Check(!Eval(Set(Frame(false),30,"DAMAGE","2")).IsResolved,"An already dead observed body got a new schedule.");
        var invalidId=Replace(Frame(false),Gift(-50,"EDR_531e",30));
        Check(!DeathrattleMultiplicityRules.TryAuditAttachment(invalidId,-50,Cards.Value,out _)&&!Eval(invalidId).IsResolved,
            "A nonpositive attachment identity authorized a reliable multiplier source.");
    }
    private static void AcquiredAndGlobals()
    {
        var granted=Granted(); var result=Eval(granted,effect:51);
        Check(result is {IsResolved:true,Activations:2,IsAcquiredEffect:true,EffectSourceEntityId:51,EffectEnchantmentEntityId:51} &&
            result.MultiplierSourceEntityIds.SequenceEqual([30]) && result.EnchantmentEntityIds.Count==0,"RLK acquired-only trait lost its real granted provenance.");
        Check(Eval(granted) is {IsResolved:true,IsEnabled:false,Activations:0},"RLK gained-only text became an invented native Deathrattle.");
        Check(!Eval(granted,effect:999).IsResolved && !Eval(Replace(granted,Gift(51,"EDR_100t7e",30)),effect:51).IsResolved,
            "Caller supplied effect ID or Battlecry gift was accepted as an acquired draw script.");
        Check(!Eval(Set(granted,30,"882","0"),effect:51).IsResolved && Eval(Set(granted,30,"882","1"),effect:51).Activations==2,
            "RLK source882 corroboration became a general total formula or overwrote an explicit zero.");
        Check(!Eval(Replace(granted,Gift(50,"EDR_531e",30)),effect:51).IsResolved,"Two different multiplier sources silently stacked.");
        Check(!Eval(Replace(Frame(),Gift(51,"EDR_531e",30))).IsResolved,"Two actual self attachments became two or four by guess.");
        foreach(var key in new[]{"371","882","1131","EXTRA_MINION_DEATHRATTLES_BASE","EXTRA_DEATHRATTLES_BASE","EXTRA_DEATHRATTLES_ADDITIONAL","1831","CANT_TRIGGER_DEATHRATTLE","UNKNOWN_DEATHRATTLE_MODIFIER"})
        {
            Check(!Eval(Set(Frame(),2,key,"1")).IsResolved,"Unreviewed player/global field became an exact schedule: "+key);
            Check(Eval(Set(Frame(),2,key,"0")) is {IsResolved:true,Activations:2},"Expired zero field blocked an otherwise exact attachment: "+key);
        }
        Check(!Eval(Set(Set(Frame(),2,"EXTRA_DEATHRATTLES_BASE","0"),2,"882","1")).IsResolved,"A named/numeric global conflict was erased.");
    }
    private static void Scope()
    {
        var frame=Replace(Frame(),Body(31,"EDR_227")); var before=JsonSerializer.Serialize(frame); var result=Eval(frame);
        Check(DeathrattleMultiplicityRules.Evaluate(frame,31,Cards.Value) is {IsResolved:true,Activations:1} &&
            result.MatchId==frame.MatchId && result.StateVersion==frame.StateVersion && result.EventSequence==frame.EventSequence && result.CardBuild==frame.ClientBuild &&
            JsonSerializer.Serialize(frame)==before,"Frame/source binding or another minion's trait leaked.");
        try{((IList<int>)result.MultiplierSourceEntityIds).Clear();throw new Exception("Mutable repeat sources");}catch(NotSupportedException){}
        try{((IList<string>)result.Unknowns).Clear();throw new Exception("Mutable unknowns");}catch(NotSupportedException){}
        foreach(var bad in new[]{frame with{ClientBuild=null},frame with{ClientBuild="255791"},frame with{LocalPlayerId=null},frame with{IsSpectating=true},
            frame with{Quality=frame.Quality with{IsComplete=false}},frame with{Quality=frame.Quality with{OpenBlockCount=1}}})
            Check(!Eval(bad).IsResolved,"Unbound quality/build/perspective authorized repetition.");
        var opposing=Set(Set(frame,30,"CONTROLLER","2"),50,"CONTROLLER","2");
        Check(!Eval(opposing).IsResolved && Eval(Set(opposing,30,"ZONE","PLAY")) is {IsResolved:true,Activations:2},"Private hand or public opposing source visibility was inverted.");
        var noDr=Replace(frame,Body(30,"EDR_852") with{Tags=Body(30,"EDR_852").Tags.Where(pair=>pair.Key!="DEATHRATTLE").ToFrozenDictionary()});
        Check(Eval(noDr) is {IsResolved:true,IsEnabled:false,Activations:0},"DoubleDR attachment granted a Deathrattle to a Battlecry body.");
    }
    private static DeathrattleMultiplicityOutcome Eval(GameSnapshot frame,IReadOnlyDictionary<string,CardRecord>? cards=null,int? effect=null)=>
        DeathrattleMultiplicityRules.Evaluate(frame,30,cards??Cards.Value,effect);
    private static GameSnapshot Granted()=>Replace(Frame(false),Body(30,"RLK_912"),Gift(51,"EDR_100t4e",30));
    private static GameSnapshot Frame(bool gift=true)=>new("death-repeat-count",10,100,5,1,1,GamePhase.Main,
        new[]{new EntitySnapshot(2,null,EntityKind.Player,Tags(("PLAYER_ID","1"))),new EntitySnapshot(3,null,EntityKind.Player,Tags(("PLAYER_ID","2"))),Body(30,"EDR_227")}
            .Concat(gift?new[]{Gift(50,"EDR_531e",30)}:[]).ToFrozenDictionary(entity=>entity.Id),new(true,true,true,true,0,[])){ClientBuild="255790"};
    private static EntitySnapshot Body(int id,string card)=>new(id,card,EntityKind.Card,Tags(("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","HAND"),("ATK","7"),("HEALTH","2"),("DAMAGE","0"),("COST","3"),("DEATHRATTLE","1")));
    private static EntitySnapshot Gift(int id,string card,int target)=>new(id,card,EntityKind.Card,Tags(("CARDTYPE","ENCHANTMENT"),("CONTROLLER","1"),("ZONE","PLAY"),("ATTACHED",target.ToString())));
    private static FrozenDictionary<string,string> Tags(params(string Key,string Value)[] values)=>values.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value);
    private static GameSnapshot Replace(GameSnapshot frame,params EntitySnapshot[] entities){var map=frame.Entities.ToDictionary();foreach(var entity in entities)map[entity.Id]=entity;return frame with{Entities=map.ToFrozenDictionary()};}
    private static GameSnapshot Set(GameSnapshot frame,int id,string key,string value)=>Replace(frame,frame.Entities[id] with{Tags=frame.Entities[id].Tags.Where(pair=>pair.Key!=key).Append(new(key,value)).ToFrozenDictionary()});
    private static GameSnapshot Remove(GameSnapshot frame,int id,string key)=>Replace(frame,frame.Entities[id] with{Tags=frame.Entities[id].Tags.Where(pair=>pair.Key!=key).ToFrozenDictionary()});
    private static IReadOnlyDictionary<string,CardRecord> ReadCards(){using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","deathrattle-repeat-255790.json")));return doc.RootElement.GetProperty("cards").EnumerateArray().Select(Read).ToDictionary(c=>c.Id);}
    private static CardRecord Read(JsonElement raw)
    {
        string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";
        string[] List(string key)=>raw.TryGetProperty(key,out var value)?value.EnumerateArray().Select(v=>v.GetString()!).ToArray():[];
        return new(Text("id"),raw.GetProperty("dbfId").GetInt32(),Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,
            raw.TryGetProperty("collectible",out var collectible)&&collectible.GetBoolean(),List("classes"),List("entourage"),raw.GetRawText());
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
