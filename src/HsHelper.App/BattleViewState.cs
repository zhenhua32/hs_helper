using System.Collections.ObjectModel;
using HsHelper.Cards;
using HsHelper.Core;

namespace HsHelper.App;

/// <summary>Only observed, perspective-safe attributes; null means the current value is unknown.</summary>
public sealed record BattleCardView(int EntityId, string? CardId, string Name, string Type,
    int? Cost, int? Attack, int? Health, int? Armor, int? Durability, bool? IsExhausted,
    string? Zone, int? Position, string Status);
public sealed record BattlePlayedCardView(long Sequence, int EntityId, string? CardId, string Name);
public sealed record BattleDeckCardView(string CardId, string Name, int Copies);
public sealed record BattleChoiceOptionView(int EntityId, string? CardId, string Name, int? Position);

public sealed record BattleChoiceView
{
    public int Id { get; }
    public int CountMin { get; }
    public int CountMax { get; }
    public IReadOnlyList<BattleChoiceOptionView> Options { get; }

    internal BattleChoiceView(int id, int countMin, int countMax, IEnumerable<BattleChoiceOptionView> options)
    { Id = id; CountMin = countMin; CountMax = countMax; Options = Array.AsReadOnly(options.ToArray()); }
}

public sealed record BattlePlayerView
{
    public int? PlayerId { get; }
    public string Label { get; }
    public BattleCardView? Hero { get; }
    public BattleCardView? Weapon { get; }
    public BattleCardView? HeroPower { get; }
    public int? ManaAvailable { get; }
    public int? ManaTotal { get; }
    public int? ManaSpent { get; }
    public int? ManaLocked { get; }
    public int? TemporaryMana { get; }
    public int? HandCount { get; }
    public int? DeckCount { get; }
    public IReadOnlyList<BattleCardView> Hand { get; }
    public IReadOnlyList<BattleCardView> Board { get; }
    public IReadOnlyList<BattlePlayedCardView> PublicPlays { get; }

    internal BattlePlayerView(int? playerId, string label, BattleCardView? hero, BattleCardView? weapon,
        BattleCardView? heroPower, int? manaAvailable, int? manaTotal, int? manaSpent, int? manaLocked,
        int? temporaryMana, int? handCount, int? deckCount, IEnumerable<BattleCardView> hand,
        IEnumerable<BattleCardView> board, IEnumerable<BattlePlayedCardView> publicPlays)
    {
        PlayerId = playerId; Label = label; Hero = hero; Weapon = weapon; HeroPower = heroPower;
        ManaAvailable = manaAvailable; ManaTotal = manaTotal; ManaSpent = manaSpent;
        ManaLocked = manaLocked; TemporaryMana = temporaryMana; HandCount = handCount; DeckCount = deckCount;
        Hand = Array.AsReadOnly(hand.ToArray()); Board = Array.AsReadOnly(board.ToArray());
        PublicPlays = Array.AsReadOnly(publicPlays.ToArray());
    }
}

public sealed record BattleViewState
{
    public string MatchId { get; }
    public long StateVersion { get; }
    public long EventSequence { get; }
    public int? Turn { get; }
    public GamePhase Phase { get; }
    public int? ActivePlayerId { get; }
    public bool HasLocalPerspective { get; }
    public bool IsComplete { get; }
    public bool IsStable { get; }
    public BattlePlayerView Local { get; }
    public BattlePlayerView Opponent { get; }
    public IReadOnlyList<BattleDeckCardView> RemainingDeck { get; }
    public bool HasDeckTracking { get; }
    public bool DeckIsExact { get; }
    public int? UnknownDeckCards { get; }
    public string DeckSummary => !HasDeckTracking ? "尚无匹配的牌库台账；剩余组成与未知数量尚未确认。" :
        DeckIsExact ? "已确认完整的剩余牌库组成；计算时仍需核对当前阶段。" :
        !IsStable ? "结算尚未稳定；以下为已知剩余记录，不能给出精确概率。" :
        "剩余组成不完整；以下为已知剩余下限，不能给出精确概率。";
    public BattleChoiceView? Choice { get; }
    public string ChoiceStatus { get; }
    public string QualitySummary => $"我方视角：{(HasLocalPerspective ? "已确认" : "尚未确认")} · 日志：{(IsComplete ? "完整" : "存在缺口")} · 局面：{(IsStable ? "稳定" : "结算中或尚未确认")}";
    public bool PublicPlaysComplete { get; }
    public IReadOnlyList<string> Warnings { get; }

    internal BattleViewState(GameSnapshot snapshot, bool hasPerspective, BattlePlayerView local,
        BattlePlayerView opponent, IEnumerable<BattleDeckCardView> remaining, bool hasDeckTracking, bool deckExact,
        int? unknownDeckCards, BattleChoiceView? choice, string choiceStatus, bool publicPlaysComplete, IEnumerable<string> warnings)
    {
        MatchId = snapshot.MatchId; StateVersion = snapshot.StateVersion; EventSequence = snapshot.EventSequence;
        Turn = snapshot.Turn; Phase = snapshot.Phase; ActivePlayerId = snapshot.ActivePlayerId;
        HasLocalPerspective = hasPerspective; IsComplete = snapshot.Quality.IsComplete; IsStable = snapshot.Quality.IsStable;
        Local = local; Opponent = opponent; RemainingDeck = Array.AsReadOnly(remaining.ToArray());
        HasDeckTracking = hasDeckTracking; DeckIsExact = deckExact; UnknownDeckCards = unknownDeckCards;
        Choice = choice; ChoiceStatus = choiceStatus; PublicPlaysComplete = publicPlaysComplete;
        Warnings = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).ToArray());
    }
}

/// <summary>No I/O, no static-stat fallback, no player names. Query VisibleCardIds at the pinned build before Build.</summary>
public static class BattleViewBuilder
{
    public static BattleViewState Build(GameSnapshot snapshot, IReadOnlyDictionary<string, CardRecord>? cards = null,
        string? fixedBuild = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var warnings = snapshot.Quality.Warnings.ToList();
        var local = ConfirmedLocalPlayer(snapshot);
        var players = snapshot.Entities.Values.Where(e => e.Kind == EntityKind.Player)
            .Select(e => e.GetIntTag("PLAYER_ID")).OfType<int>().Distinct().ToArray();
        if (!local.HasValue || !players.Contains(local.Value))
        { local = null; warnings.Add("我方视角尚未确认，手牌与双方归属保持未知。"); }
        var enemies = players.Where(id => id != local).ToArray();
        var opponent = local.HasValue && enemies.Length == 1 ? enemies[0] : (int?)null;
        if (local.HasValue && !opponent.HasValue) warnings.Add("对手归属尚未唯一确认。");
        var tracking = local.HasValue && MatchesDeckBinding(snapshot, fixedBuild) ? snapshot.DeckTracking : null;
        if (snapshot.DeckTracking is not null && tracking is null) warnings.Add("牌库台账与当前局面或卡库版本不一致，已隐藏过期内容。");
        var publicLedger = snapshot.PublicPlays is { } ledger && ledger.MatchId == snapshot.MatchId &&
            ledger.StateVersion == snapshot.StateVersion ? ledger : null;
        if (snapshot.PublicPlays is not null && publicLedger is null) warnings.Add("公开出牌台账与当前局面不一致，已隐藏过期内容。");
        if (publicLedger is { IsComplete: false }) warnings.Add("公开出牌记录存在日志缺口。");
        warnings.AddRange(tracking?.Warnings ?? []); warnings.AddRange(publicLedger?.Warnings ?? []);

        string Name(string? id) => id is not null && cards?.TryGetValue(id, out var card) == true
            ? card.Name : id is null ? "未知卡牌" : "未识别卡牌（" + id + "）";
        string Type(EntitySnapshot entity) => CardType(entity.Tags.GetValueOrDefault("CARDTYPE")) ??
            (entity.CardId is { } id && cards?.TryGetValue(id, out var card) == true ? card.Type : "UNKNOWN");
        BattleCardView Card(EntitySnapshot entity)
        {
            var type = Type(entity);
            var statuses = StatusTags.Where(pair => entity.GetIntTag(pair.Key) is > 0).Select(pair => pair.Value).ToList();
            if (type == "HERO_POWER")
            {
                statuses.Add(entity.GetIntTag("EXHAUSTED") switch { 0 => "未疲劳（尚需校验）", > 0 => "已疲劳", _ => "疲劳未知" });
                if (entity.GetIntTag("HERO_POWER_DISABLED") is > 0 || snapshot.Entities.Values.Any(e => e.Kind == EntityKind.Player &&
                    e.GetIntTag("PLAYER_ID") == entity.ControllerId && e.GetIntTag("HERO_POWER_DISABLED") is > 0)) statuses.Add("技能禁用");
            }
            return new(entity.Id, entity.CardId, Name(entity.CardId), type, entity.Cost, entity.Attack, entity.RemainingHealth,
                entity.GetIntTag("ARMOR"), entity.GetIntTag("DURABILITY"), entity.GetIntTag("EXHAUSTED") is { } exhausted ? exhausted != 0 : null,
                entity.Zone, entity.ZonePosition, string.Join("、", statuses));
        }
        BattlePlayerView Player(int? id, bool own)
        {
            var candidates = id.HasValue ? snapshot.Entities.Values.Where(e => e.Kind == EntityKind.Player && e.GetIntTag("PLAYER_ID") == id).Take(2).ToArray() : [];
            var player = candidates is [var onlyPlayer] ? onlyPlayer : null;
            var entities = id.HasValue ? snapshot.Entities.Values.Where(e => e.Kind == EntityKind.Card && e.ControllerId == id).ToArray() : [];
            EntitySnapshot? Unique(string type) => entities.Where(e => e.Zone == "PLAY" && Type(e) == type).Take(2).ToArray() is [var only] ? only : null;
            var heroId = player?.GetIntTag("HERO_ENTITY");
            var hero = heroId.HasValue && snapshot.Entities.TryGetValue(heroId.Value, out var observedHero) &&
                observedHero.ControllerId == id && observedHero.Zone == "PLAY" ? observedHero : Unique("HERO");
            var weapon = Unique("WEAPON"); var power = Unique("HERO_POWER");
            var total = player?.GetIntTag("RESOURCES"); var spent = player?.GetIntTag("RESOURCES_USED");
            var locked = player?.GetIntTag("OVERLOAD_LOCKED"); var temporary = player?.GetIntTag("TEMP_RESOURCES");
            int? mana = total.HasValue && spent.HasValue && locked.HasValue && temporary.HasValue
                ? (int)Math.Clamp((long)total.Value - spent.Value - locked.Value + temporary.Value, 0, int.MaxValue) : null;
            int? Count(string zone, string tag) => player?.GetIntTag(tag) is >= 0 and var observed ? observed :
                zone == "HAND" && id.HasValue && snapshot.Quality.IsComplete ? entities.Count(e => e.Zone == zone) : null;
            var hand = own ? entities.Where(e => e.Zone == "HAND").OrderBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id).Select(Card) : [];
            var board = entities.Where(e => e.Zone == "PLAY" && Type(e) is "MINION" or "UNKNOWN")
                .OrderBy(e => e.ZonePosition ?? int.MaxValue).ThenBy(e => e.Id).Select(Card);
            var plays = publicLedger?.Plays.Where(p => id.HasValue && p.ControllerId == id && p.Sequence <= snapshot.EventSequence)
                .Select(p => new BattlePlayedCardView(p.Sequence, p.EntityId, p.CardId, Name(p.CardId))) ?? [];
            return new(id, own ? "我方" : "对手", hero is null ? null : Card(hero), weapon is null ? null : Card(weapon),
                power is null ? null : Card(power), mana, total, spent, locked, temporary, Count("HAND", "NUM_CARDS_IN_HAND"),
                own && tracking?.TotalCards is { } deckCount ? deckCount : Count("DECK", "NUM_CARDS_IN_DECK"), hand, board, plays);
        }
        var remaining = tracking?.KnownRemaining.OrderBy(pair => Name(pair.Key), StringComparer.Ordinal)
            .Select(pair => new BattleDeckCardView(pair.Key, Name(pair.Key), pair.Value)) ?? [];
        var exact = tracking is { IsExact: true, UnknownCount: 0, TotalCards: >= 0 } &&
            snapshot.Quality.IsComplete && snapshot.Quality.IsStable && tracking.KnownRemaining.Values.All(c => c >= 0) &&
            tracking.KnownRemaining.Values.Sum(c => (long)c) == tracking.TotalCards;
        var currentChoice = ConfirmedChoice(snapshot, local);
        var choice = currentChoice is null ? null : new BattleChoiceView(currentChoice.Id, currentChoice.CountMin,
            currentChoice.CountMax, currentChoice.Options.OrderBy(option => option.Position ?? int.MaxValue)
                .ThenBy(option => option.EntityId).Select(option => new BattleChoiceOptionView(option.EntityId,
                    option.CardId, Name(option.CardId), option.Position)));
        var choiceStatus = choice is not null ? $"当前我方选择 #{choice.Id}，选择 {choice.CountMin}～{choice.CountMax} 个。这里只展示候选，不模拟选择后效果。" :
            snapshot.Phase is GamePhase.Choice or GamePhase.Mulligan ? "我方选择尚未确认或候选未完整读取；暂不展示候选身份。" :
            "当前未处于留牌或选择阶段。";
        return new(snapshot, local.HasValue, Player(local, true), Player(opponent, false), remaining, tracking is not null, exact,
            tracking?.UnknownCount, choice, choiceStatus, publicLedger?.IsComplete == true, warnings);
    }

    public static bool MatchesDeckBinding(GameSnapshot snapshot, string? fixedBuild = null) => snapshot.DeckTracking is { } tracking &&
        ConfirmedLocalPlayer(snapshot).HasValue &&
        tracking.MatchId == snapshot.MatchId && tracking.StateVersion == snapshot.StateVersion &&
        (fixedBuild ?? snapshot.ClientBuild) is { Length: > 0 } build && build != "unknown" && tracking.CardBuild == build &&
        (snapshot.ClientBuild is null || snapshot.ClientBuild == build);

    public static IReadOnlyList<string> VisibleCardIds(GameSnapshot snapshot, string? fixedBuild = null)
    {
        var local = ConfirmedLocalPlayer(snapshot);
        var ids = snapshot.Entities.Values.Where(e => e.Kind == EntityKind.Card &&
            (e.Zone == "PLAY" || local.HasValue && e.ControllerId == local && e.Zone == "HAND"))
            .Select(e => e.CardId).OfType<string>();
        if (snapshot.PublicPlays is { } ledger && ledger.MatchId == snapshot.MatchId && ledger.StateVersion == snapshot.StateVersion)
            ids = ids.Concat(ledger.Plays.Where(p => p.Sequence <= snapshot.EventSequence).Select(p => p.CardId).OfType<string>());
        if (ConfirmedChoice(snapshot, local) is { } choice)
            ids = ids.Concat(choice.Options.Select(o => o.CardId).OfType<string>());
        if (MatchesDeckBinding(snapshot, fixedBuild)) ids = ids.Concat(snapshot.DeckTracking!.KnownRemaining.Keys);
        return Array.AsReadOnly(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static int? ConfirmedLocalPlayer(GameSnapshot snapshot) => ChoiceVisibility.ConfirmedLocalPlayer(snapshot);

    private static CurrentChoiceSnapshot? ConfirmedChoice(GameSnapshot snapshot, int? local) =>
        local.HasValue && local == snapshot.LocalPlayerId ? ChoiceVisibility.ConfirmedOwn(snapshot) : null;

    private static string? CardType(string? type) => type switch
    {
        "3" => "HERO", "4" => "MINION", "5" => "SPELL", "6" => "ENCHANTMENT", "7" => "WEAPON", "10" => "HERO_POWER",
        null or "" => null, _ => type
    };
    private static readonly ReadOnlyDictionary<string, string> StatusTags = new(new Dictionary<string, string>
    {
        ["TAUNT"] = "嘲讽", ["DIVINE_SHIELD"] = "圣盾", ["STEALTH"] = "潜行", ["FROZEN"] = "冻结",
        ["POISONOUS"] = "剧毒", ["LIFESTEAL"] = "吸血", ["RUSH"] = "突袭", ["CHARGE"] = "冲锋",
        ["WINDFURY"] = "风怒", ["DORMANT"] = "休眠", ["IMMUNE"] = "免疫", ["SILENCED"] = "已沉默"
    });
}
