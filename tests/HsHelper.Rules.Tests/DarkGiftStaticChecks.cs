using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DarkGiftStaticChecks
{
    private const string Charge = "EDR_100t6e", Life = "EDR_100te";
    private const string ChargeCoverage = "dark-gift-observed-charge:EDR_100t6e", LifeCoverage = "dark-gift-observed-lifesteal:EDR_100te";
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> FixedCards = new(ReadCards);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("dark gift fixtures preserve exact public card and CardDefs provenance", Provenance);
        yield return ("observed Charge gift permits native play and lethal without changing current stats or costs", ChargePlay);
        yield return ("observed Lifesteal gift heals actual combat damage without reapplying its attack bonus", LifestealPlay);
        yield return ("observed Lifesteal gift preserves armor shield immunity poison and healing cap rules", DamageControls);
        yield return ("matching gift numeric aliases corroborate current flags without introducing grants", CoherentAliases);
        yield return ("gift source identities attachment owners and current types cannot be forged", SourceConflicts);
        yield return ("gift fixed record raw mechanics and text changes cannot borrow an exact ID", GiftMetadataConflicts);
        yield return ("native target record and raw metadata changes cannot unlock ID-selected effects", TargetMetadataConflicts);
        yield return ("missing malformed and contradictory target observations remain unverified", TargetObservationConflicts);
        yield return ("audited duplicate static gifts share current values while ongoing gifts remain bounded", SuppressionAndUnknowns);
        yield return ("static gift coverage cannot cross Raptor observation or other interaction boundaries", OtherBoundaries);
        yield return ("all current audited hunter bodies require fixed data and JSON order is immaterial", NativeTargetsAndCanonicalData);
        yield return ("ordinary simple-profile minions retain their existing rule path with coherent target metadata", SimpleTarget);
    }

    private static void Provenance()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dark-gift-static-255790.json")));
        var root = fixture.RootElement;
        Check(root.GetProperty("build").GetString() == "255790" && root.GetProperty("sourceSha256").GetString() == "7C9D0321B22BEF8D9C4E67397C7B8110AAC149164A95DD554118F9B6DC638299" &&
            root.GetProperty("cardDefsSha256").GetString() == "EE72B26D5B90B595EE5DE63E05979BF80195896BA32CF943C6F5FFC8BFBB72A8", "Fixed public source hashes changed.");
        foreach (var (id, dbf, button, text) in new[] { (Charge, 113809, 113802, "<b>冲锋</b>"), (Life, 113803, 113796, "+3攻击力和<b>吸血</b>。") })
        {
            var card = FixedCards.Value[id]; var evidence = root.GetProperty("cardDefsEvidence").EnumerateArray().Single(row => row.GetProperty("CardId").GetString() == id);
            Check(card.DbfId == dbf && card.Type == "ENCHANTMENT" && card.Text == text && evidence.GetProperty("Dbf").GetString() == dbf.ToString(), "Gift identity or full text lost its original record.");
            var tags = evidence.GetProperty("Tags").EnumerateArray().ToArray();
            Check(tags.Any(tag => tag.GetProperty("EnumId").GetString() == "2921" && tag.GetProperty("Value").GetString() == button.ToString()) &&
                tags.Any(tag => tag.GetProperty("EnumId").GetString() == "3567" && tag.GetProperty("Value").GetString() == "1"), "CardDefs no longer ties the exact enchantment to its button.");
        }
        Check(FixedCards.Value["EDR_100t8e"].DbfId == 113885 && FixedCards.Value["EDR_100t8e"].Text == "+3/+3并打乱属性值。" &&
            FixedCards.Value["EDR_100t8e1"].DbfId == 115355 && FixedCards.Value["EDR_100t8e1"].Text == "+4/+5。", "Nearby IDs were conflated.");
    }
    private static void ChargePlay()
    {
        var frame = Frame(); var before = JsonSerializer.Serialize(frame); var result = Eval(frame); var control = Eval(Remove(frame, 50));
        Check(result.Verification == LineVerification.Verified && result.IsLethal && result.AppliedActions == 2 && result.RuleCoverage.Contains(ChargeCoverage), "Observed Charge remained blocked: " + JsonSerializer.Serialize(result));
        SameCombat(result, control); Check(result.Result.AvailableMana == 4 && Find(result, 30).Attack == 5 && before == JsonSerializer.Serialize(frame), "Gift was reapplied or the input mutated.");
    }
    private static void LifestealPlay()
    {
        var frame = Frame(Life); var result = Eval(frame); var control = Eval(Remove(frame, 50));
        Check(result.Verification == LineVerification.Verified && result.RuleCoverage.Contains(LifeCoverage) && result.AppliedActions == 2 &&
            Find(result, 11).RemainingHealth == 28 && Find(result, 30).Attack == 8 && Find(result, 40).RemainingHealth == 12 && result.Result.AvailableMana == 4,
            "Lifesteal used something other than the current eight attack/one cost: " + JsonSerializer.Serialize(result));
        SameCombat(result, control);
    }
    private static void DamageControls()
    {
        foreach (var (tag, value, expectedHero, expectedTarget, alive) in new[]
        {
            ("ARMOR", "3", 28, 15, true), ("DIVINE_SHIELD", "1", 20, 20, true),
            ("IMMUNE", "1", 20, 20, true), ("POISONOUS", "1", 28, 0, false)
        })
        {
            var frame = Tag(Frame(Life), tag == "POISONOUS" ? 30 : 40, tag, value);
            var result = Eval(frame); SameCombat(result, Eval(Remove(frame, 50)));
            Check(Find(result, 11).RemainingHealth == expectedHero && Find(result, 40).RemainingHealth == expectedTarget && Find(result, 40).IsAlive == alive && !result.IsLethal,
                "Gift changed a pre-existing damage defense or poison rule.");
            if (tag == "IMMUNE") Check(result.Verification == LineVerification.Invalid && result.AppliedActions == 1, "Gift made an immune target attack legal.");
        }
        var capped = Tag(Frame(Life), 11, "DAMAGE", "1");
        var heal = Eval(capped); SameCombat(heal, Eval(Remove(capped, 50)));
        Check(Find(heal, 11).RemainingHealth == 30 && Find(heal, 11).Armor == 0, "Healing exceeded maximum health or created armor.");
    }
    private static void CoherentAliases()
    {
        foreach (var gift in new[] { Charge, Life })
        {
            var frame = Frame(gift);
            foreach (var (key, value) in new[] { ("40", "30"), ("49", "1"), ("50", "1"), ("53", "50"), ("202", "6"),
                ("2921", gift == Charge ? "113802" : "113796"), ("3567", "1") }) frame = Tag(frame, 50, key, value);
            foreach (var (key, value) in new[] { ("47", gift == Charge ? "5" : "8"), ("45", "2"), ("44", "0"), ("48", "1"), ("49", "3"), ("50", "1"), ("202", "4"), (gift == Charge ? "197" : "685", "1") })
                frame = Tag(frame, 30, key, value);
            var result = Eval(frame);
            Check(result.Verification == LineVerification.Verified && result.RuleCoverage.Contains(gift == Charge ? ChargeCoverage : LifeCoverage), "Matching original aliases remained unexplained.");
        }
    }
    private static void SourceConflicts()
    {
        foreach (var (key, value) in new (string, string?)[]
        {
            ("ATTACHED", null), ("ATTACHED", "999"), ("ATTACHED", "0"), ("ATTACHED", "bad"), ("40", "999"),
            ("CONTROLLER", "2"), ("50", "2"), ("CARDTYPE", "SPELL"), ("202", "5"), ("ZONE", "SETASIDE"), ("49", "3"),
            ("ENTITY_ID", "51"), ("2921", "113796"), ("3567", "2"), ("SILENCED", "1"), ("DORMANT", "1"), ("DEATHRATTLE", "1")
        }) NotCovered(Tag(Frame(), 50, key, value));
        NotCovered(Replace(Frame(), Frame().Entities[50] with { Kind = EntityKind.Unknown }));
        NotCovered(Frame() with { ClientBuild = "255791" });
    }
    private static void GiftMetadataConflicts()
    {
        foreach (var gift in new[] { Charge, Life })
        {
            var original = FixedCards.Value[gift];
            foreach (var bad in new[] { original with { DbfId = original.DbfId + 1 }, original with { Type = "SPELL" }, original with { Text = "新增未知触发" },
                original with { RawJson = "{}" }, original with { RawJson = "broken" },
                Raw(original, raw => raw["mechanics"] = new JsonArray("TRIGGER_VISUAL")), Raw(original, raw => raw["text"] = "未知") })
                NotCovered(Frame(gift), CardsWith(bad));
        }
    }
    private static void TargetMetadataConflicts()
    {
        var original = FixedCards.Value["EDR_227"];
        foreach (var bad in new[] { original with { DbfId = original.DbfId + 1 }, original with { Text = "未知使用触发" }, original with { Type = "SPELL" },
            Raw(original, raw => raw["dbfId"] = 777777), Raw(original, raw => raw["mechanics"] = new JsonArray("TRIGGER_VISUAL")),
            Raw(original, raw => raw["text"] = "" ) with { Text = "" } })
            NotCovered(Frame(), CardsWith(bad));
    }
    private static void TargetObservationConflicts()
    {
        foreach (var (key, value) in new (string, string?)[]
        {
            ("CHARGE", null), ("CHARGE", "0"), ("CHARGE", "2"), ("197", "0"), ("ATK", null), ("ATK", "bad"), ("47", "9"),
            ("HEALTH", null), ("HEALTH", "0"), ("45", "9"), ("DAMAGE", null), ("DAMAGE", "3"), ("44", "1"),
            ("COST", null), ("48", "9"), ("CARDTYPE", "SPELL"), ("202", "5"), ("CONTROLLER", "2"), ("50", "2"), ("49", "1"),
            ("SILENCED", "1"), ("188", "1"), ("DORMANT", "1"), ("FROZEN", "1"), ("260", "1"), ("FROZEN", "bad"),
            ("ARMOR", "bad"), ("ARMOR", "-1"), ("292", "1"), ("CRAFTABLE_FIRST_HALF_DBID", "1")
        }) NotCovered(Tag(Frame(), 30, key, value));
        foreach (var (key, value) in new (string, string?)[] { ("LIFESTEAL", null), ("LIFESTEAL", "0"), ("685", "0"), ("ATK", null), ("DAMAGE", null) })
            NotCovered(Tag(Frame(Life), 30, key, value));
        NotCovered(Tag(Frame(Life), 11, "DAMAGE", null));
    }
    private static void SuppressionAndUnknowns()
    {
        var duplicate = Replace(Frame(), Frame().Entities[50] with { Id = 51 });
        var repeated = Eval(duplicate);
        Check(repeated.Verification == LineVerification.Verified && repeated.IsLethal && Find(repeated, 30).Attack == 5 && repeated.Result.AvailableMana == 4,
            "Identical audited static sources were rejected or reapplied their effect.");
        foreach (var id in new[] { "EDR_100t4e", "EDR_100t5e", "EDR_100t7e", "EDR_100t7e1", "EDR_100t9e" })
        {
            var frame = Replace(Frame(), Frame().Entities[50] with { CardId = id });
            var result = Eval(frame);
            Check(result.Verification != LineVerification.Verified && !result.IsLethal && !result.RuleCoverage.Any(IsGiftCoverage), "A neighboring or scripted gift was treated as a static supported one.");
        }
        var mixed = Replace(Frame(), Frame().Entities[50] with { Id = 51, CardId = "EDR_100t5e" });
        var uncertain = Eval(mixed);
        Check(uncertain.Verification != LineVerification.Verified && !uncertain.IsLethal && uncertain.UnsupportedMechanics.Contains("UNMAPPED_ATTACHED_ENCHANTMENT"), "Known static gift erased another unknown enchantment.");
    }
    private static void OtherBoundaries()
    {
        var raptor = Entity(60, "EDR_849", "MINION", "PLAY", 1, ("ATK", "3"), ("HEALTH", "3"), ("DAMAGE", "0"), ("SILENCED", "0"), ("DORMANT", "0"));
        var result = Eval(Replace(Frame(), raptor));
        Check(result.AppliedActions == 1 && result.Result.RaptorBoundaryReached && !result.IsLethal && result.Verification != LineVerification.Verified &&
            result.UnsupportedMechanics.Contains("AWAIT_RAPTOR_SNAPSHOT"), "Static gift bypassed actual Raptor after-play observation.");
        var secret = Entity(70, null, "SPELL", "SECRET", 2, ("SECRET", "1"));
        var conditional = Eval(Replace(Frame(), secret));
        Check(conditional.Verification != LineVerification.Verified && !conditional.IsLethal && conditional.UnsupportedMechanics.Contains("OPPONENT_SECRET"), "Static gift erased an unknown opponent interaction.");
    }
    private static void NativeTargetsAndCanonicalData()
    {
        foreach (var id in new[] { "EDR_849", "CORE_UNG_912", "EDR_852", "EDR_800", "EDR_227", "EDR_226", "FIR_921", "EDR_480", "DINO_421", "FIR_953", "EDR_489",
            "BE_044", "JAIL_831", "EDR_888", "CORE_EDR_004_2026", "CORE_EDR_004", "CORE_CS2_189", "CS2_189", "CORE_TTN_866" })
        {
            var frame = Tag(Frame(), 30, "ZONE", "PLAY"); frame = Tag(frame, 30, "NUM_TURNS_IN_PLAY", "1"); frame = Tag(frame, 30, "EXHAUSTED", "0");
            frame = Replace(frame, frame.Entities[30] with { CardId = id });
            var result = Eval(frame, attackOnly: true);
            Check(result.RuleCoverage.Contains(ChargeCoverage), "An existing native body is missing fixed metadata audit: " + id);
            NotCovered(frame, CardsWith(FixedCards.Value[id] with { DbfId = 999999 }), attackOnly: true);
        }
        var cards = FixedCards.Value.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var id in new[] { Charge, "EDR_227" })
        {
            using var raw = JsonDocument.Parse(cards[id].RawJson);
            cards[id] = cards[id] with { RawJson = JsonSerializer.Serialize(raw.RootElement.EnumerateObject().Reverse().ToDictionary(property => property.Name, property => property.Value)) };
        }
        Check(Eval(Frame(), cards).Verification == LineVerification.Verified, "Reordering identical JSON changed its semantic fingerprint.");
    }
    private static void SimpleTarget()
    {
        var json = """{"id":"SIMPLE_GIFT_TARGET","dbfId":799999,"name":"Synthetic vanilla","text":"","type":"MINION","cardClass":"NEUTRAL","set":"SYNTHETIC","cost":1,"attack":5,"health":2} """;
        var card = new CardRecord("SIMPLE_GIFT_TARGET", 799999, "Synthetic vanilla", "", "NEUTRAL", "SYNTHETIC", "MINION", 1, false, [], [], json);
        var frame = Replace(Frame(), Frame().Entities[30] with { CardId = card.Id });
        var result = Eval(frame, CardsWith(card));
        Check(result.Verification == LineVerification.Verified && result.IsLethal && result.RuleCoverage.Contains(ChargeCoverage), "A coherent ordinary vanilla target lost its existing generic rule path.");
    }

    private static LineEvaluation Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord>? cards = null, bool attackOnly = false) =>
        new LineEvaluator().EvaluateLine(frame, attackOnly ? [new(ActionKind.Attack, 30, frame.Entities.ContainsKey(40) ? 40 : 21)] :
            [new(ActionKind.PlayCard, 30), new(ActionKind.Attack, 30, frame.Entities.ContainsKey(40) ? 40 : 21)], cards ?? FixedCards.Value);
    private static void NotCovered(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord>? cards = null, bool attackOnly = false)
    {
        var result = Eval(frame, cards, attackOnly);
        Check(result.Verification != LineVerification.Verified && !result.IsLethal && !result.RuleCoverage.Any(IsGiftCoverage), "Conflicting gift evidence became verified or gained coverage: " + JsonSerializer.Serialize(result));
    }
    private static bool IsGiftCoverage(string value) => value is ChargeCoverage or LifeCoverage;
    private static SimulatedEntity Find(LineEvaluation line, int id) => line.Result.Entities.Single(entity => entity.EntityId == id);
    private static void SameCombat(LineEvaluation actual, LineEvaluation control)
    {
        Check(actual.AppliedActions == control.AppliedActions && actual.Result.AvailableMana == control.Result.AvailableMana &&
            actual.Result.Entities.Where(entity => entity.EntityId != 50).Select(entity => (entity.EntityId, entity.Attack, entity.RemainingHealth, entity.Armor, entity.Cost, entity.Zone, entity.IsAlive))
                .SequenceEqual(control.Result.Entities.Select(entity => (entity.EntityId, entity.Attack, entity.RemainingHealth, entity.Armor, entity.Cost, entity.Zone, entity.IsAlive))),
            "Observed static coverage changed the generic combat projection.");
    }
    private static GameSnapshot Frame(string gift = Charge)
    {
        var life = gift == Life;
        var entities = new List<EntitySnapshot>
        {
            new(2, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"]="1", ["HERO_ENTITY"]="11", ["RESOURCES"]="5", ["RESOURCES_USED"]="0", ["TEMP_RESOURCES"]="0" }.ToFrozenDictionary()),
            new(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"]="2", ["HERO_ENTITY"]="21" }.ToFrozenDictionary()),
            Entity(11,"HERO_05","HERO","PLAY",1,("CLASS","HUNTER"),("ATK","0"),("HEALTH","30"),("DAMAGE","10"),("ARMOR","0")),
            Entity(21,"HERO_08","HERO","PLAY",2,("CLASS","MAGE"),("ATK","0"),("HEALTH",life?"30":"5"),("DAMAGE","0"),("ARMOR","0")),
            Entity(30,"EDR_227","MINION","HAND",1,("ATK",life?"8":"5"),("HEALTH","2"),("DAMAGE","0"),("COST","1"),("RUSH","1"),("DEATHRATTLE","1"),(life?"LIFESTEAL":"CHARGE","1")),
            Entity(50,gift,"ENCHANTMENT","PLAY",1,("ATTACHED","30"))
        };
        if (life) entities.Add(Entity(40,"CORE_CS2_189","MINION","PLAY",2,("ATK","0"),("HEALTH","20"),("DAMAGE","0"),("ARMOR","0")));
        return new("dark-gift-static", 7, 40, 3, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true,true,true,true,0,[])) { ClientBuild="255790" };
    }
    private static EntitySnapshot Entity(int id, string? card, string type, string zone, int owner, params (string Key,string Value)[] extra)
    {
        var tags = new Dictionary<string,string> { ["CARDTYPE"]=type, ["ZONE"]=zone, ["CONTROLLER"]=owner.ToString() };
        foreach(var pair in extra) tags[pair.Key]=pair.Value;
        return new(id,card,EntityKind.Card,tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities=frame.Entities.ToDictionary(pair=>pair.Key,pair=>pair.Value); entities[entity.Id]=entity; return frame with { Entities=entities.ToFrozenDictionary() }; }
    private static GameSnapshot Remove(GameSnapshot frame, int id) => frame with { Entities=frame.Entities.Where(pair=>pair.Key!=id).ToFrozenDictionary() };
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string? value)
    { var tags=frame.Entities[id].Tags.ToDictionary(pair=>pair.Key,pair=>pair.Value); if(value is null)tags.Remove(key);else tags[key]=value;return Replace(frame,frame.Entities[id] with { Tags=tags.ToFrozenDictionary() }); }
    private static IReadOnlyDictionary<string,CardRecord> CardsWith(CardRecord card)
    { var cards=FixedCards.Value.ToDictionary(pair=>pair.Key,pair=>pair.Value);cards[card.Id]=card;return cards; }
    private static CardRecord Raw(CardRecord card, Action<JsonObject> change)
    { var raw=JsonNode.Parse(card.RawJson)!.AsObject();change(raw);return card with { RawJson=raw.ToJsonString() }; }
    private static IReadOnlyDictionary<string,CardRecord> ReadCards()
    {
        var result=new Dictionary<string,CardRecord>(StringComparer.Ordinal);
        foreach(var file in new[]{"beast-hunter-255790.json","discovery-play-255790.json","rewind-choice-255790.json","terror-end-turn-255790.json","dark-gift-static-255790.json"})
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",file)));
            foreach(var raw in doc.RootElement.GetProperty("cards").EnumerateArray())
            {
                string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()!:"";
                var id=Text("id");
                result[id]=new(id,raw.GetProperty("dbfId").GetInt32(),Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),
                    raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,raw.TryGetProperty("collectible",out var collectible)&&collectible.GetBoolean(),[],[],raw.GetRawText());
            }
        }
        return result;
    }
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
