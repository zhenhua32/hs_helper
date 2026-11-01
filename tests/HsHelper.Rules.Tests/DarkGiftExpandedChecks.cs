using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DarkGiftExpandedChecks
{
    private sealed record Spec(string Id,int Dbf,string Suffix,int Attack,int Health,int Cost,params (string Name,string Alias)[] Keywords)
    { public string Coverage=>"dark-gift-observed-"+Suffix+":"+Id; }
    private static readonly Spec[] Specs =
    [
        new("EDR_100t1e",113804,"elusive",7,4,4,("ELUSIVE","1211")),
        new("EDR_100t2e",113805,"short-claws",3,2,0),
        new("EDR_100t3e",113806,"taunt",5,6,4,("TAUNT","190")),
        new("EDR_100t8e1",115355,"sweet-dreams",9,7,4),
        new("EDR_100t13e",120226,"shield-windfury",5,2,4,("DIVINE_SHIELD","194"),("WINDFURY","189")),
        new("EDR_100t8e",113885,"shuffled-stats",4,8,3),
        new("EDR_100t5e2",117173,"copy-stats",1,1,4),
        new("EDR_100t5e5",120020,"copy-stats",2,2,4)
    ];
    private static readonly Lazy<IReadOnlyDictionary<string,CardRecord>> FixedCards=new(ReadCards);
    public static IEnumerable<(string Name,Action Run)> Cases()
    {
        yield return ("all observed static gift fixtures retain exact full primary metadata and CardDefs tags", Provenance);
        yield return ("all eight additional static gifts use current hand values without applying bonuses twice", EveryHandGift);
        yield return ("shield Windfury gift permits a second attack after the first shield is consumed", ShieldConsumption);
        yield return ("a previously consumed shield is not granted again by its persistent gift annotation", AlreadySpentShield);
        yield return ("multiple audited static gifts share observed aggregate stats keywords and cost", MultipleStaticSources);
        yield return ("settled shuffle and copy annotations do not rerandomize stats or create entities", SettledRecords);
        yield return ("numeric Elusive prevents spells but permits covered battlecries and ordinary attacks", ElusiveTargets);
        yield return ("spell and hero-power targeting restrictions do not impersonate the Elusive keyword", DistinctTargetingFlags);
        yield return ("observed Taunt gift enforces target selection on an enemy public minion", TauntTargets);
        yield return ("static gift source gameplay flags reject malformed values rather than defaulting to zero", SourceGameplayFlags);
        yield return ("expanded static gifts reject missing contradictory identities stats keywords and metadata", BrokenEvidence);
        yield return ("fixed visual and button tags are explained only by their exact source definitions", MarkerAliases);
        yield return ("audited static attachments expire locally after a covered target death without hiding unknown effects", CoveredTargetDeath);
        yield return ("ongoing gifts and Raptor boundaries cannot borrow multi-static coverage", OngoingAndRaptor);
    }
    private static void Provenance()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","dark-gift-static-255790.json")));
        var root=doc.RootElement;
        Check(root.GetProperty("build").GetString()=="255790" && root.GetProperty("sourceSha256").GetString()=="7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            root.GetProperty("cardDefsSha256").GetString()=="EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8","Fixed primary provenance changed.");
        foreach(var spec in Specs)
        {
            var card=FixedCards.Value[spec.Id]; var evidence=root.GetProperty("cardDefsEvidence").EnumerateArray().Single(row=>row.GetProperty("CardId").GetString()==spec.Id);
            Check(card.DbfId==spec.Dbf && card.Type=="ENCHANTMENT" && evidence.GetProperty("Dbf").GetString()==spec.Dbf.ToString(),"Gift record and client entity identity differ.");
            var tags=evidence.GetProperty("Tags").EnumerateArray().ToArray();
            Check(tags.Any(tag=>tag.GetProperty("EnumId").GetString()=="202" && tag.GetProperty("Value").GetString()=="6") &&
                tags.Single(tag=>tag.GetProperty("EnumId").GetString()=="184").GetProperty("Chinese").GetString()==card.Text,"Complete client text/type was not retained.");
            if(spec.Id is "EDR_100t8e" or "EDR_100t5e2" or "EDR_100t5e5")
                Check(!tags.Any(tag=>tag.GetProperty("EnumId").GetString() is "2921" or "3567"),"A copy/shuffle annotation acquired an invented button/Nightmare relationship.");
            else Check(tags.Any(tag=>tag.GetProperty("EnumId").GetString()=="2921") && tags.Any(tag=>tag.GetProperty("EnumId").GetString()=="3567" && tag.GetProperty("Value").GetString()=="1"),"Known button provenance was lost.");
        }
    }
    private static void EveryHandGift()
    {
        foreach(var spec in Specs)
        {
            var frame=HandFrame(spec); var result=Eval(frame); var control=Eval(Remove(frame,50));
            Verified(result,spec.Coverage); Same(result,control,50);
            Check(Find(result,30).Attack==spec.Attack && Find(result,30).MaximumHealth==spec.Health && result.Result.AvailableMana==5-spec.Cost &&
                Find(result,40).RemainingHealth==30-spec.Attack,"An observed modifier was applied again or current cost was replaced.");
        }
    }
    private static void ShieldConsumption()
    {
        var frame=ShieldFrame(1); var result=Eval(frame,ShieldActions); Same(result,Eval(Remove(frame,50),ShieldActions),50);
        Verified(result,Specs[4].Coverage);
        Check(Find(result,30).RemainingHealth==6 && Find(result,30).Flags["DIVINE_SHIELD"]==0 && Find(result,30).AttacksUsed==2 && Find(result,40).RemainingHealth==14,
            "Second Windfury attack restored the shield or ignored current damage.");
    }
    private static void AlreadySpentShield()
    {
        var frame=ShieldFrame(0); var result=Eval(frame,ShieldActions); Verified(result,Specs[4].Coverage);
        Same(result,Eval(Remove(frame,50),ShieldActions),50);
        Check(Find(result,30).RemainingHealth==4 && Find(result,30).Flags["DIVINE_SHIELD"]==0,"Persistent gift metadata restored an already-spent shield.");
        Unverified(Tag(frame,30,"DIVINE_SHIELD",null),ShieldActions);
        Unverified(Tag(frame,30,"WINDFURY","0"),ShieldActions);
        Unverified(Tag(frame,30,"194","1"),ShieldActions);
    }
    private static void MultipleStaticSources()
    {
        var frame=Frame(E(30,"EDR_227","MINION","HAND",1,("ATK","9"),("HEALTH","7"),("DAMAGE","0"),("COST","1"),("RUSH","1"),("DEATHRATTLE","1"),
            ("CHARGE","1"),("LIFESTEAL","1"),("DIVINE_SHIELD","1"),("WINDFURY","1")),
            Gift(50,"EDR_100t6e"),Gift(51,"EDR_100te"),Gift(52,"EDR_100t8e1"),Gift(53,"EDR_100t13e"));
        frame=Tag(Tag(Tag(frame,40,"ATK","1"),40,"HEALTH","1"),21,"DAMAGE","21");
        PlannedAction[] actions=[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,40),new(ActionKind.Attack,30,21)];
        var result=Eval(frame,actions); Verified(result,"dark-gift-observed-charge:EDR_100t6e","dark-gift-observed-lifesteal:EDR_100te",Specs[3].Coverage,Specs[4].Coverage);
        Same(result,Eval(Remove(frame,50,51,52,53),actions),50,51,52,53);
        Check(result.IsLethal && Find(result,30).Attack==9 && Find(result,30).MaximumHealth==7 && Find(result,30).Flags["DIVINE_SHIELD"]==0 &&
            Find(result,11).RemainingHealth==30 && result.Result.AvailableMana==4,"Static sources were combined as new grants or repeated bonuses.");
        var duplicate=Add(frame,Gift(54,"EDR_100t8e1"));
        var again=Eval(duplicate,actions); Verified(again,Specs[3].Coverage);Check(Find(again,30).Attack==9,"Duplicate static records reapplied printed changes.");
    }
    private static void SettledRecords()
    {
        foreach(var spec in Specs.Where(spec=>spec.Id is "EDR_100t8e" or "EDR_100t5e2" or "EDR_100t5e5"))
        {
            var frame=Tag(Tag(Tag(HandFrame(spec),30,"ATK","11"),30,"HEALTH","13"),30,"COST","0");
            var before=JsonSerializer.Serialize(frame); var result=Eval(frame); Verified(result,spec.Coverage);
            Check(Find(result,30).Attack==11 && Find(result,30).MaximumHealth==13 && result.Result.AvailableMana==5 && result.Result.Entities.Count==frame.Entities.Count &&
                before==JsonSerializer.Serialize(frame),"A settled annotation reran a setter/shuffle/copy or mutated the snapshot.");
        }
    }
    private static void ElusiveTargets()
    {
        var frame=Parse(EnemyKeywordFrame("EDR_100t1e","1211"));
        Check(frame.Entities[40].GetIntTag("ELUSIVE")==1 && !frame.Entities[40].Tags.ContainsKey("1211"),"Real Core numeric Elusive normalization was not exercised.");
        var spell=Eval(frame,[new(ActionKind.PlayCard,36,40)]);
        Check(spell.Verification==LineVerification.Invalid && spell.AppliedActions==0,"Elusive was targetable by a spell.");
        var battlecry=Eval(frame,[new(ActionKind.PlayCard,35,40)]); Verified(battlecry,Specs[0].Coverage);
        var attack=Eval(frame,[new(ActionKind.Attack,30,40)]); Verified(attack,Specs[0].Coverage);
        Check(Find(battlecry,40).RemainingHealth==5 && Find(attack,40).RemainingHealth==5,"Elusive blocked a covered battlecry or ordinary combat.");
    }
    private static void DistinctTargetingFlags()
    {
        foreach(var key in new[]{"311","332"})
        {
            var frame=Parse(EnemyKeywordFrame("EDR_100t1e",key));
            var result=Eval(frame,[new(ActionKind.Attack,30,40)]);
            Check(result.Verification!=LineVerification.Verified && !result.RuleCoverage.Contains(Specs[0].Coverage) && !frame.Entities[40].Tags.ContainsKey("ELUSIVE"),
                "A narrower targeting flag was mistaken for an observed gift keyword.");
        }
    }
    private static void TauntTargets()
    {
        var frame=Parse(EnemyKeywordFrame("EDR_100t3e","190"));
        var blocked=Eval(frame,[new(ActionKind.Attack,30,21)]);
        Check(blocked.Verification==LineVerification.Invalid && blocked.AppliedActions==0,"An enemy gifted Taunt was bypassed.");
        Verified(Eval(frame,[new(ActionKind.Attack,30,40)]),Specs[2].Coverage);
    }
    private static void SourceGameplayFlags()
    {
        foreach(var spec in Specs)
        foreach(var (key,value) in new[]{("DEATHRATTLE","bad"),("BATTLECRY","bad"),("TRIGGER_VISUAL","bad"),("AURA","bad"),("REBORN","bad"),("SECRET","bad"),
            ("DEATHRATTLE","1"),("TRIGGER_VISUAL","1"),("SPELLPOWER","bad"),("TAG_SCRIPT_DATA_NUM_1","1"),("SILENCED","bad")})
        {
            var result=Eval(Parse(Tag(HandFrame(spec),50,key,value)));
            Check(result.Verification!=LineVerification.Verified && !result.RuleCoverage.Contains(spec.Coverage),"Malformed gameplay evidence was lost during integer materialization.");
        }
    }
    private static void BrokenEvidence()
    {
        foreach(var spec in Specs)
        {
            foreach(var key in new[]{"ATK","HEALTH","DAMAGE","COST"})Unverified(Tag(HandFrame(spec),30,key,null));
            foreach(var keyword in spec.Keywords)Unverified(Tag(HandFrame(spec),30,keyword.Name,null));
            foreach(var (id,key,value) in new[]{(50,"ATTACHED","999"),(50,"CONTROLLER","2"),(50,"CARDTYPE","SPELL"),(50,"HEALTH","0"),(30,"202","5"),(30,"45","0"),(30,"COST","bad"),(30,"BATTLECRY","bad"),(30,"CRAFTABLE_FIRST_HALF_DBID","1")})
                Unverified(Tag(HandFrame(spec),id,key,value));
            var card=FixedCards.Value[spec.Id]; var changed=FixedCards.Value.ToDictionary();
            changed[card.Id]=card with{DbfId=1}; Unverified(HandFrame(spec),cards:changed);
            var raw=JsonNode.Parse(card.RawJson)!.AsObject();raw["mechanics"]=new JsonArray("TRIGGER_VISUAL");changed[card.Id]=card with{RawJson=raw.ToJsonString()};Unverified(HandFrame(spec),cards:changed);
            Unverified(HandFrame(spec) with{ClientBuild="255791"});
        }
    }
    private static void MarkerAliases()
    {
        foreach(var spec in Specs)
        {
            var frame=HandFrame(spec);var source=FixedCards.Value[spec.Id];
            using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","dark-gift-static-255790.json")));
            var tags=fixture.RootElement.GetProperty("cardDefsEvidence").EnumerateArray().Single(row=>row.GetProperty("CardId").GetString()==spec.Id).GetProperty("Tags").EnumerateArray();
            foreach(var tag in tags.Where(tag=>tag.GetProperty("EnumId").GetString() is "2921" or "3567" or "330" or "331"))
                frame=Tag(frame,50,tag.GetProperty("EnumId").GetString()!,tag.GetProperty("Value").GetString());
            foreach(var keyword in spec.Keywords)
            {
                frame=Tag(frame,30,keyword.Alias,"1");
                frame=Tag(frame,50,keyword.Alias,"1");
            }
            Verified(Eval(frame),spec.Coverage);
            Unverified(Tag(frame,50,"2921","999999"));
            var reordered=JsonNode.Parse(source.RawJson)!.AsObject().Reverse().ToArray();var raw=new JsonObject();foreach(var pair in reordered)raw.Add(pair.Key,pair.Value?.DeepClone());
            var cards=FixedCards.Value.ToDictionary();cards[source.Id]=source with{RawJson=raw.ToJsonString()};Verified(Eval(HandFrame(spec),cards:cards),spec.Coverage);
        }
        // A stat-only annotation cannot certify an otherwise uninterpreted numeric keyword.
        // Numeric LIFESTEAL is intentionally not normalized by current Core; keep that gap visible.
        Unverified(Tag(HandFrame(Specs[1]),30,"685","1"));
        foreach(var spec in Specs.Concat(new[]{new Spec("EDR_100t6e",113809,"charge",5,2,1,("CHARGE","197")),new Spec("EDR_100te",113803,"lifesteal",8,2,1,("LIFESTEAL","685"))}))
        {
            var frame=HandFrame(spec);
            foreach(var (name,alias) in new[]{("ENCHANTMENT_BIRTH_VISUAL","330"),("ENCHANTMENT_IDLE_VISUAL","331")})
            {frame=Tag(frame,50,name,"0");frame=Tag(frame,50,alias,"0");}
            var result=Eval(frame);Verified(result,spec.Coverage);Same(result,Eval(Remove(frame,50)),50);
            Unverified(Tag(frame,50,"330","1")); // Contradictory named and numeric values remain invalid.
        }
    }
    private static void CoveredTargetDeath()
    {
        var frame=Frame(
            E(30,"CS2_171","MINION","PLAY",1,("ATK","5"),("HEALTH","2"),("DAMAGE","0"),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1")),
            E(32,"CS2_171","MINION","PLAY",1,("ATK","3"),("HEALTH","1"),("DAMAGE","0"),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1")),
            E(40,"CS2_171","MINION","PLAY",2,("ATK","1"),("HEALTH","5"),("DAMAGE","0"),("TAUNT","1")),Gift(50,"EDR_100t3e",40,2));
        frame=Parse(Tag(frame,21,"DAMAGE","27"));
        PlannedAction[] actions=[new(ActionKind.Attack,30,40),new(ActionKind.Attack,32,21)];
        var before=JsonSerializer.Serialize(frame);var result=Eval(frame,actions);Verified(result,Specs[2].Coverage);
        Same(result,Eval(Remove(frame,50),actions),50);
        Check(result.IsLethal && result.AppliedActions==2 && !Find(result,40).IsAlive && Find(result,50).Zone=="PLAY" && Find(result,50).IsAlive && before==JsonSerializer.Serialize(frame),
            "A dead target kept a fully audited static effect active or cleanup invented a client entity/zone update.");
        var found=new LineEvaluator().FindLethal(frame,FixedCards.Value,2,5000);
        Check(found.VerifiedLethalFound && found.Evaluation is{Verification:LineVerification.Verified,IsLethal:true},"Search could not continue after a covered gifted Taunt death.");
        Unverified(Tag(frame,40,"DAMAGE","5"),[new(ActionKind.Attack,32,21)]);
        Unverified(Tag(frame,50,"DEATHRATTLE","bad"),actions);
        Unverified(Tag(frame,50,"ATTACHED","999"),actions);
        Unverified(Add(frame,Gift(51,"EDR_100t5e",40,2)),actions);
        foreach(var flag in new[]{"DEATHRATTLE","REBORN"})
        {
            var pending=Eval(Tag(frame,40,flag,"1"),actions);
            Check(pending.Verification!=LineVerification.Verified && !pending.IsLethal && pending.UnsupportedMechanics.Contains("DEATHRATTLE_OR_REBORN"),
                "A static attachment hid an unresolved deathrattle or reborn boundary.");
        }
    }
    private static void OngoingAndRaptor()
    {
        foreach(var id in new[]{"EDR_100t4e","EDR_100t5e","EDR_100t7e","EDR_100t7e1","EDR_100t9e","EDR_100t10"})
        {
            var frame=Add(HandFrame(Specs[3]),Gift(51,id));
            if(id=="EDR_100t7e")
            {
                var harmless=Eval(frame);
                Check(harmless.Verification==LineVerification.Verified && harmless.RuleCoverage.Contains(Specs[3].Coverage) && harmless.Result.HeroPowerChanges.Count==0,
                    "A Battlecry-only attachment multiplied a non-Battlecry summon/attack or lost already observed static values.");
            }
            else Unverified(frame);
        }
        var raptor=E(60,"EDR_849","MINION","PLAY",1,("ATK","2"),("HEALTH","3"),("DAMAGE","0"),("EXHAUSTED","1"),("NUM_TURNS_IN_PLAY","1"));
        var result=Eval(Add(HandFrame(Specs[3]),raptor));
        Check(result.Result.RaptorBoundaryReached && result.AppliedActions==1 && !result.IsLethal && result.Verification!=LineVerification.Verified,"Expanded static coverage crossed a real Raptor observation boundary.");
    }
    private static readonly PlannedAction[] ShieldActions=[new(ActionKind.Attack,30,40),new(ActionKind.Attack,30,40)];
    private static GameSnapshot ShieldFrame(int shield)=>Frame(E(30,"DINO_421","MINION","PLAY",1,("ATK","8"),("HEALTH","8"),("DAMAGE","0"),("COST","8"),
        ("DIVINE_SHIELD",shield.ToString()),("WINDFURY","1"),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1")),Gift(50,"EDR_100t13e"),
        E(40,"CS2_171","MINION","PLAY",2,("ATK","2"),("HEALTH","30"),("DAMAGE","0")));
    private static GameSnapshot HandFrame(Spec spec)
    {
        var frame=Frame(E(30,"EDR_227","MINION","HAND",1,("ATK",spec.Attack.ToString()),("HEALTH",spec.Health.ToString()),("DAMAGE","0"),("COST",spec.Cost.ToString()),("RUSH","1"),("DEATHRATTLE","1")),Gift(50,spec.Id));
        foreach(var keyword in spec.Keywords)frame=Tag(frame,30,keyword.Name,"1");return frame;
    }
    private static GameSnapshot EnemyKeywordFrame(string gift,string keyword)=>Frame(
        E(30,"CS2_171","MINION","PLAY",1,("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("EXHAUSTED","0"),("NUM_TURNS_IN_PLAY","1")),
        E(35,"CS2_189","MINION","HAND",1,("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("COST","1"),("BATTLECRY","1")),
        E(36,"CORE_DS1_185","SPELL","HAND",1,("COST","1")),
        E(40,"EDR_227","MINION","PLAY",2,("ATK","0"),("HEALTH","6"),("DAMAGE","0"),(keyword,"1")),Gift(50,gift,40,2));
    private static GameSnapshot Frame(params EntitySnapshot[] extra)
    {
        var values=new Dictionary<int,EntitySnapshot>{
            [2]=new(2,null,EntityKind.Player,new Dictionary<string,string>{["PLAYER_ID"]="1",["HERO_ENTITY"]="11",["RESOURCES"]="5",["RESOURCES_USED"]="0",["TEMP_RESOURCES"]="0",["CONTROLLER"]="1"}.ToFrozenDictionary()),
            [3]=new(3,null,EntityKind.Player,new Dictionary<string,string>{["PLAYER_ID"]="2",["HERO_ENTITY"]="21",["CONTROLLER"]="2"}.ToFrozenDictionary()),
            [11]=E(11,"HERO_05","HERO","PLAY",1,("CLASS","HUNTER"),("ATK","0"),("HEALTH","30"),("DAMAGE","10"),("ARMOR","0")),
            [21]=E(21,"HERO_08","HERO","PLAY",2,("CLASS","MAGE"),("ATK","0"),("HEALTH","30"),("DAMAGE","0"),("ARMOR","0")),
            [40]=E(40,"CS2_171","MINION","PLAY",2,("ATK","0"),("HEALTH","30"),("DAMAGE","0"))};
        foreach(var entity in extra)values[entity.Id]=entity;
        return new("expanded-static-gifts",7,40,3,1,1,GamePhase.Main,values.ToFrozenDictionary(),new(true,true,true,true,0,[])){ClientBuild="255790"};
    }
    private static GameSnapshot Parse(GameSnapshot frame)
    {
        var engine=new PowerLogStateEngine(1,frame.MatchId);
        foreach(var line in new[]{"GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED","CREATE_GAME","GameEntity EntityID=1","tag=STATE value=RUNNING","tag=TURN value=0"})engine.ProcessLine(line);
        foreach(var entity in frame.Entities.Values.OrderBy(entity=>entity.Id))
        {
            engine.ProcessLine(entity.Kind==EntityKind.Player?$"Player EntityID={entity.Id} PlayerID={entity.GetIntTag("PLAYER_ID")}":$"FULL_ENTITY - Creating ID={entity.Id} CardID={entity.CardId}");
            foreach(var tag in entity.Tags)engine.ProcessLine($"tag={tag.Key} value={tag.Value}");
        }
        foreach(var line in new[]{"TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value=1","TAG_CHANGE Entity=3 tag=CURRENT_PLAYER value=0","TAG_CHANGE Entity=1 tag=TURN value=3","TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY","TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION","PowerProcessor.EndCurrentTaskList() - complete"})engine.ProcessLine(line);
        return engine.Snapshot();
    }
    private static EntitySnapshot E(int id,string card,string type,string zone,int owner,params (string Key,string Value)[] extra)
    {var tags=new Dictionary<string,string>{["CARDTYPE"]=type,["ZONE"]=zone,["CONTROLLER"]=owner.ToString()};foreach(var pair in extra)tags[pair.Key]=pair.Value;return new(id,card,EntityKind.Card,tags.ToFrozenDictionary());}
    private static EntitySnapshot Gift(int id,string card,int target=30,int owner=1)=>E(id,card,"ENCHANTMENT","PLAY",owner,("ATTACHED",target.ToString()));
    private static GameSnapshot Add(GameSnapshot frame,EntitySnapshot entity){var values=frame.Entities.ToDictionary();values[entity.Id]=entity;return frame with{Entities=values.ToFrozenDictionary()};}
    private static GameSnapshot Remove(GameSnapshot frame,params int[] ids)=>frame with{Entities=frame.Entities.Where(pair=>!ids.Contains(pair.Key)).ToFrozenDictionary()};
    private static GameSnapshot Tag(GameSnapshot frame,int id,string key,string? value){var tags=frame.Entities[id].Tags.ToDictionary();if(value is null)tags.Remove(key);else tags[key]=value;return Add(frame,frame.Entities[id] with{Tags=tags.ToFrozenDictionary()});}
    private static LineEvaluation Eval(GameSnapshot frame,PlannedAction[]? actions=null,IReadOnlyDictionary<string,CardRecord>? cards=null)=>new LineEvaluator().EvaluateLine(frame,actions??[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,40)],cards??FixedCards.Value);
    private static SimulatedEntity Find(LineEvaluation result,int id)=>result.Result.Entities.Single(entity=>entity.EntityId==id);
    private static void Verified(LineEvaluation result,params string[] coverage)=>Check(result.Verification==LineVerification.Verified && coverage.All(result.RuleCoverage.Contains),"Static gift was not covered: "+JsonSerializer.Serialize(result));
    private static void Unverified(GameSnapshot frame,PlannedAction[]? actions=null,IReadOnlyDictionary<string,CardRecord>? cards=null){var result=Eval(frame,actions,cards);Check(result.Verification!=LineVerification.Verified && !result.IsLethal,"Bad or unresolved gift evidence became Verified: "+JsonSerializer.Serialize(result));}
    private static void Same(LineEvaluation actual,LineEvaluation control,params int[] ignored)
    {Check(actual.AppliedActions==control.AppliedActions && actual.Result.AvailableMana==control.Result.AvailableMana && actual.Result.Entities.Where(entity=>!ignored.Contains(entity.EntityId)).Select(Shape).SequenceEqual(control.Result.Entities.Select(Shape)),"Static classification changed combat, current values or entities.");}
    private static string Shape(SimulatedEntity entity)=>JsonSerializer.Serialize(new{entity.EntityId,entity.Attack,entity.MaximumHealth,entity.RemainingHealth,entity.Armor,entity.Cost,entity.AttacksUsed,entity.IsAlive,entity.Zone,Flags=entity.Flags.OrderBy(pair=>pair.Key,StringComparer.Ordinal)});
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        var cards=new Dictionary<string,CardRecord>(StringComparer.Ordinal);
        foreach(var file in new[]{"beast-hunter-255790.json","dark-gift-static-255790.json","lethal-search-255790.json","quest-progress-255790.json"})
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",file)));
            foreach(var raw in doc.RootElement.GetProperty("cards").EnumerateArray())
            {
                string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";
                string[] List(string key)=>raw.TryGetProperty(key,out var array)?array.EnumerateArray().Select(value=>value.GetString()!).ToArray():[];
                var id=Text("id");cards[id]=new(id,raw.GetProperty("dbfId").GetInt32(),Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,
                    raw.TryGetProperty("collectible",out var collectible)&&collectible.GetBoolean(),List("classes"),List("entourage"),raw.GetRawText());
            }
        }return cards;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
