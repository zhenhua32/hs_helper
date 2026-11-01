using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class BattlecryMultiplicityChecks
{
    private static readonly Lazy<IReadOnlyDictionary<string, CardRecord>> Fixed = new(Cards);
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Battlecry multiplicity ordinary suppression and a single exact attached gift need no invented count marker", OrdinaryAndGift);
        yield return ("Battlecry multiplicity pins the whole gift definition and refuses record raw disagreement", CompleteDefinition);
        yield return ("Battlecry multiplicity checks current attachment identity topology and every raw alias", AttachmentEvidence);
        yield return ("Battlecry multiplicity never invents formulas for multiple global or unreviewed twist sources", UnknownMultipliers);
        yield return ("Battlecry multiplicity stays entity scoped perspective bound immutable and distinct from Deathrattles", ScopeAndBinding);
    }

    private static void OrdinaryAndGift()
    {
        Check(Evaluate(Frame(false)) is { IsResolved: true, IsEnabled: true, Activations: 1, EnchantmentEntityIds.Count: 0 },
            "An ordinary Battlecry needed an extra once marker.");
        Check(Evaluate(Remove(Frame(false), 30, "CARDTYPE")) is { IsResolved: true, Activations: 1 } &&
            !Evaluate(Remove(Frame(), 30, "CARDTYPE")).IsResolved,
            "Ordinary audited native identity lost its existing missing-type convention, or an actual repeat attachment borrowed it.");
        var result = Evaluate(Frame());
        Check(result is { IsResolved: true, IsEnabled: true, Activations: 2 } && result.EnchantmentEntityIds.SequenceEqual([50]) &&
            BattlecryMultiplicityRules.TryAuditAttachment(Frame(), 50, Fixed.Value, out var target) && target == 30,
            "The actual complete gift did not establish exactly two activations.");
        foreach (var (key, value) in new[] { ("BATTLECRY", "0"), ("218", "0"), ("SILENCED", "1"), ("188", "1") })
            Check(Evaluate(Tag(Frame(), 30, key, value)) is { IsResolved: true, IsEnabled: false, Activations: 0 },
                "Explicit suppression was lost before summon: " + key);
        foreach (var (key, value) in new[] { ("BATTLECRY", "invalid"), ("SILENCED", "2"), ("218", "-1"), ("188", "1.0") })
            Check(!Evaluate(Tag(Frame(), 30, key, value)).IsResolved, "Invalid raw boolean became a native fallback: " + key);
        Check(!Evaluate(Tag(Tag(Frame(), 30, "BATTLECRY", "1"), 30, "218", "0")).IsResolved, "A boolean alias conflict chose the enabling value.");
    }

    private static void CompleteDefinition()
    {
        var original = Fixed.Value[BattlecryMultiplicityRules.EnchantmentCardId];
        Check(BattlecryMultiplicityRules.IsAuditedEnchantment("255790", original), "The exact full source definition was not audited.");
        foreach (var bad in new[] { original with { DbfId = 1 }, original with { Name = "different" }, original with { Text = "twice" },
            original with { CardClass = "SHAMAN" }, original with { Classes = ["HUNTER"] }, original with { Entourage = ["unknown"] },
            original with { RawJson = "{}" }, original with { RawJson = "broken" }, original with { RawJson = new string(' ', 65537) } })
        {
            var cards = Fixed.Value.ToDictionary(); cards[original.Id] = bad;
            Check(!BattlecryMultiplicityRules.IsAuditedEnchantment("255790", bad) && !Evaluate(Frame(), cards).IsResolved,
                "Changed record fields or incomplete raw metadata inherited a repeat effect.");
        }
        foreach (var mutate in new Action<JsonObject>[] { raw => raw["dbfId"] = 999, raw => raw["text"] = "本随从的战吼触发三次。",
            raw => raw["mechanics"] = new JsonArray("DEATHRATTLE"), raw => raw["unreviewedScript"] = 1 })
        {
            var raw = JsonNode.Parse(original.RawJson)!.AsObject(); mutate(raw);
            var bad = Read(JsonSerializer.SerializeToElement(raw)); var cards = Fixed.Value.ToDictionary(); cards[original.Id] = bad;
            Check(!Evaluate(Frame(), cards).IsResolved, "Coherent whole-source mutation was accepted from a familiar ID.");
        }
        Check(!BattlecryMultiplicityRules.IsAuditedEnchantment(null, original) && !BattlecryMultiplicityRules.IsAuditedEnchantment("255791", original) &&
            !BattlecryMultiplicityRules.IsAuditedEnchantment("255790", Fixed.Value["EDR_100t7e1"]), "A build or carrier borrowed t7e's identity.");
    }

    private static void AttachmentEvidence()
    {
        foreach (var (id, key, value) in new[] { (50,"40","31"), (50,"50","2"), (50,"49","3"), (50,"202","4"),
            (50,"ENTITY_ID","51"), (50,"53","51"), (50,"SILENCED","1"), (50,"DORMANT","1"),
            (50,"TAG_SCRIPT_DATA_NUM_6","1"), (50,"IS_NIGHTMARE_BONUS","0"), (50,"TAG_SCRIPT_DATA_NUM_1","2"),
            (50,"DEATHRATTLE","1"), (50,"SPELLPOWER","1"), (30,"50","2"), (30,"49","1"), (30,"202","SPELL") })
        {
            var frame = Tag(Frame(), id, key, value);
            Check(!BattlecryMultiplicityRules.TryAuditAttachment(frame, 50, Fixed.Value, out _) && !Evaluate(frame).IsResolved,
                "Conflicting current attachment/subject evidence became a double Battlecry: " + id + "/" + key);
        }
        foreach (var key in new[] { "ATTACHED", "CONTROLLER", "CARDTYPE", "ZONE" })
        {
            var frame = Remove(Frame(), 50, key);
            Check(!BattlecryMultiplicityRules.TryAuditAttachment(frame, 50, Fixed.Value, out _), "A missing current relation was supplied from the gift ID: " + key);
        }
        var numeric = Frame(); var entity = numeric.Entities[50];
        var tags = entity.Tags.Where(pair => pair.Key is not ("ATTACHED" or "CARDTYPE")).ToDictionary(); tags["40"] = "30"; tags["202"] = "6";
        numeric = Replace(numeric, entity with { Tags = tags.ToFrozenDictionary() });
        Check(Evaluate(numeric) is { IsResolved: true, Activations: 2 }, "Valid numeric relation/type observations were rejected.");
        Check(Evaluate(Tag(Tag(Frame(), 50, "ZONE", "GRAVEYARD"), 50, "49", "4")) is { IsResolved: true, Activations: 1 },
            "A consistently expired attachment still repeated the next Battlecry.");
        Check(!Evaluate(Tag(Tag(Frame(), 50, "ZONE", "GRAVEYARD"), 50, "49", "1")).IsResolved,
            "A named graveyard value hid contradictory active attachment evidence and became an exact ordinary activation.");
    }

    private static void UnknownMultipliers()
    {
        foreach (var key in new[] { "411", "1112", "1126", "EXTRA_BATTLECRIES_BASE", "EXTRA_MINION_BATTLECRIES_BASE", "EXTRA_BATTLECRIES_ADDITIONAL", "UNKNOWN_BATTLECRY_MODIFIER" })
        {
            Check(!Evaluate(Tag(Frame(), 2, key, "1")).IsResolved && !Evaluate(Tag(Frame(false), 2, key, "1")).IsResolved,
                "A nonzero count field became a total or was added to a gift: " + key);
            Check(Evaluate(Tag(Frame(), 2, key, "0")) is { IsResolved: true, Activations: 2 }, "Expired zero modifier blocked a known single gift: " + key);
        }
        var duplicate = Replace(Frame(), Gift(51, 30));
        Check(!Evaluate(duplicate).IsResolved, "Two actual repeat attachments automatically became two or four.");
        var twist = Replace(Frame(), Gift(50, 30) with { CardId = "EDR_100t7e1" });
        Check(!Evaluate(twist).IsResolved, "A similarly named twist inherited the Rude Awakening lifecycle.");
        var sourceAlias = Tag(Tag(Frame(), 30, "EXTRA_MINION_BATTLECRIES_BASE", "0"), 30, "1112", "1");
        Check(!Evaluate(sourceAlias).IsResolved, "An alias conflict hid an extra-minion repeat source.");
    }

    private static void ScopeAndBinding()
    {
        var frame = Replace(Frame(), Body(32, "EDR_852")); var before = JsonSerializer.Serialize(frame);
        Check(BattlecryMultiplicityRules.Evaluate(frame, 32, Fixed.Value) is { IsResolved: true, Activations: 1 }, "Another minion's gift repeated this source.");
        var result = Evaluate(frame);
        Check(result.MatchId == frame.MatchId && result.StateVersion == frame.StateVersion && result.EventSequence == frame.EventSequence && result.CardBuild == "255790" &&
            result.SourceEntityId == 30 && JsonSerializer.Serialize(frame) == before, "Count proof changed input or lost its exact frame binding.");
        try { ((IList<int>)result.EnchantmentEntityIds).Clear(); throw new Exception("Mutable repeat sources."); } catch (NotSupportedException) { }
        foreach (var bad in new[] { frame with { ClientBuild = null }, frame with { ClientBuild = "255791" }, frame with { IsSpectating = true },
            frame with { LocalPlayerId = null }, frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { OpenBlockCount = 1 } } })
            Check(!Evaluate(bad).IsResolved, "Unbound perspective/build/current frame authorized repetition.");
        var dino = Replace(Frame(), Body(30, "DINO_421"));
        Check(Evaluate(dino) is { IsResolved: true, IsEnabled: false, Activations: 0 }, "A double-Battlecry gift granted a Battlecry or double Deathrattle to DINO.");
        var opposing = Tag(Tag(Frame(), 30, "CONTROLLER", "2"), 50, "CONTROLLER", "2");
        Check(!Evaluate(opposing).IsResolved, "A private opposing hand supplied its current attachment or count.");
    }

    private static BattlecryMultiplicityOutcome Evaluate(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord>? cards = null) => BattlecryMultiplicityRules.Evaluate(frame, 30, cards ?? Fixed.Value);
    private static GameSnapshot Frame(bool gift = true) => new("repeat-count", 10, 100, 5, 1, 1, GamePhase.Main,
        new[] { new EntitySnapshot(2, null, EntityKind.Player, Tags(("PLAYER_ID","1"))), new EntitySnapshot(3, null, EntityKind.Player, Tags(("PLAYER_ID","2"))), Body(30,"EDR_852") }
            .Concat(gift ? new[] { Gift(50,30) } : []).ToFrozenDictionary(entity => entity.Id), new(true,true,true,true,0,[])) { ClientBuild="255790" };
    private static EntitySnapshot Body(int id, string card) => new(id, card, EntityKind.Card, Tags(("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","HAND"),("ATK","2"),("HEALTH","3"),("DAMAGE","0"),("COST","2")));
    private static EntitySnapshot Gift(int id, int target) => new(id, "EDR_100t7e", EntityKind.Card, Tags(("CARDTYPE","ENCHANTMENT"),("CONTROLLER","1"),("ZONE","PLAY"),("ATTACHED",target.ToString())));
    private static FrozenDictionary<string,string> Tags(params (string Key,string Value)[] values) => values.ToFrozenDictionary(pair=>pair.Key,pair=>pair.Value);
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity) { var rows=frame.Entities.ToDictionary(); rows[entity.Id]=entity; return frame with { Entities=rows.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame,int id,string key,string value) { var tags=frame.Entities[id].Tags.ToDictionary(); tags[key]=value; return Replace(frame,frame.Entities[id] with { Tags=tags.ToFrozenDictionary() }); }
    private static GameSnapshot Remove(GameSnapshot frame,int id,string key) => Replace(frame,frame.Entities[id] with { Tags=frame.Entities[id].Tags.Where(pair=>pair.Key!=key).ToFrozenDictionary() });
    private static IReadOnlyDictionary<string,CardRecord> Cards() { using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","battlecry-repeat-255790.json"))); return doc.RootElement.GetProperty("cards").EnumerateArray().Select(Read).ToDictionary(c=>c.Id); }
    private static CardRecord Read(JsonElement raw)
    {
        string Text(string key)=>raw.TryGetProperty(key,out var value)?value.GetString()??"":"";
        string[] List(string key)=>raw.TryGetProperty(key,out var value)?value.EnumerateArray().Select(x=>x.GetString()!).ToArray():[];
        return new(Text("id"),raw.GetProperty("dbfId").GetInt32(),Text("name"),Text("text"),Text("cardClass"),Text("set"),Text("type"),raw.TryGetProperty("cost",out var cost)?cost.GetInt32():null,
            raw.TryGetProperty("collectible",out var col)&&col.GetBoolean(),List("classes"),List("entourage"),raw.GetRawText());
    }
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
