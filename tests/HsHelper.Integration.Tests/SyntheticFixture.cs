using System.Text;
using System.Text.Json;
using HsHelper.Cards;

internal static class SyntheticFixture
{
    public const string Build = "900001";
    public const string NewBuild = "900002";
    public const string FutureEnemy = "SYN_FUTURE_ENEMY";
    public const string PrivateProbe = "SYN_PRIVATE_ENEMY";
    public static string CardId(int index) => $"SYN_CARD_{index:D2}";
    public static DeckDefinition Deck(bool changed = false) => new(2, [31], Enumerable.Range(1, 15)
        .Select(i => new DeckCard(1000 + (changed && i == 15 ? 16 : i), 2)).ToArray(), []);

    // These deliberately fictional cards and build labels are never current game data.
    public static string CardsJson(bool changed = false, bool includeFuture = true)
    {
        var cards = new List<object>();
        for (var i = 1; i <= 16; i++)
            cards.Add(new { id = CardId(i), dbfId = 1000 + i, name = $"synthetic {(changed ? "new" : "original")} {i:D2}",
                text = i == 1 ? "冲锋" : "", cardClass = "HUNTER", set = "SYNTHETIC", type = "MINION",
                cost = i == 1 ? 1 : i % 7 + 1, attack = i == 1 ? 3 : 2, health = i == 1 ? 2 : 3,
                collectible = true, rarity = "COMMON", race = "BEAST", mechanics = i == 1 ? new[] { "CHARGE" } : [] });
        cards.Add(new { id = "SYN_HERO_05", dbfId = 31, name = "synthetic hunter", text = "", cardClass = "HUNTER", set = "SYNTHETIC", type = "HERO", cost = 0, collectible = false });
        cards.Add(new { id = "SYN_HERO_08", dbfId = 32, name = "synthetic mage", text = "", cardClass = "MAGE", set = "SYNTHETIC", type = "HERO", cost = 0, collectible = false });
        if (includeFuture)
            cards.Add(new { id = FutureEnemy, dbfId = 2000, name = "synthetic later public enemy", text = "", cardClass = "MAGE", set = "SYNTHETIC", type = "MINION", cost = 1, attack = 2, health = 2, collectible = true });
        return JsonSerializer.Serialize(cards);
    }

    public static string Log(string gameType = "GT_RANKED", bool won = true)
    {
        var log = new StringBuilder("# SYNTHETIC ONLY: generated anonymous acceptance fixture; not a player log.\n");
        var sequence = 0;
        void Add(string content) => log.AppendLine($"D 12:00:00.{sequence++:D7} PowerTaskList.DebugPrintPower() - {content}");
        void Tag(string name, object value) => Add($"    tag={name} value={value}");
        void Change(int entity, string tag, object value) => Add($"TAG_CHANGE Entity={entity} tag={tag} value={value}");
        void End(string checkpoint)
        {
            log.AppendLine($"D 12:00:00.{sequence++:D7} PowerProcessor.EndCurrentTaskList() - task complete");
            log.AppendLine($"# SYNTHETIC_CHECKPOINT {checkpoint}");
        }
        void Entity(int id, string card, int owner, string zone, int type, int position = 0)
        {
            Add($"FULL_ENTITY - Creating ID={id} CardID={card}");
            Tag("CONTROLLER", owner); Tag("ZONE", zone); Tag("CARDTYPE", type); Tag("ZONE_POSITION", position);
        }
        log.AppendLine($"D 12:00:00.0000000 GameState.DebugPrintGame() - BuildNumber={Build} FormatType=FT_STANDARD GameType={gameType}");
        Add("CREATE_GAME"); Add("GameEntity EntityID=1");
        Tag("STATE", "RUNNING"); Tag("TURN", 1); Tag("STEP", "BEGIN_MULLIGAN");
        Add("Player EntityID=2 PlayerID=1");
        Tag("HERO_ENTITY", 10); Tag("RESOURCES", 1); Tag("RESOURCES_USED", 0); Tag("MAXRESOURCES", 10);
        Tag("CURRENT_PLAYER", 1); Tag("FIRST_PLAYER", 1); Tag("PLAYSTATE", "PLAYING");
        Add("Player EntityID=3 PlayerID=2");
        Tag("HERO_ENTITY", 20); Tag("RESOURCES", 0); Tag("RESOURCES_USED", 0); Tag("CURRENT_PLAYER", 0); Tag("FIRST_PLAYER", 0); Tag("PLAYSTATE", "PLAYING");
        Entity(10, "SYN_HERO_05", 1, "PLAY", 3); Tag("CLASS", "HUNTER"); Tag("HEALTH", 30); Tag("DAMAGE", 0); Tag("ATK", 0); Tag("ARMOR", 0);
        Entity(20, "SYN_HERO_08", 2, "PLAY", 3); Tag("CLASS", "MAGE"); Tag("HEALTH", 7); Tag("DAMAGE", 0); Tag("ATK", 0); Tag("ARMOR", 0);
        // Exactly thirty original own entities: three visible opening cards and 27 anonymous deck cards.
        for (var i = 0; i < 30; i++)
        {
            Entity(100 + i, i < 3 ? CardId(i / 2 + 1) : "", 1, i < 3 ? "HAND" : "DECK", 4, i < 3 ? i + 1 : i - 2);
            if (i < 3) { Tag("COST", i < 2 ? 1 : 3); Tag("ATK", i < 2 ? 3 : 2); Tag("HEALTH", i < 2 ? 2 : 3); Tag("DAMAGE", 0); }
        }
        // Opponent goes second: four anonymous opening cards, 26 anonymous deck cards, and the coin.
        for (var i = 0; i < 30; i++) Entity(200 + i, "", 2, i < 4 ? "HAND" : "DECK", 4, i < 4 ? i + 1 : i - 3);
        Entity(230, "GAME_005", 2, "HAND", 5, 5);
        End("opening");
        Change(1, "STEP", "MAIN_ACTION"); End("before-draw");
        Add($"SHOW_ENTITY - Updating Entity=103 CardID={CardId(2)}");
        Tag("COST", 3); Tag("ATK", 2); Tag("HEALTH", 3); Tag("DAMAGE", 0);
        Change(103, "ZONE", "HAND"); Change(103, "ZONE_POSITION", 4); End("drawn");
        // Deliberately inject a nonpublic identity after automatic perspective confirmation.
        // It must remain absent from every player-view snapshot, stored event and model payload.
        Add($"SHOW_ENTITY - Updating Entity=201 CardID={PrivateProbe}"); Tag("COST", 9); End("private-probe");
        Add("BLOCK_START BlockType=PLAY Entity=[id=100] EffectCardId= EffectIndex=0 Target=0 SubOption=-1");
        Change(100, "ZONE", "PLAY"); Change(100, "ZONE_POSITION", 1); Change(100, "NUM_TURNS_IN_PLAY", 0);
        Change(100, "EXHAUSTED", 0); Change(2, "RESOURCES_USED", 1); Add("BLOCK_END"); End("played");
        Add("BLOCK_START BlockType=ATTACK Entity=[id=100] EffectCardId= EffectIndex=0 Target=[id=20] SubOption=-1");
        Change(20, "DAMAGE", 3); Change(100, "NUM_ATTACKS_THIS_TURN", 1); Change(100, "EXHAUSTED", 1);
        Add("BLOCK_END"); End("attacked");
        Change(2, "CURRENT_PLAYER", 0); Change(3, "CURRENT_PLAYER", 1); Change(1, "TURN", 2); End("enemy-turn");
        Change(200, "ZONE", "PLAY"); Change(200, "ZONE_POSITION", 1); End("enemy-unresolved-play");
        Add($"SHOW_ENTITY - Updating Entity=200 CardID={FutureEnemy}");
        Tag("COST", 1); Tag("ATK", 2); Tag("HEALTH", 2); Tag("DAMAGE", 0);
        End("enemy-public");
        Change(2, "PLAYSTATE", won ? "WON" : "LOST"); Change(3, "PLAYSTATE", won ? "LOST" : "WON");
        Change(1, "STATE", "COMPLETE"); End("complete");
        return log.ToString();
    }
}
