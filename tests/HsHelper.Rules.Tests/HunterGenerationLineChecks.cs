using System.Collections.Frozen;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class HunterGenerationLineChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases(IReadOnlyDictionary<string, CardRecord> cards)
    {
        LineEvaluation Evaluate(GameSnapshot frame, params PlannedAction[] actions) =>
            new LineEvaluator().EvaluateLine(frame, actions.Length == 0 ? [new(ActionKind.PlayCard, 30)] : actions, cards);
        yield return ("Macaw enters and previews one generated Beast without creating an entity or touching the deck", () =>
        {
            var frame = Frame(); var before = System.Text.Json.JsonSerializer.Serialize(frame); var result = Evaluate(frame);
            var generation = result.Result.GenerationPreviews.Single();
            Check(result.AppliedActions == 1 && result.Verification == LineVerification.Conditional && result.Result.AvailableMana == 9 &&
                result.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "PLAY" && result.Result.GenerationBoundaryReached &&
                generation.RequestedCards is { Minimum: 1, Maximum: 1 } && generation.HandCountAfter is { Minimum: 1, Maximum: 1 } &&
                generation.CardsAddedToHand is { Minimum: 1, Maximum: 1 } && result.Result.Entities.All(entity => entity.Zone != "HAND") &&
                result.Result.Entities.Single(entity => entity.EntityId == 40).Zone == "DECK" && !result.IsLethal &&
                before == System.Text.Json.JsonSerializer.Serialize(frame), "Generated count became a card entity, removed a deck card, or mutated the input.");
        });
        yield return ("a generation boundary refuses a following action at the original applied prefix", () =>
        {
            var result = Evaluate(Frame(), new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Unsupported && result.AppliedActions == 1 &&
                result.UnsupportedMechanics.Contains("AWAIT_GENERATION_SNAPSHOT") && !result.Result.TurnEnded,
                "A generated card or unresolved trigger was skipped to validate a later action.");
        });
        yield return ("generation overlays use the simulated hand count after earlier cards have left", () =>
        {
            var frame = Add(Frame(maximum: 2), Card(31, "EDR_852", "HAND", 1));
            frame = Player(frame, ("NUM_CARDS_IN_HAND", "2"));
            var result = Evaluate(frame, new(ActionKind.PlayCard, 31), new(ActionKind.PlayCard, 30));
            Check(result.AppliedActions == 2 && result.Result.GenerationPreviews.Single().HandCountAfter is { Minimum: 1, Maximum: 1 },
                "Generation reused the original hand size or deducted the source twice.");
        });
        yield return ("a silenced Macaw has no native generation and can reach the following end turn", () =>
        {
            var frame = Add(Frame(), Card(30, "CORE_UNG_912", "HAND", 1, ("SILENCED", "1")));
            var result = Evaluate(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.Verification == LineVerification.Verified && result.AppliedActions == 2 && result.Result.TurnEnded &&
                !result.Result.GenerationBoundaryReached && result.Result.GenerationPreviews.Single().RequestedCards.Maximum == 0,
                "Silence retained the native generated card or introduced a pending generation.");
        });
        yield return ("generation overlay preserves conflicting observed hand counts and hand-limit aliases", () =>
        {
            var result = Evaluate(Player(Frame(), ("NUM_CARDS_IN_HAND", "9"), ("28", "8")));
            var generation = result.Result.GenerationPreviews.Single();
            Check(generation.HandCountAfter is null && !generation.CardsAddedToHand.IsExact &&
                generation.CardsAddedToHand.Minimum == 0 && generation.CardsAddedToHand.Maximum == 1,
                "Conflicting observed counts were replaced with exact runtime defaults.");
        });
        yield return ("a suppressed runtime battlecry never discards a conflicting raw active alias", () =>
        {
            var frame = Add(Frame(), Card(30, "CORE_UNG_912", "HAND", 1, ("BATTLECRY", "0"), ("218", "1")));
            var result = Evaluate(frame, new(ActionKind.PlayCard, 30), new(ActionKind.EndTurn, 2));
            Check(result.AppliedActions == 1 && result.Verification == LineVerification.Unsupported && result.Result.GenerationBoundaryReached &&
                result.Result.GenerationPreviews.Single().RequestedCards is { Minimum: 0, Maximum: 1 },
                "A single zero runtime flag erased the contradictory raw generation branch.");
        });
        yield return ("a full generation destination records the loss without a usable hand card", () =>
        {
            var generation = Evaluate(Frame(maximum: 0)).Result.GenerationPreviews.Single();
            Check(generation.CardsAddedToHand is { Minimum: 0, Maximum: 0 } &&
                generation.CardsLostToHandLimit is { Minimum: 1, Maximum: 1 } && generation.HandCountAfter is { Minimum: 0, Maximum: 0 },
                "A generated card entered a full hand or changed the hand count.");
        });
        yield return ("Macaw still validates mana targets board size and custom text before payment", () =>
        {
            var noMana = Evaluate(Player(Frame(), ("RESOURCES", "0")));
            var target = Evaluate(Frame(), new PlannedAction(ActionKind.PlayCard, 30, 11));
            var custom = Evaluate(Add(Frame(), Card(30, "CORE_UNG_912", "HAND", 1, ("CRAFTABLE_FIRST_HALF_DBID", "1"))));
            var full = Frame();
            for (var i = 0; i < 7; i++) full = Add(full, Card(50 + i, "EDR_852", "PLAY", 1));
            var boardFull = Evaluate(full);
            Check(new[] { noMana, target, boardFull }.All(result => result.Verification == LineVerification.Invalid && result.AppliedActions == 0 && result.Result.GenerationPreviews.Count == 0) &&
                custom.Verification == LineVerification.Unsupported && custom.AppliedActions == 0 && custom.Result.AvailableMana == 10,
                "Generation bypassed a play restriction or executed an assembled base battlecry.");
        });
        yield return ("lethal opponent-health payment stops Macaw before its native generation", () =>
        {
            var frame = Add(Frame(), Card(31, "EDR_489", "HAND", 8));
            frame = Player(frame, ("NUM_CARDS_IN_HAND", "2"));
            var tags = frame.Entities[11].Tags.ToDictionary(); tags["HEALTH"] = "1";
            frame = Add(frame, frame.Entities[11] with { Tags = tags.ToFrozenDictionary() });
            var result = Evaluate(frame, new(ActionKind.PlayCard, 31), new(ActionKind.PlayCard, 30));
            Check(result.Result.OpponentDefeatedByPayment && result.Result.GenerationPreviews.Count == 0 &&
                !result.Result.GenerationBoundaryReached && result.Result.Entities.Single(entity => entity.EntityId == 30).Zone == "HAND",
                "Generation executed after a lethal payment ended further source effects.");
        });
    }

    internal static GameSnapshot Frame(int maximum = 10)
    {
        var entities = new[]
        {
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string,string>
            { ["PLAYER_ID"]="1", ["RESOURCES"]="10", ["RESOURCES_USED"]="0", ["HERO_ENTITY"]="10", ["MAXHANDSIZE"]=maximum.ToString(), ["NUM_CARDS_IN_HAND"]="1" }.ToFrozenDictionary()),
            new EntitySnapshot(3, null, EntityKind.Player, new Dictionary<string,string> { ["PLAYER_ID"]="2", ["HERO_ENTITY"]="11" }.ToFrozenDictionary()),
            Hero(10,1), Hero(11,2), Card(30,"CORE_UNG_912","HAND",1), Card(40,null,"DECK",0)
        };
        return new("native-generation-line", 15, 150, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity=>entity.Id), new(true,true,true,true,0,[])) { ClientBuild="255790" };
    }
    internal static EntitySnapshot Card(int id, string? card, string zone, int cost, params (string Key,string Value)[] more)
    {
        var tags = new Dictionary<string,string> { ["CONTROLLER"]="1",["ZONE"]=zone,["CARDTYPE"]="MINION",["COST"]=cost.ToString() };
        foreach(var (key,value) in more) tags[key]=value;
        return new(id,card,EntityKind.Card,tags.ToFrozenDictionary());
    }
    private static EntitySnapshot Hero(int id,int owner) => new(id,"HERO_"+owner,EntityKind.Card,new Dictionary<string,string>
    { ["CONTROLLER"]=owner.ToString(),["ZONE"]="PLAY",["CARDTYPE"]="HERO",["CLASS"]=owner==1?"HUNTER":"MAGE",["HEALTH"]="30",["DAMAGE"]="0",["ARMOR"]="0" }.ToFrozenDictionary());
    internal static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity)
    { var entities=frame.Entities.ToDictionary(); entities[entity.Id]=entity; return frame with { Entities=entities.ToFrozenDictionary() }; }
    internal static GameSnapshot Player(GameSnapshot frame,params (string Key,string Value)[] more)
    { var tags=frame.Entities[2].Tags.ToDictionary(); foreach(var(key,value) in more) tags[key]=value; return Add(frame,frame.Entities[2] with { Tags=tags.ToFrozenDictionary() }); }
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}
