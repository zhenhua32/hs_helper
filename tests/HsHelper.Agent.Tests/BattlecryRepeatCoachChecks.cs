using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using HsHelper.Agent;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;

internal static class BattlecryRepeatCoachChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("repeat Battlecry native and assembled complete lines share lazy card loading search and final host", CompleteLines);
        yield return ("repeat Battlecry policies schemas and one-activation previews separate planned counts from completed effects", PolicyAndBoundary);
    }

    private static async Task CompleteLines()
    {
        var catalog = await Catalog();
        foreach (var assembled in new[] { false, true })
        {
            var frame = Frame(assembled ? "EDR_102" : "EDR_852", assembled);
            var observations = new List<CoachToolObservation>(); var coach = new SnapshotCoach(frame, new(), catalog, "255790", toolCompleted: observations.Add);
            var args = Line(assembled); var result = await coach.ExecuteAsync("evaluate_line", args, default);
            var ledger = result["Result"]!["BattlecryExecutions"]!.AsArray().Single()!;
            Check(result["Verification"]!.ToString() == "Verified" && result["IsLethal"]!.GetValue<bool>() &&
                result["AppliedActions"]!.GetValue<int>() == (assembled ? 3 : 4) && result["Result"]!["AvailableMana"]!.GetValue<int>() == 0 &&
                ledger["ActionOrdinal"]!.GetValue<int>() == 1 && ledger["PlannedActivations"]!.GetValue<int>() == 2 &&
                ledger["StartedActivations"]!.GetValue<int>() == 2 && ledger["CompletedActivations"]!.GetValue<int>() == 2 && ledger["IsComplete"]!.GetValue<bool>() &&
                ledger["EnchantmentEntityIds"]!.AsArray().Single()!.GetValue<int>() == 50,
                "A complete repeat line lost its exact source, paid/summoned twice or claimed only one activation.");
            if (assembled)
                Check(result["Result"]!["AssembledEffects"]!.AsArray().Single()!["ExecutedCount"]!.GetValue<int>() == 2 &&
                    !result["Result"]!["DiscoveryBoundaryReached"]!.GetValue<bool>(), "The repeat restored the base Discover script or lost the aggregate hand buff.");
            else
                Check(result["Result"]!["ImbueCountAfter"]!.GetValue<int>() == 4 && result["Result"]!["HeroPowerChanges"]!.AsArray().Count == 2,
                    "Imbue was multiplied as an entire play or still advanced only once.");
            var search = await coach.ExecuteAsync("find_lethal", new() { ["maximum_depth"] = assembled ? 3 : 4, ["maximum_nodes"] = 1000 }, default);
            Check(search["VerifiedLethalFound"]!.GetValue<bool>() && search["Evaluation"]!["Result"]!["BattlecryExecutions"]!.AsArray().Any(
                item => item!["SourceEntityId"]!.GetValue<int>() == 30 && item["CompletedActivations"]!.GetValue<int>() == 2),
                "Search could not enumerate the known repeated enabler used by the manual evaluator.");
            var advice = assembled ? new AdviceAction[] { new("play",30),new("play",31),new("attack",31,11) } :
                [new("play",30),new("hero_power",12),new("play",31),new("attack",31,11)];
            var host = await coach.Capabilities().ValidateActions!(advice, default);
            Check(host.Status == AdviceValidationStatus.Verified && host.IsLegal && host.VerifiedLethal && observations[0].Success,
                "Final host did not reuse the source-bound metadata and atomic-repeat action rules.");
        }
    }

    private static async Task PolicyAndBoundary()
    {
        foreach (var task in new[] { AdviceTaskKind.Turn, AdviceTaskKind.Choice })
        {
            var policy = AdviceTaskTemplates.Get(task);
            Check(policy.Version == "hunter-v25/" + task.ToString().ToLowerInvariant() &&
                new[] { "BattlecryExecutions", "StartedActivations", "CompletedActivations", "IsSingleBattlecryActivationPreview", "双亡语", "不提交第二条play" }
                    .All(text => policy.Instructions.Contains(text, StringComparison.Ordinal)), "The repeat policy lost counts, boundary or single-play semantics.");
        }
        var tool = SnapshotCoach.Definitions.Single(d => d.Name == "evaluate_line");
        foreach (var key in new[] { "repeat_count", "enchantment_entity_ids", "completed_activations", "activation_ordinal", "next_target_entity_id" })
        {
            var args = Line(true); args[key] = 2;
            Check(!tool.ValidateArguments(args), "The model supplied its own trigger evidence: " + key);
            args = Line(true); args["actions"]![0]![key] = 2;
            Check(!tool.ValidateArguments(args), "A forged repeat argument entered a normal action: " + key);
        }
        var catalog = await Catalog(); var frame = Frame("CORE_UNG_912", false);
        var coach = new SnapshotCoach(frame, new(), catalog, "255790");
        var preview = await coach.ExecuteAsync("evaluate_generation", new() { ["source_entity_id"] = 30 }, default);
        Check(preview["BattlecryMultiplicity"]?["Activations"]?.GetValue<int>() == 2 && preview["IsSingleBattlecryActivationPreview"]?.GetValue<bool>() == true,
            "One random-generation preview was represented as the whole double-Battlecry card.");
        var result = await coach.ExecuteAsync("evaluate_line", new() { ["actions"] = new JsonArray(new JsonObject { ["kind"] = "play", ["source_entity_id"] = 30 }) }, default);
        var ledger = result["Result"]!["BattlecryExecutions"]!.AsArray().Single()!;
        Check(result["Verification"]!.ToString() != "Verified" && result["Result"]!["GenerationBoundaryReached"]!.GetValue<bool>() &&
            ledger["PlannedActivations"]!.GetValue<int>() == 2 && ledger["StartedActivations"]!.GetValue<int>() == 1 &&
            ledger["CompletedActivations"]!.GetValue<int>() == 0 && ledger["RemainingActivations"]!.GetValue<int>() == 2,
            "A first unresolved random identity completed both activations or enabled an invented successor.");
    }

    private static JsonObject Line(bool assembled)
    {
        var actions = new JsonArray(new JsonObject { ["kind"]="play", ["source_entity_id"]=30 });
        if (!assembled) actions.Add(new JsonObject { ["kind"]="hero_power", ["source_entity_id"]=12 });
        actions.Add(new JsonObject { ["kind"]="play", ["source_entity_id"]=31 });
        actions.Add(new JsonObject { ["kind"]="attack", ["source_entity_id"]=31, ["target_entity_id"]=11 });
        return new() { ["actions"]=actions };
    }
    private static GameSnapshot Frame(string source, bool assembled)
    {
        var sourceTags = new List<(string,string)> { ("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","HAND"),("COST",assembled?"4":"2"),
            ("ATK",assembled?"5":"2"),("HEALTH",assembled?"4":"3"),("DAMAGE","0") };
        if (assembled) { sourceTags.Add(("CRAFTABLE_FIRST_HALF_DBID","0")); sourceTags.Add(("CRAFTABLE_SECOND_HALF_DBID","121051")); }
        return new("repeat-coach-"+source,10,100,5,1,1,GamePhase.Main,new[]
        {
            new EntitySnapshot(2,null,EntityKind.Player,Tags(("PLAYER_ID","1"),("CONTROLLER","1"),("HERO_ENTITY","10"),("RESOURCES","5"),("RESOURCES_USED","0"),("NUM_CARDS_IN_HAND","2"),("3527","2"))),
            new EntitySnapshot(3,null,EntityKind.Player,Tags(("PLAYER_ID","2"),("CONTROLLER","2"),("HERO_ENTITY","11"))),
            new EntitySnapshot(10,"HERO_05",EntityKind.Card,Tags(("CARDTYPE","HERO"),("CLASS","3"),("CONTROLLER","1"),("ZONE","PLAY"),("HEALTH","30"),("DAMAGE","0"),("ARMOR","0"),("ATK","0"))),
            new EntitySnapshot(11,"HERO_08",EntityKind.Card,Tags(("CARDTYPE","HERO"),("CONTROLLER","2"),("ZONE","PLAY"),("HEALTH",assembled?"3":"5"),("DAMAGE","0"),("ARMOR","0"),("ATK","0"))),
            new EntitySnapshot(12,"EDR_850p",EntityKind.Card,Tags(("CARDTYPE","HERO_POWER"),("CONTROLLER","1"),("ZONE","PLAY"),("COST","2"),("EXHAUSTED","0"),("TAG_SCRIPT_DATA_NUM_1","2"),("TAG_SCRIPT_DATA_NUM_2","2"))),
            new EntitySnapshot(30,source,EntityKind.Card,Tags(sourceTags.ToArray())),
            new EntitySnapshot(31,"CS2_171",EntityKind.Card,Tags(("CARDTYPE","MINION"),("CONTROLLER","1"),("ZONE","HAND"),("COST",assembled?"1":"5"),("ATK","1"),("HEALTH","1"),("DAMAGE","0"),("CHARGE","1"),("CARDRACE","20"))),
            new EntitySnapshot(50,"EDR_100t7e",EntityKind.Card,Tags(("CARDTYPE","ENCHANTMENT"),("CONTROLLER","1"),("ZONE","PLAY"),("ATTACHED","30")))
        }.Where(entity=>!assembled||entity.Id!=12).ToFrozenDictionary(e=>e.Id),new(true,true,true,true,0,[])) { ClientBuild="255790" };
    }
    private static FrozenDictionary<string,string> Tags(params (string Key,string Value)[] values)=>values.ToFrozenDictionary(v=>v.Key,v=>v.Value);
    private static async Task<CardCatalog> Catalog()
    {
        var workspace=new DirectoryInfo(AppContext.BaseDirectory);
        while(workspace is not null&&!File.Exists(Path.Combine(workspace.FullName,"AGENTS.md")))workspace=workspace.Parent;
        var rows=new Dictionary<string,JsonNode>();
        foreach(var name in new[]{"battlecry-repeat-255790.json","assembled-scripts-255790.json","native-definitions-255790.json"})
        {
            var doc=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace!.FullName,"tests","HsHelper.Rules.Tests","Fixtures",name)))!;
            foreach(var row in doc["cards"]!.AsArray())rows[row!["id"]!.ToString()]=row.DeepClone();
        }
        var catalog=new CardCatalog(Path.Combine(workspace!.FullName,".local","repeat-coach",Guid.NewGuid().ToString("N"),"cards.db"));
        await catalog.InitializeAsync(); using var input=new MemoryStream(Encoding.UTF8.GetBytes(new JsonArray(rows.Values.Select(v=>v.DeepClone()).ToArray()).ToJsonString()));
        await catalog.ImportAsync(input,"255790",source:"fixture://repeat-coach");return catalog;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
