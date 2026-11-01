using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class DeathrattleRepeatCoachChecks
{
    public static IEnumerable<(string Name,Func<Task> Run)> Cases()
    {
        yield return ("repeat Deathrattle lazy tools search and final host require one death before the full native Imbue and Seismopod lethal continuations", CompleteLines);
        yield return ("repeat Deathrattle policy keeps script multiplier and death provenance distinct while a real acquired draw stops after one activation", Boundaries);
    }
    private static async Task CompleteLines()
    {
        var catalog=await Catalog();
        foreach(var dino in new[]{false,true})
        {
            var frame=Frame(dino);var before=JsonSerializer.Serialize(frame);var observations=new List<CoachToolObservation>();
            var coach=new SnapshotCoach(frame,new(),catalog,"255790",toolCompleted:observations.Add);
            var line=await coach.ExecuteAsync("evaluate_line",Line(dino),default);var proof=line["Result"]!["DeathrattleExecutions"]!.AsArray().Single()!;
            Check(line["Verification"]!.ToString()=="Verified"&&line["IsLethal"]!.GetValue<bool>()&&line["AppliedActions"]!.GetValue<int>()==(dino?3:4)&&
                line["Result"]!["AvailableMana"]!.GetValue<int>()==0&&Entity(line,31)["Attack"]!.GetValue<int>()==(dino?7:5)&&Entity(line,40)["RemainingHealth"]!.GetValue<int>()==1&&
                proof["SourceEntityId"]!.GetValue<int>()==30&&proof["EffectSourceEntityId"]!.GetValue<int>()==30&&proof["EffectKind"]!.ToString()==(dino?"NativeHandDeckBuff":"NativeImbue")&&
                proof["ActionOrdinal"]!.GetValue<int>()==1&&proof["DeathBatchOrdinal"]!.GetValue<int>()==1&&proof["DeathBatchEntityIds"]!.AsArray().Select(v=>v!.GetValue<int>()).SequenceEqual([30])&&
                proof["PlannedActivations"]!.GetValue<int>()==2&&proof["StartedActivations"]!.GetValue<int>()==2&&proof["CompletedActivations"]!.GetValue<int>()==2&&
                proof["RemainingActivations"]!.GetValue<int>()==0&&proof["IsComplete"]!.GetValue<bool>()&&proof["MultiplierSourceEntityIds"]!.AsArray().Single()!.GetValue<int>()==50&&
                JsonSerializer.Serialize(frame)==before,"A native two-activation continuation lost LKI, repeated death, regranted Attack or skipped its necessary enabler.");
            var search=await coach.ExecuteAsync("find_lethal",new(){["maximum_depth"]=dino?3:4,["maximum_nodes"]=1000},default);
            Check(search["VerifiedLethalFound"]!.GetValue<bool>()&&search["Actions"]!.AsArray().Count==(dino?3:4)&&
                search["Actions"]![0]!["SourceEntityId"]!.GetValue<int>()==30&&search["Actions"]![0]!["TargetEntityId"]!.GetValue<int>()==40&&
                search["Evaluation"]!["Result"]!["DeathrattleExecutions"]!.AsArray().Single()!["CompletedActivations"]!.GetValue<int>()==2,
                "Search either bypassed the required Deathrattle via a source face attack or failed the complete continuation.");
            AdviceAction[] actions=dino?[new("attack",30,40),new("play",31),new("attack",31,11)]:
                [new("attack",30,40),new("hero_power",12),new("play",31),new("attack",31,11)];
            var host=await coach.Capabilities().ValidateActions!(actions,default);
            Check(host.Status==AdviceValidationStatus.Verified&&host.IsLegal&&host.VerifiedLethal&&observations[0].Success,
                "Final host did not independently replay the same one-death two-script-activation evidence.");
        }
    }
    private static async Task Boundaries()
    {
        foreach(var task in new[]{AdviceTaskKind.Turn,AdviceTaskKind.Choice})
        {
            var policy=AdviceTaskTemplates.Get(task);
            Check(policy.Version=="hunter-v25/"+task.ToString().ToLowerInvariant()&&
                new[]{"DeathrattleExecutions","DeathBatchEntityIds","IsBatchOrderIndependent","EffectSourceEntityId","MultiplierSourceEntityIds","StartedActivations","CompletedActivations","IsSingleDeathrattleActivationPreview","不证明死亡已经发生"}
                .All(text=>policy.Instructions.Contains(text,StringComparison.Ordinal)),"Deathrattle policy blurred candidate death, script, multiplicity or pending draws.");
        }
        var definition=SnapshotCoach.Definitions.Single(tool=>tool.Name=="evaluate_line");
        foreach(var key in new[]{"repeat_count","death_event_sequence","death_batch_entity_ids","effect_enchantment_entity_id","completed_activations"})
        {
            var args=Line(false);args[key]=2;Check(!definition.ValidateArguments(args),"Caller supplied death or count evidence: "+key);
            args=Line(false);args["actions"]![0]![key]=2;Check(!definition.ValidateArguments(args),"A normal action accepted forged trigger fields: "+key);
        }
        foreach(var kind in new[]{"death","deathrattle"})
            Check(!definition.ValidateArguments(new(){["actions"]=new JsonArray(new JsonObject{["kind"]=kind,["source_entity_id"]=30})}),"Model manufactured another death action.");
        var frame=Frame(false);var map=frame.Entities.ToDictionary();
        map[30]=E(30,"RLK_912",("CARDTYPE","MINION"),("ZONE","PLAY"),("CONTROLLER","1"),("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("DEATHRATTLE","1"),("882","1"),("NUM_TURNS_IN_PLAY","1"),("EXHAUSTED","0"));
        map.Remove(50);map[51]=E(51,"EDR_100t4e",("CARDTYPE","ENCHANTMENT"),("ZONE","PLAY"),("CONTROLLER","1"),("ATTACHED","30"));
        map[40]=E(40,"CS2_172",("CARDTYPE","MINION"),("ZONE","PLAY"),("CONTROLLER","2"),("ATK","1"),("HEALTH","2"),("DAMAGE","0"));
        map[2]=map[2] with{Tags=map[2].Tags.Where(pair=>pair.Key!="NUM_CARDS_IN_DECK").Append(new("NUM_CARDS_IN_DECK","2")).ToFrozenDictionary()};
        map[70]=E(70,null,("CARDTYPE","MINION"),("ZONE","DECK"),("CONTROLLER","1"));map[71]=E(71,null,("CARDTYPE","MINION"),("ZONE","DECK"),("CONTROLLER","1"));
        frame=frame with{Entities=map.ToFrozenDictionary()};var before=JsonSerializer.Serialize(frame);
        var coach=new SnapshotCoach(frame,new(),await Catalog(),"255790");
        var preview=await coach.ExecuteAsync("evaluate_deathrattle_draw",new(){["source_entity_id"]=30},default);
        Check(preview["DeathrattleMultiplicity"]?["Activations"]?.GetValue<int>()==2&&preview["IsSingleDeathrattleActivationPreview"]?.GetValue<bool>()==true&&
            preview["RequestedDraws"]!["Minimum"]!.GetValue<int>()==2&&preview["RequestedDraws"]!["Maximum"]!.GetValue<int>()==2,
            "An acquired-only multiplier became a native Deathrattle or a completed total Draw4 preview.");
        var result=await coach.ExecuteAsync("evaluate_line",new(){["actions"]=new JsonArray(new JsonObject{["kind"]="attack",["source_entity_id"]=30,["target_entity_id"]=40},new JsonObject{["kind"]="play",["source_entity_id"]=31})},default);
        var proof=result["Result"]!["DeathrattleExecutions"]!.AsArray().Single()!;
        Check(result["Verification"]!.ToString()!="Verified"&&result["AppliedActions"]!.GetValue<int>()==1&&result["Result"]!["DrawBoundaryReached"]!.GetValue<bool>()&&
            proof["EffectEnchantmentEntityId"]!.GetValue<int>()==51&&proof["EffectSourceEntityId"]!.GetValue<int>()==51&&proof["PlannedActivations"]!.GetValue<int>()==2&&
            proof["StartedActivations"]!.GetValue<int>()==1&&proof["CompletedActivations"]!.GetValue<int>()==0&&proof["RemainingActivations"]!.GetValue<int>()==2&&
            JsonSerializer.Serialize(frame)==before,"A first unknown granted draw completed both activations or enabled an invented followup.");
    }
    private static JsonObject Line(bool dino)
    {
        var actions=new JsonArray(new JsonObject{["kind"]="attack",["source_entity_id"]=30,["target_entity_id"]=40});
        if(!dino)actions.Add(new JsonObject{["kind"]="hero_power",["source_entity_id"]=12});
        actions.Add(new JsonObject{["kind"]="play",["source_entity_id"]=31});actions.Add(new JsonObject{["kind"]="attack",["source_entity_id"]=31,["target_entity_id"]=11});
        return new(){["actions"]=actions};
    }
    private static GameSnapshot Frame(bool dino)=>new("repeat-dr-coach-"+dino,10,100,5,1,1,GamePhase.Main,new[]{
        new EntitySnapshot(2,null,EntityKind.Player,Tags(("PLAYER_ID","1"),("CONTROLLER","1"),("HERO_ENTITY","10"),("RESOURCES",dino?"1":"3"),("RESOURCES_USED","0"),("NUM_CARDS_IN_HAND","1"),("NUM_CARDS_IN_DECK","0"),("MAXHANDSIZE","10"),("FATIGUE","0"),("3527","2"))),
        new EntitySnapshot(3,null,EntityKind.Player,Tags(("PLAYER_ID","2"),("CONTROLLER","2"),("HERO_ENTITY","11"))),
        E(10,"HERO_05",("CARDTYPE","HERO"),("CLASS","3"),("CONTROLLER","1"),("ZONE","PLAY"),("HEALTH","30"),("DAMAGE","0"),("ARMOR","0"),("ATK","0")),
        E(11,"HERO_08",("CARDTYPE","HERO"),("CONTROLLER","2"),("ZONE","PLAY"),("HEALTH","30"),("DAMAGE",dino?"23":"25"),("ARMOR","0"),("ATK","0")),
        E(12,"EDR_850p",("CARDTYPE","HERO_POWER"),("CONTROLLER","1"),("ZONE","PLAY"),("COST","2"),("EXHAUSTED","0"),("TAG_SCRIPT_DATA_NUM_1","2"),("TAG_SCRIPT_DATA_NUM_2","2")),
        E(30,dino?"DINO_421":"EDR_227",("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","PLAY"),("ATK",dino?"10":"7"),("HEALTH",dino?"8":"2"),("DAMAGE",dino?"7":"0"),("DEATHRATTLE","1"),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1"),("CANT_ATTACK_HEROES","1")),
        E(31,"CS2_171",("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","HAND"),("COST",dino?"1":"5"),("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("CHARGE","1"),("CARDRACE","20")),
        E(40,"CS2_172",("CARDTYPE","MINION"),("CONTROLLER","2"),("ZONE","PLAY"),("ATK",dino?"1":"2"),("HEALTH",dino?"11":"8"),("DAMAGE","0")),
        E(50,"EDR_531e",("CARDTYPE","ENCHANTMENT"),("CONTROLLER","1"),("ZONE","PLAY"),("ATTACHED","30"))
    }.ToFrozenDictionary(entity=>entity.Id),new(true,true,true,true,0,[])){ClientBuild="255790"};
    private static EntitySnapshot E(int id,string? card,params(string Key,string Value)[] tags)=>new(id,card,EntityKind.Card,Tags(tags));
    private static FrozenDictionary<string,string> Tags(params(string Key,string Value)[] tags)=>tags.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value);
    private static JsonNode Entity(JsonNode result,int id)=>result["Result"]!["Entities"]!.AsArray().Single(entity=>entity!["EntityId"]!.GetValue<int>()==id)!;
    private static async Task<CardCatalog> Catalog()
    {
        var workspace=new DirectoryInfo(AppContext.BaseDirectory);while(workspace is not null&&!File.Exists(Path.Combine(workspace.FullName,"AGENTS.md")))workspace=workspace.Parent;
        var rows=new Dictionary<string,JsonNode>();foreach(var name in new[]{"deathrattle-repeat-255790.json","native-definitions-255790.json"})
        {var doc=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,"tests","HsHelper.Rules.Tests","Fixtures",name)))!;foreach(var row in doc["cards"]!.AsArray())rows[row!["id"]!.ToString()]=row.DeepClone();}
        var catalog=new CardCatalog(Path.Combine(workspace!.FullName,".local","deathrattle-repeat-coach",Guid.NewGuid().ToString("N"),"cards.db"));await catalog.InitializeAsync();
        using var input=new MemoryStream(Encoding.UTF8.GetBytes(new JsonArray(rows.Values.Select(row=>row.DeepClone()).ToArray()).ToJsonString()));await catalog.ImportAsync(input,"255790",source:"fixture://deathrattle-repeat-coach");return catalog;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
