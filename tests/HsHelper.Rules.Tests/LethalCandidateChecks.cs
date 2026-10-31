using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class LethalCandidateChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> FixedCards = new(ReadCards);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("lethal search simple Charge control preserves fixed public metadata provenance", Provenance);
        yield return ("lethal search uses Coin to enable a currently unaffordable Arcane Shot", CoinShot);
        yield return ("lethal search uses Coin before an otherwise unaffordable Steady Shot", CoinPower);
        yield return ("lethal search includes observed Charge on a native non-Rush hunter body", GiftCharge);
        yield return ("lethal search connects Coin native Charge summon and hero attack", CoinGiftCharge);
        yield return ("native simple Charge shares manual keyword fallback and explicit suppression", NativeCharge);
        yield return ("observed Charge Elven Archer preserves its required battlecry target", TargetedCharge);
        yield return ("new Charge candidates do not invent missing current costs or combat attributes", MissingObservations);
        yield return ("new Charge candidates reject contradictory source and keyword observations", SourceConflicts);
        yield return ("new search paths retain fixed gift metadata attachment and build audits", GiftAudits);
        yield return ("new search paths cannot cross Raptor secret or unknown enchantment boundaries", ObservationBoundaries);
        yield return ("new Charge searches retain board capacity mana taunt immunity and attack restrictions", CombatAndPayment);
        yield return ("Parser-observed hand restrictions survive entry and protect manual and searched combat", CarryAttackRestrictions);
        yield return ("new candidate profile inspection safely rejects malformed or excessive effect data", CandidateDataLimits);
        yield return ("new search paths remain bounded cancellable stable and immutable", SearchLimits);
    }

    private static void Provenance()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","lethal-search-255790.json")));
        var root = doc.RootElement; var card = FixedCards.Value["CS2_171"];
        Check(root.GetProperty("build").GetString()=="255790" &&
            root.GetProperty("sourceSha256").GetString()=="7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            root.GetProperty("cardDefsSha256").GetString()=="EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8" &&
            card.DbfId==648 && card.Type=="MINION" && card.Text=="<b>冲锋</b>" && card.Cost==1,
            "Native Charge was inferred from an invented identity or different source.");
        using var raw = JsonDocument.Parse(card.RawJson);
        Check(raw.RootElement.GetProperty("attack").GetInt32()==1 && raw.RootElement.GetProperty("health").GetInt32()==1 &&
            raw.RootElement.GetProperty("mechanics").EnumerateArray().Select(value=>value.GetString()).SequenceEqual(["CHARGE"]),
            "Native Charge source definition changed.");
    }
    private static void CoinShot()
    {
        var frame = CoinShotFrame(); var result = Found(frame,2);
        Sequence(result, new(ActionKind.PlayCard,29), new(ActionKind.PlayCard,31,21));
        Check(result.Evaluation!.Result.AvailableMana==0 && result.Evaluation.RuleCoverage.Contains("coin-temporary-mana"),"Coin did not pay for the observed one-cost spell.");
        var coin=FixedCards.Value["GAME_005"];
        foreach(var bad in new[] {coin with{DbfId=1},coin with{Type="MINION"},coin with{Text="不能获得法力水晶。"},
            coin with{RawJson="{}"},coin with{RawJson="broken"},
            Raw(coin,raw=>raw["dbfId"]=1),Raw(coin,raw=>raw["text"]="不能获得法力水晶。") with{Text="不能获得法力水晶。"},
            Raw(coin,raw=>raw["mechanics"]=new JsonArray("TRIGGER_VISUAL"))})
        {
            var cards=With(bad); NotFound(frame,cards);
            var rejected=new LineEvaluator().EvaluateLine(frame,[new(ActionKind.PlayCard,29),new(ActionKind.PlayCard,31,21)],cards);
            Check(rejected.Verification==LineVerification.Unsupported && rejected.AppliedActions==0 && !rejected.IsLethal &&
                rejected.Result.AvailableMana==0 && rejected.Result.Entities.Single(entity=>entity.EntityId==29).Zone=="HAND",
                "A corrupted Coin reached payment, temporary mana, or a later action.");
        }
        // Canonical full definitions accept harmless object property ordering differences.
        var reversed=JsonNode.Parse(coin.RawJson)!.AsObject().Reverse().ToArray(); var reordered=new JsonObject();
        foreach(var pair in reversed) reordered.Add(pair.Key,pair.Value?.DeepClone());
        var canonical=new LineEvaluator().FindLethal(frame,With(coin with{RawJson=reordered.ToJsonString()}),2,5000);
        Check(canonical.VerifiedLethalFound,"Coin audit was tied to JSON property ordering.");
        foreach(var type in new[]{"MINION","WEAPON"})
        {
            // Record, raw and observed type agree with each other, but contradict the fixed
            // GAME_005 identity. Neither simple summon nor equip may bypass its native gate.
            var impostor=Raw(coin,raw=>{raw["type"]=type;raw["text"]="";raw["attack"]=1;raw["health"]=1;}) with{Type=type,Text=""};
            var source=Card(29,"GAME_005",type,"HAND",("COST","0"),("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("CHARGE","1"));
            var fakeFrame=Frame(0,1,source); var fakeCards=With(impostor);
            NotFound(fakeFrame,fakeCards);
            var rejected=new LineEvaluator().EvaluateLine(fakeFrame,[new(ActionKind.PlayCard,29),new(ActionKind.Attack,type=="MINION"?29:11,21)],fakeCards);
            Check(rejected.Verification==LineVerification.Unsupported && rejected.AppliedActions==0 && !rejected.IsLethal &&
                rejected.UnsupportedMechanics.Contains("UNAUDITED_COIN_DEFINITION") &&
                rejected.Result.Entities.Single(entity=>entity.EntityId==29).Zone=="HAND" && rejected.Result.AvailableMana==0,
                "A same-ID Coin definition bypassed its native audit through a consistent different type.");
        }
    }
    private static void CoinPower()
    {
        var frame = Frame(1,2,Coin(),Card(32,"HERO_05bp","HERO_POWER","PLAY",("COST","2"),("EXHAUSTED","0")));
        var result = Found(frame,2);
        Sequence(result,new(ActionKind.PlayCard,29),new(ActionKind.HeroPower,32));
        Check(result.Evaluation!.RuleCoverage.Contains("steady-shot-base-2"),"Search substituted a different source for the supported power.");
    }
    private static void GiftCharge()
    {
        var result = Found(DinoFrame(),2);
        Sequence(result,new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21));
        Check(result.Evaluation!.RuleCoverage.Contains("dark-gift-observed-charge:EDR_100t6e") &&
            Entity(result,30).Attack==8 && Entity(result,30).Cost==8 && result.Evaluation.Result.AvailableMana==0,
            "The search reapplied a gift or bypassed its current audit.");
    }
    private static void CoinGiftCharge()
    {
        var frame = Add(DinoFrame(7),Coin()); var result = Found(frame,3);
        Sequence(result,new(ActionKind.PlayCard,29),new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21));
        Check(result.Evaluation!.Result.AvailableMana==0 && Entity(result,29).Zone=="GRAVEYARD","Coin was reused or resources were read from the pre-Coin frame.");
    }
    private static void NativeCharge()
    {
        foreach(var explicitFlag in new[]{true,false})
        {
            var frame = BoarFrame(); if(!explicitFlag) frame=Tag(frame,30,"CHARGE",null);
            var manual = new LineEvaluator().EvaluateLine(frame,[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21)],FixedCards.Value);
            Check(manual.Verification==LineVerification.Verified && manual.IsLethal,"Native Charge manual control was not covered.");
            Sequence(Found(frame,2),new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21));
        }
        NotFound(Tag(BoarFrame(),30,"CHARGE","0"));
        NotFound(Tag(Tag(BoarFrame(),30,"CHARGE",null),30,"SILENCED","1"));
        var buffed = Tag(Tag(Tag(BoarFrame(),30,"ATK","3"),30,"COST","3"),2,"RESOURCES","3");
        buffed = Tag(buffed,21,"DAMAGE","27");
        Check(Entity(Found(buffed,2),30).Attack==3,"Search replaced current boosted stats with printed values.");
    }
    private static void TargetedCharge()
    {
        var archer=Card(30,"CS2_189","MINION","HAND",("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("COST","1"),("CHARGE","1"),("BATTLECRY","1"));
        var frame=Frame(1,2,archer,Gift());
        var result=Found(frame,2);
        Sequence(result,new(ActionKind.PlayCard,30,21),new(ActionKind.Attack,30,21));
        Check(result.Evaluation!.RuleCoverage.Contains("elven-archer-battlecry-1"),"Archer was incorrectly generated as a no-target summon.");
    }
    private static void MissingObservations()
    {
        foreach(var key in new[]{"COST","ATK","HEALTH","DAMAGE"}) NotFound(Tag(BoarFrame(),30,key,null));
        NotFound(Tag(CoinShotFrame(),29,"COST",null));
        NotFound(Tag(DinoFrame(),30,"CHARGE",null)); // It has no native printed Charge.
        NotFound(Tag(DinoFrame(),30,"CHARGE","0"));
        var noCatalog=FixedCards.Value.Where(pair=>pair.Key!="DINO_421").ToDictionary();
        NotFound(DinoFrame(),noCatalog);
        NotFound(DinoFrame() with { Quality=new(true,false,true,true,0,[]) });
        NotFound(DinoFrame() with { ActivePlayerId=2 });
    }
    private static void SourceConflicts()
    {
        foreach(var (key,value) in new[]{("CHARGE","2"),("CHARGE","bad"),("197","0"),("197","bad"),
            ("47","0"),("45","0"),("48","0"),("44","1"),("DAMAGE","1"),("FROZEN","1"),("260","1"),("DORMANT","1"),
            ("202","5"),("49","1"),("50","2"),("CONTROLLER","2"),("CARDTYPE","SPELL"),("CRAFTABLE_FIRST_HALF_DBID","1")})
            NotFound(Tag(BoarFrame(),30,key,value));
        NotFound(Replace(BoarFrame(),BoarFrame().Entities[30] with{Kind=EntityKind.Unknown}));
        NotFound(Tag(CoinShotFrame(),29,"CARDTYPE","MINION"));
        NotFound(Tag(CoinShotFrame(),29,"COST","-1"));
        foreach(var value in new[]{"bad","2","-1"})
            NotFound(Tag(Tag(BoarFrame(),30,"CHARGE",null),30,"SILENCED",value));
        NotFound(Tag(Tag(Tag(BoarFrame(),30,"CHARGE",null),30,"SILENCED","0"),30,"188","1"));
        NotFound(Tag(Tag(Tag(BoarFrame(),30,"CHARGE",null),30,"SILENCED","1"),30,"188","0"));
        var explicitlyUnsilenced=Tag(Tag(Tag(BoarFrame(),30,"CHARGE",null),30,"SILENCED","0"),30,"188","0");
        Sequence(Found(explicitlyUnsilenced,2),new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21));
    }
    private static void GiftAudits()
    {
        foreach(var (key,value) in new[]{("ATTACHED","999"),("CONTROLLER","2"),("CARDTYPE","SPELL"),("197","0")})
            NotFound(Tag(DinoFrame(),key=="197"?30:50,key,value));
        NotFound(DinoFrame() with{ClientBuild="255791"});
        var gift=FixedCards.Value["EDR_100t6e"];
        var target=FixedCards.Value["DINO_421"];
        foreach(var bad in new[]{gift with{DbfId=1},gift with{Text="未知赠福"},target with{DbfId=1},target with{Text="未知使用效果"}})
            NotFound(DinoFrame(),With(bad));
    }
    private static void ObservationBoundaries()
    {
        NotFound(Add(DinoFrame(),Card(60,"EDR_100t5e","ENCHANTMENT","PLAY",("ATTACHED","30"))));
        NotFound(Add(DinoFrame(),Card(60,null,"SPELL","SECRET",owner:2)));
        var raptor=Card(60,"EDR_849","MINION","PLAY",("ATK","2"),("HEALTH","3"),("DAMAGE","0"),("EXHAUSTED","1"),("NUM_TURNS_IN_PLAY","1"));
        var frame=Add(DinoFrame(),raptor);
        var manual=new LineEvaluator().EvaluateLine(frame,[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21)],FixedCards.Value);
        Check(manual.Result.RaptorBoundaryReached && manual.AppliedActions==1 && !manual.IsLethal,"Raptor negative control did not reach the actual observation boundary.");
        NotFound(frame);
    }
    private static void CombatAndPayment()
    {
        NotFound(DinoFrame(7));
        NotFound(Tag(CoinShotFrame(),29,"COST","1"));
        NotFound(Tag(DinoFrame(),21,"IMMUNE","1"));
        NotFound(Tag(DinoFrame(),30,"CANT_ATTACK","1"));
        var taunt=Card(60,"CS2_171","MINION","PLAY",("ATK","0"),("HEALTH","100"),("DAMAGE","0"),("TAUNT","1"),owner:2);
        NotFound(Add(DinoFrame(),taunt));
        var full=DinoFrame();
        for(var id=60;id<67;id++) full=Add(full,Card(id,"CS2_171","MINION","PLAY",("ATK","0"),("HEALTH","1"),("DAMAGE","0"),("EXHAUSTED","1"),("NUM_TURNS_IN_PLAY","1")));
        NotFound(full);
    }
    private static void CandidateDataLimits()
    {
        var card=FixedCards.Value["CS2_171"];
        foreach(var raw in new[]{"broken","[]","{\"mechanics\":1}"}) NotFound(BoarFrame(),With(card with{RawJson=raw}));
        // A long unterminated HTML-like run makes the existing bounded SimpleProfile regex time out.
        // Manual evaluation already converts that to Unsupported; candidate enumeration must do likewise.
        var longCard=card with{Text=new string('<',200_000)};
        var cards=With(longCard);
        var manual=new LineEvaluator().EvaluateLine(BoarFrame(),[new(ActionKind.PlayCard,30)],cards);
        Check(manual.Verification==LineVerification.Unsupported && manual.UnsupportedMechanics.Contains("EVALUATION_LIMIT"),"Long-text control did not exercise the bounded profile path.");
        NotFound(BoarFrame(),cards);
    }
    private static void CarryAttackRestrictions()
    {
        var evaluator=new LineEvaluator();
        foreach(var key in new[]{"CANT_ATTACK_HEROES","CANNOT_ATTACK_HEROES","CANT_ATTACK_MINIONS","FROZEN","DORMANT","UNTOUCHABLE","CANT_ATTACK"})
        {
            var frame=ParsedRestriction(key); var before=JsonSerializer.Serialize(frame);
            Check(frame.Quality.IsComplete && frame.Quality.IsStable && frame.Entities[30].GetIntTag(key)==1,"Parser did not preserve the tested current restriction.");
            var attackHero=evaluator.EvaluateLine(frame,[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21)],FixedCards.Value);
            var attackMinion=evaluator.EvaluateLine(frame,[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,40)],FixedCards.Value);
            Check(attackHero.Result.Entities.Single(entity=>entity.EntityId==30).Flags.GetValueOrDefault(key)==1,
                "Entry erased an observed attack restriction: "+key);
            if(key=="CANT_ATTACK_MINIONS")
            {
                Check(attackHero.Verification==LineVerification.Verified && attackHero.IsLethal && attackMinion.Verification==LineVerification.Invalid && attackMinion.AppliedActions==1,
                    "A minion-only prohibition incorrectly stopped legal hero combat or permitted its forbidden target.");
                Sequence(Found(frame,2),new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21));
            }
            else
            {
                Check(attackHero.Verification==LineVerification.Invalid && !attackHero.IsLethal && attackHero.AppliedActions==1,
                    "Entry washed away a restriction and allowed lethal: "+key);
                if(key is "CANT_ATTACK_HEROES" or "CANNOT_ATTACK_HEROES")
                    Check(attackMinion.Verification==LineVerification.Verified && attackMinion.AppliedActions==2,
                        "A hero-only prohibition incorrectly stopped legal minion combat.");
                else Check(attackMinion.Verification==LineVerification.Invalid && attackMinion.AppliedActions==1,
                    "A globally restricted attacker was allowed to attack a minion.");
                NotFound(frame);
            }
            Check(before==JsonSerializer.Serialize(frame),"Restriction preservation mutated the real input frame.");
        }
        var reset=ParsedRestriction("EXHAUSTED"); reset=Tag(reset,30,"NUM_ATTACKS_THIS_TURN","3");
        var entry=evaluator.EvaluateLine(reset,[new(ActionKind.PlayCard,30),new(ActionKind.Attack,30,21)],FixedCards.Value);
        Check(entry.Verification==LineVerification.Verified && entry.IsLethal && entry.Result.Entities.Single(entity=>entity.EntityId==30).AttacksUsed==1,
            "Preserving restrictions accidentally preserved pre-entry readiness or attack counts.");
    }
    private static GameSnapshot ParsedRestriction(string key)
    {
        var engine=new PowerLogStateEngine(1,"lethal-search-parser-restriction-"+key);
        foreach(var line in new[]{
            "GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED",
            "CREATE_GAME","GameEntity EntityID=1","tag=STATE value=RUNNING","tag=TURN value=0",
            "Player EntityID=2 PlayerID=1","tag=CONTROLLER value=1","tag=CURRENT_PLAYER value=1","tag=HERO_ENTITY value=11",
            "tag=RESOURCES value=1","tag=RESOURCES_USED value=0","tag=TEMP_RESOURCES value=0","tag=MAXRESOURCES value=10",
            "Player EntityID=3 PlayerID=2","tag=CONTROLLER value=2","tag=CURRENT_PLAYER value=0","tag=HERO_ENTITY value=21",
            "FULL_ENTITY - Creating ID=11 CardID=HERO_05","tag=CONTROLLER value=1","tag=ZONE value=PLAY","tag=CARDTYPE value=HERO",
            "tag=CLASS value=HUNTER","tag=ATK value=0","tag=HEALTH value=30","tag=DAMAGE value=0","tag=ARMOR value=0",
            "FULL_ENTITY - Creating ID=21 CardID=HERO_08","tag=CONTROLLER value=2","tag=ZONE value=PLAY","tag=CARDTYPE value=HERO",
            "tag=CLASS value=MAGE","tag=ATK value=0","tag=HEALTH value=30","tag=DAMAGE value=29","tag=ARMOR value=0",
            "FULL_ENTITY - Creating ID=30 CardID=CS2_171","tag=CONTROLLER value=1","tag=ZONE value=HAND","tag=CARDTYPE value=MINION",
            "tag=ATK value=1","tag=HEALTH value=1","tag=DAMAGE value=0","tag=COST value=1","tag=CHARGE value=1",$"tag={key} value=1",
            "FULL_ENTITY - Creating ID=40 CardID=CS2_171","tag=CONTROLLER value=2","tag=ZONE value=PLAY","tag=CARDTYPE value=MINION",
            "tag=ATK value=0","tag=HEALTH value=1","tag=DAMAGE value=0",
            "TAG_CHANGE Entity=1 tag=TURN value=1","TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY","TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION",
            "PowerProcessor.EndCurrentTaskList() - complete"
        }) engine.ProcessLine(line);
        return engine.Snapshot();
    }
    private static void SearchLimits()
    {
        var frame=Add(DinoFrame(7),Coin()); var before=JsonSerializer.Serialize(frame);
        var evaluator=new LineEvaluator();
        Check(!evaluator.FindLethal(frame,FixedCards.Value,2,100).VerifiedLethalFound,"A three-action chain exceeded maximum depth.");
        var bounded=evaluator.FindLethal(frame,FixedCards.Value,6,1);
        Check(!bounded.VerifiedLethalFound && bounded.ExploredNodes<=1 && !bounded.IsExhaustive,"Search ignored its node limit or claimed exhaustion.");
        var first=Found(frame,3); var second=Found(frame,3);
        Check(first.Actions.SequenceEqual(second.Actions) && before==JsonSerializer.Serialize(frame),"New search paths changed the source or unstable candidate order changed the route.");
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        try { evaluator.FindLethal(frame,FixedCards.Value,cancellationToken:canceled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch(OperationCanceledException) { }
        var capped=Frame(10,2,Coin(),Card(31,"CORE_DS1_185","SPELL","HAND",("COST","11")));
        var noGain=evaluator.FindLethal(capped,FixedCards.Value,6,30);
        Check(!noGain.VerifiedLethalFound && noGain.ExploredNodes<=30 && !noGain.IsExhaustive,"A capped or already-used Coin looped or paid an impossible cost.");
    }

    private static LethalSearchResult Found(GameSnapshot frame,int depth)
    {
        var before=JsonSerializer.Serialize(frame);
        var result=new LineEvaluator().FindLethal(frame,FixedCards.Value,depth,5000);
        Check(result.VerifiedLethalFound && result.Evaluation is {Verification:LineVerification.Verified,IsLethal:true} && !result.IsExhaustive,
            "Expected covered lethal was missing: "+JsonSerializer.Serialize(result));
        var replay=new LineEvaluator().EvaluateLine(frame,result.Actions,FixedCards.Value);
        Check(replay.Verification==LineVerification.Verified && replay.IsLethal && replay.AppliedActions==result.Actions.Count && before==JsonSerializer.Serialize(frame),
            "Search returned a route that its actual final validator could not verify or changed the input.");
        return result;
    }
    private static void NotFound(GameSnapshot frame,IReadOnlyDictionary<string,CardRecord>? cards=null)
    {
        var result=new LineEvaluator().FindLethal(frame,cards??FixedCards.Value,4,5000);
        Check(!result.VerifiedLethalFound && result.Evaluation is null && !result.IsExhaustive,"Uncertain or invalid observations produced a verified route: "+JsonSerializer.Serialize(result));
    }
    private static void Sequence(LethalSearchResult result,params PlannedAction[] actions)=>Check(result.Actions.SequenceEqual(actions),"Search used unexpected entities, target requirements, or action order.");
    private static SimulatedEntity Entity(LethalSearchResult result,int id)=>result.Evaluation!.Result.Entities.Single(entity=>entity.EntityId==id);
    private static GameSnapshot CoinShotFrame()=>Frame(0,2,Coin(),Card(31,"CORE_DS1_185","SPELL","HAND",("COST","1")));
    private static GameSnapshot DinoFrame(int mana=8)=>Frame(mana,8,
        Card(30,"DINO_421","MINION","HAND",("ATK","8"),("HEALTH","8"),("DAMAGE","0"),("COST","8"),("CHARGE","1"),("TAUNT","1"),("ELUSIVE","1"),("DEATHRATTLE","1")),Gift());
    private static GameSnapshot BoarFrame()=>Frame(1,1,Card(30,"CS2_171","MINION","HAND",("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("COST","1"),("CHARGE","1")));
    private static EntitySnapshot Coin()=>Card(29,"GAME_005","SPELL","HAND",("COST","0"));
    private static EntitySnapshot Gift()=>Card(50,"EDR_100t6e","ENCHANTMENT","PLAY",("ATTACHED","30"));
    private static GameSnapshot Frame(int mana,int enemyHealth,params EntitySnapshot[] extra)
    {
        var entities=new[] {
            new EntitySnapshot(2,null,EntityKind.Player,new Dictionary<string,string>{["PLAYER_ID"]="1",["HERO_ENTITY"]="11",["RESOURCES"]=mana.ToString(),["RESOURCES_USED"]="0",["TEMP_RESOURCES"]="0",["MAXRESOURCES"]="10"}.ToFrozenDictionary()),
            new EntitySnapshot(3,null,EntityKind.Player,new Dictionary<string,string>{["PLAYER_ID"]="2",["HERO_ENTITY"]="21"}.ToFrozenDictionary()),
            Card(11,"HERO_05","HERO","PLAY",("CLASS","HUNTER"),("ATK","0"),("HEALTH","30"),("DAMAGE","0"),("ARMOR","0")),
            Card(21,"HERO_08","HERO","PLAY",("CLASS","MAGE"),("ATK","0"),("HEALTH","30"),("DAMAGE",(30-enemyHealth).ToString()),("ARMOR","0"),owner:2)
        }.Concat(extra).ToFrozenDictionary(entity=>entity.Id);
        return new("lethal-search-candidates",7,40,3,1,1,GamePhase.Main,entities,new(true,true,true,true,0,[])){ClientBuild="255790"};
    }
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,params (string Key,string Value)[] extra)=>Card(id,cardId,type,zone,extra,1);
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,int owner)=>Card(id,cardId,type,zone,[],owner);
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,(string Key,string Value) first,int owner)=>Card(id,cardId,type,zone,[first],owner);
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,(string Key,string Value) first,(string Key,string Value) second,(string Key,string Value) third,(string Key,string Value) fourth,int owner)=>Card(id,cardId,type,zone,[first,second,third,fourth],owner);
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,(string Key,string Value) first,(string Key,string Value) second,(string Key,string Value) third,(string Key,string Value) fourth,(string Key,string Value) fifth,int owner)=>Card(id,cardId,type,zone,[first,second,third,fourth,fifth],owner);
    private static EntitySnapshot Card(int id,string? cardId,string type,string zone,(string Key,string Value)[] extra,int owner)
    {
        var tags=new Dictionary<string,string>{["CARDTYPE"]=type,["ZONE"]=zone,["CONTROLLER"]=owner.ToString()};
        foreach(var tag in extra) tags[tag.Key]=tag.Value;
        return new(id,cardId,EntityKind.Card,tags.ToFrozenDictionary());
    }
    private static GameSnapshot Add(GameSnapshot frame,EntitySnapshot entity)=>Replace(frame,entity);
    private static GameSnapshot Replace(GameSnapshot frame,EntitySnapshot entity)
    { var values=frame.Entities.ToDictionary(); values[entity.Id]=entity; return frame with{Entities=values.ToFrozenDictionary()}; }
    private static GameSnapshot Tag(GameSnapshot frame,int id,string key,string? value)
    { var tags=frame.Entities[id].Tags.ToDictionary(); if(value is null) tags.Remove(key);else tags[key]=value; return Replace(frame,frame.Entities[id] with{Tags=tags.ToFrozenDictionary()}); }
    private static IReadOnlyDictionary<string,CardRecord> With(CardRecord card)
    { var values=FixedCards.Value.ToDictionary(); values[card.Id]=card;return values; }
    private static CardRecord Raw(CardRecord card,Action<JsonObject> change)
    { var raw=JsonNode.Parse(card.RawJson)!.AsObject();change(raw);return card with{RawJson=raw.ToJsonString()}; }
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        var cards=new Dictionary<string,CardRecord>(StringComparer.Ordinal);
        foreach(var file in new[]{"beast-hunter-255790.json","dark-gift-static-255790.json","play-trigger-line-255790.json","quest-progress-255790.json","terror-end-turn-255790.json","lethal-search-255790.json"})
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",file)));
            foreach(var raw in doc.RootElement.GetProperty("cards").EnumerateArray())
            {
                string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";
                string[] List(string key)=>raw.TryGetProperty(key,out var array)?array.EnumerateArray().Select(value=>value.GetString()!).ToArray():[];
                var id=Text("id");
                cards[id]=new(id,raw.GetProperty("dbfId").GetInt32(),Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),
                    raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,raw.TryGetProperty("collectible",out var collectible)&&collectible.GetBoolean(),List("classes"),List("entourage"),raw.GetRawText());
            }
        }
        return cards;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
