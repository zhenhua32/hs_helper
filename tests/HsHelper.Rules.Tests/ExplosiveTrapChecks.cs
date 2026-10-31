using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

public static class ExplosiveTrapChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Trap both fixed native aliases preview one simultaneous all-enemy damage batch without executing the incoming attack", () =>
        {
            foreach (var id in new[] { "CORE_EX1_610", "EX1_610" })
            {
                var frame = Frame(id); var result = Eval(frame, cards);
                Check(result.TriggerActive == true && result.ResolvesDirectDamage && result.TrapConsumed == true && result.DamagePerEnemy == 2 &&
                    result.SpellDamage == 0 && result.Targets.Count == 3 && result.Targets.All(target => target.Damage == 2) &&
                    result.Targets.Single(target => target.EntityId == 20).HealthAfter == 1 && result.DefendingHeroEntityId == 10 &&
                    result.AttackerSurvivesDirectDamage == true && result.AttackStoppedByDirectDamage == false &&
                    !result.CanEvaluateFollowupActions && !result.HasAuthoritativeNextState && result.IsLegal is null && result.IsConditionalPreview &&
                    frame.Entities[10].RemainingHealth == 30 && frame.Entities[30].Zone == "SECRET", "Native trap omitted targets, performed the attack, or promoted an intent to real state.");
            }
        });
        yield return ("Trap normal attacker death cancels the declared attack before original attack damage", () =>
        {
            var result = Eval(Tag(Frame(), 20, "HEALTH", "1"), cards);
            Check(result.AttackerSurvivesDirectDamage == false && result.AttackStoppedByDirectDamage == true && result.Targets.Single(target => target.EntityId == 20).HealthAfter == -1,
                "A dead ordinary attacker could continue attacking the defending hero.");
        });
        yield return ("Trap enemy hero death ends the attack even when its attacking minion survives and the batch still hits all enemies", () =>
        {
            var result = Eval(Tag(Frame(), 11, "DAMAGE", "29"), cards);
            Check(result.AttackerSurvivesDirectDamage == true && result.AttackStoppedByDirectDamage == true && result.Targets.Count == 3 &&
                result.Targets.Single(target => target.EntityId == 11).IsAliveAfter == false && result.Targets.Single(target => target.EntityId == 21).Damage == 2,
                "Game-ending hero damage was missed or stopped the simultaneous batch before its other recipients.");
        });
        yield return ("Trap one damage packet removes shields and armor while immunity preserves both", () =>
        {
            var frame = Tag(Tag(Frame(), 20, "DIVINE_SHIELD", "1"), 11, "ARMOR", "3"); var result = Eval(frame, cards);
            var shield = result.Targets.Single(target => target.EntityId == 20); var hero = result.Targets.Single(target => target.EntityId == 11);
            Check(shield.Damage == 0 && shield.HealthAfter == 3 && !shield.DivineShieldAfter && hero.ArmorAfter == 1 && hero.HealthAfter == 10, "Shield or armor was resolved as separate one-point packets.");
            frame = Tag(Tag(frame, 11, "IMMUNE", "1"), 11, "DIVINE_SHIELD", "1"); hero = Eval(frame, cards).Targets.Single(target => target.EntityId == 11);
            Check(hero.Damage == 0 && hero.ArmorAfter == 3 && hero.DivineShieldAfter && hero.HealthAfter == 10, "Immunity consumed shield or armor.");
        });
        yield return ("Trap native current Spell Damage adds once and global totals cannot contradict a known body lower bound", () =>
        {
            var frame = Add(Frame(), Minion(40, "CORE_CS2_142", 1, 2)); var result = Eval(frame, cards);
            Check(result.SpellDamage == 1 && result.DamagePerEnemy == 3, "Native current spell-damage body did not boost the trap.");
            frame = Tag(Tag(frame, 1, "CURRENT_SPELLPOWER", "1"), 40, "SPELLPOWER", "1");
            Check(Eval(frame, cards).DamagePerEnemy == 3, "Global and individual spell power were added twice.");
            Unknown(Eval(Tag(frame, 1, "CURRENT_SPELLPOWER", "0"), cards));
            Check(Eval(Tag(frame, 1, "CURRENT_SPELLPOWER", "2"), cards).DamagePerEnemy == 4, "A reliable larger aggregate could not account for additional current power.");
            frame = Remove(Tag(Frame(), 1, "CURRENT_SPELLPOWER", "3"), 1, "CURRENT_SPELLPOWER");
            frame = Tag(frame, 30, "NEXT_SPELL_SPELLPOWER", "9"); Check(Eval(frame, cards).DamagePerEnemy == 2, "A future next-spell token was used as secret-trigger spell damage.");
        });
        yield return ("Trap known native hero powers on both sides do not spuriously trigger or alter the damage batch", () =>
        {
            foreach (var own in new[] { "HERO_05bp", "EDR_850p" })
            foreach (var enemy in Enumerable.Range(1, 11).Select(value => "HERO_" + value.ToString("00") + "bp"))
            {
                var frame = Add(Add(Frame(), Power(50, own, 1)), Power(51, enemy, 2));
                Check(Eval(frame, cards).ResolvesDirectDamage && Eval(frame, cards).DamagePerEnemy == 2, "A normal native active hero power was mistaken for a secret-trigger script.");
            }
            var modified = Alter(cards, "EDR_850p", "你的奥秘造成的伤害翻倍。", []);
            Unknown(Eval(Add(Frame(), Power(50, "EDR_850p", 1)), modified));
        });
        yield return ("Trap owner-turn preview is a current no-trigger result without falsely reporting current spell power zero", () =>
        {
            var frame = Add(Frame(), Minion(40, "CORE_CS2_142", 1, 2)); frame = Tag(Tag(frame with { ActivePlayerId = 1 }, 1, "CURRENT_PLAYER", "1"), 2, "CURRENT_PLAYER", "0");
            var result = Eval(frame, cards); Check(result.TriggerActive == false && result.TrapConsumed == false && result.DamagePerEnemy == 0 &&
                result.SpellDamage is null && result.Targets.Count == 0 && !result.RequiresObservation, "Own-turn non-trigger became a future prediction or fabricated zero current spell power.");
        });
        yield return ("Trap explicit current-turn aliases must agree with the snapshot active player", () =>
        {
            Unknown(Eval(Tag(Tag(Frame(), 1, "CURRENT_PLAYER", "1"), 2, "CURRENT_PLAYER", "0"), cards));
            Unknown(Eval(Tag(Frame(), 1, "23", "1"), cards));
        });
        yield return ("Trap modern native Longbow and same-text Candleshot protect only an attacking hero and preserve its shield and armor", () =>
        {
            foreach (var id in new[] { "DS1_188", "CORE_LOOT_222" })
            {
                var frame = Add(Tag(Tag(Frame(), 11, "ARMOR", "3"), 11, "DIVINE_SHIELD", "1"), Weapon(60, id, 2));
                var result = Eval(frame, cards, 11); var hero = result.Targets.Single(target => target.EntityId == 11);
                Check(result.ResolvesDirectDamage && hero.Damage == 0 && hero.ArmorAfter == 3 && hero.DivineShieldAfter &&
                    result.Targets.Where(target => target.Type == "MINION").All(target => target.Damage == 2), "Native while-attacking immunity was missing or incorrectly protected the other enemies.");
                Unknown(Eval(Tag(frame, 60, "DAMAGE", "3"), cards, 11));
                Unknown(Eval(Remove(frame, 60, "DAMAGE"), cards, 11));
                Unknown(Eval(Tag(frame, 60, "45", "4"), cards, 11));
            }
            var plain = Eval(Frame(), cards, 11); Check(plain.ResolvesDirectDamage && plain.Targets.Single(target => target.EntityId == 11).Damage == 2, "An ordinary attacking hero inherited weapon immunity.");
        });
        yield return ("Trap unknown while-attacking immunity and nonnative weapon text cannot become exact normal hero damage", () =>
        {
            Unknown(Eval(Tag(Frame(), 11, "373", "1"), cards, 11));
            var altered = Alter(cards, "DS1_188", "你的英雄攻击时获得其它未知效果。", []);
            Unknown(Eval(Add(Frame(), Weapon(60, "DS1_188", 2)), altered, 11));
        });
        yield return ("Trap known native deathrattle or granted Reborn leave an explicit direct prefix and never create replacement entities", () =>
        {
            foreach (var frame in new[] { Add(Frame(), Minion(20, "EDR_227", 2, 1)), Tag(Tag(Frame(), 20, "HEALTH", "1"), 20, "REBORN", "1") })
            {
                var result = Eval(frame, cards); Check(result.ResolvesDirectDamage && result.Targets.Count == 3 && result.AttackerSurvivesDirectDamage == false &&
                    result.AttackStoppedByDirectDamage is null && result.RequiresObservation && result.Unknowns.Any(value => value.Contains("前缀", StringComparison.Ordinal)), "Death aftermath was fabricated or promoted to a settled attack continuation.");
            }
        });
        yield return ("Trap audited Macaw battlecry body does not fire merely from taking this batch damage", () =>
        {
            var frame = Add(Frame(), Minion(20, "CORE_UNG_912", 2, 4)); Check(Eval(frame, cards).ResolvesDirectDamage, "An enter-play-only native Battlecry blocked the damage prefix.");
        });
        yield return ("Trap damage replacement and unreviewed immunity aura bodies are stopped before any false direct prefix", () =>
        {
            Unknown(Eval(Add(Frame(), Minion(40, "AT_124", 2, 9)), cards));
            Unknown(Eval(Add(Frame(), Minion(40, "CORE_GVG_021", 2, 7)), cards));
            Unknown(Eval(Frame(), Alter(cards, "CS2_172", "改由本随从承受你的英雄受到的伤害。", [])));
        });
        yield return ("Trap source type school race custom halves and raw definition substitutions never reuse the native card ID", () =>
        {
            foreach (var (key, value) in new[] { ("CARDTYPE", "MINION"), ("202", "4"), ("SPELL_SCHOOL", "SHADOW"), ("1635", "6"),
                ("CARDRACE", "20"), ("200", "20"), ("4572", "1"), ("4573", "bad"), ("219", "0") }) Unknown(Eval(Tag(Frame(), 30, key, value), cards));
            foreach (var replacement in new[] { cards["CORE_EX1_610"] with { DbfId = 585 }, cards["CORE_EX1_610"] with { Text = "other" }, cards["CORE_EX1_610"] with { RawJson = "[]" } })
            { var changed = cards.ToDictionary(); changed["CORE_EX1_610"] = replacement; Check(!ExplosiveTrapRules.IsAuditedSource(replacement), "Wrong native source passed the installation audit."); Unknown(Eval(Frame(), changed)); }
        });
        yield return ("Trap named numeric protection aliases and public school or damage modifiers retain unknowns", () =>
        {
            foreach (var (key, value) in new[] { ("274", "1"), ("247", "1"), ("4439", "1"), ("356", "1"), ("357", "1"), ("1937", "2"), ("1946", "1"), ("2252", "3") })
                Unknown(Eval(Tag(Frame(), 1, key, value), cards));
            Unknown(Eval(Tag(Tag(Frame(), 20, "IMMUNE", "1"), 20, "240", "0"), cards));
            Unknown(Eval(Tag(Tag(Frame(), 20, "DIVINE_SHIELD", "1"), 20, "194", "0"), cards));
            Unknown(Eval(Tag(Tag(Frame(), 30, "LIFESTEAL", "1"), 30, "685", "0"), cards));
        });
        yield return ("Trap active non-minion spellpower sources and owner character type conflicts cannot vanish from the context", () =>
        {
            Unknown(Eval(Add(Frame(), Tag(Weapon(60, "CS2_106", 1), "SPELLPOWER", "1")), cards));
            Unknown(Eval(Add(Frame(), Tag(Minion(40, "CORE_CS2_142", 1, 2), "202", "5")), cards));
            var enchant = Tag(Tag(Minion(70, "PRIVATE_OWN_ENCHANT", 1, 1), "CARDTYPE", "ENCHANTMENT"), "ATTACHED", "10");
            Unknown(Eval(Add(Frame(), enchant), cards));
        });
        yield return ("Trap multiple owner secrets have unknown ordering while an exactly audited sidequest is not an attack secret", () =>
        {
            Unknown(Eval(Add(Frame(), Secret(40, "EX1_610")), cards));
            var frame = Add(Frame(), Secret(40, "TLC_EVENT_400")); Check(Eval(frame, cards).ResolvesDirectDamage, "A fixed play-Beast-or-Undead sidequest was treated as another attack secret.");
            Unknown(Eval(frame, Alter(cards, "TLC_EVENT_400", "你的英雄受到攻击时获得免疫。", ["SIDE_QUEST"])));
        });
        yield return ("Trap opponent hidden secrets on their owner's turn neither trigger nor disclose their identity", () =>
        {
            var hidden = Tag(Tag(Secret(90, "PRIVATE_OPPONENT_SECRET"), "CONTROLLER", "2"), "SECRET", "1"); var result = Eval(Add(Frame(), hidden), cards);
            Check(result.ResolvesDirectDamage && result.DamagePerEnemy == 2 && !JsonSerializer.Serialize(result).Contains("PRIVATE_", StringComparison.Ordinal), "An inactive opposing secret altered the current batch or leaked its identity.");
            Unknown(Eval(Add(Frame(), Tag(hidden, "QUEST", "1")), cards));
            Unknown(Eval(Remove(Add(Frame(), hidden), 90, "SECRET"), cards));
        });
        yield return ("Trap optional trigger overlays bind immutable metadata and cannot omit second secrets or change player IDs", () =>
        {
            var frame = Frame(); var state = State(frame);
            foreach (var bad in new[] { state with { MatchId = "other" }, state with { StateVersion = 11 }, state with { EventSequence = 101 },
                state with { SecretEntityId = 31 }, state with { AttackerEntityId = 21 },
                state with { ParticipantsBeforeTrigger = state.ParticipantsBeforeTrigger.Where(entity => entity.Id != 1).ToArray() },
                state with { ParticipantsBeforeTrigger = state.ParticipantsBeforeTrigger.Select(entity => entity.Id == 1 ? Tag(entity, "PLAYER_ID", "2") : entity).ToArray() } }) Unknown(Eval(frame, cards, state: bad));
            frame = Add(frame, Secret(40, "EX1_610")); Unknown(Eval(frame, cards, state: state));
            Unknown(Eval(Frame(), cards, state: State(Frame()) with { PriorResolutionPending = true }));
        });
        yield return ("Trap perspective phase visibility and complete participant requirements reject hidden sources before returning targets", () =>
        {
            var frame = Frame(); foreach (var bad in new[] { frame with { IsSpectating = true }, frame with { ClientBuild = "255791" },
                frame with { Quality = frame.Quality with { IsComplete = false } }, frame with { Quality = frame.Quality with { IsStable = false } },
                frame with { Quality = frame.Quality with { HasLocalPerspective = false } }, frame with { Phase = GamePhase.Choice },
                Tag(frame, 30, "ZONE", "HAND"), Tag(frame, 30, "CONTROLLER", "2"), Tag(frame, 20, "ZONE", "HAND") }) Unknown(Eval(bad, cards));
            var spectator = Eval(frame with { IsSpectating = true }, cards); Check(spectator.SecretCardId is null && spectator.DefendingHeroEntityId is null, "Rejected perspective exported private source identity.");
        });
        yield return ("Trap previews freeze lists and do not consume the actual secret or modify the original cards", () =>
        {
            var frame = Frame(); var before = JsonSerializer.Serialize(frame); var catalog = JsonSerializer.Serialize(cards); var input = State(frame).ParticipantsBeforeTrigger.ToList();
            var state = new ExplosiveTrapState(frame.MatchId, 10, 100, 30, 20, input); input.Clear(); var result = Eval(frame, cards, state: state);
            Check(result.ResolvesDirectDamage && before == JsonSerializer.Serialize(frame) && catalog == JsonSerializer.Serialize(cards), "Caller mutation or preview consumed authoritative data.");
            try { ((IList<ExplosiveTrapDamageResult>)result.Targets).Clear(); throw new Exception("Mutable Trap targets."); } catch (NotSupportedException) { }
        });
    }

    private static ExplosiveTrapOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string,CardRecord> cards, int attacker = 20, ExplosiveTrapState? state = null) => ExplosiveTrapRules.Evaluate(frame, 30, attacker, cards, state);
    private static GameSnapshot Frame(string source = "CORE_EX1_610") => new("trap-fixture", 10, 100, 5, 2, 1, GamePhase.Main,
        new[] { Player(1, 1, 0), Player(2, 2, 1), Hero(10, 1, 30), Hero(11, 2, 10), Secret(30, source), Minion(20, "CS2_172", 2, 3), Minion(21, "CS2_172", 2, 4) }
            .ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    private static EntitySnapshot Player(int id, int player, int current) => new(id, null, EntityKind.Player,
        new Dictionary<string,string> { ["PLAYER_ID"] = player.ToString(), ["CONTROLLER"] = player.ToString(), ["CURRENT_PLAYER"] = current.ToString() }.ToFrozenDictionary());
    private static EntitySnapshot Hero(int id, int owner, int health) => new(id, "HERO_" + owner, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO", ["HEALTH"] = "30", ["DAMAGE"] = (30-health).ToString(), ["ARMOR"] = "0", ["ATK"] = "3" }.ToFrozenDictionary());
    private static EntitySnapshot Minion(int id, string card, int owner, int health) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "MINION", ["HEALTH"] = health.ToString(), ["DAMAGE"] = "0", ["ATK"] = "3" }.ToFrozenDictionary());
    private static EntitySnapshot Secret(int id, string card) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = "1", ["ZONE"] = "SECRET", ["CARDTYPE"] = "SPELL" }.ToFrozenDictionary());
    private static EntitySnapshot Power(int id, string card, int owner) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "HERO_POWER" }.ToFrozenDictionary());
    private static EntitySnapshot Weapon(int id, string card, int owner) => new(id, card, EntityKind.Card,
        new Dictionary<string,string> { ["CONTROLLER"] = owner.ToString(), ["ZONE"] = "PLAY", ["CARDTYPE"] = "WEAPON", ["HEALTH"] = "3", ["DAMAGE"] = "0" }.ToFrozenDictionary());
    private static ExplosiveTrapState State(GameSnapshot frame) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, 30, 20, frame.Entities.Values.ToArray());
    private static EntitySnapshot Tag(EntitySnapshot entity, string key, string value) { var tags = entity.Tags.ToDictionary(); tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static GameSnapshot Tag(GameSnapshot frame, int id, string key, string value) => Add(frame, Tag(frame.Entities[id], key, value));
    private static GameSnapshot Add(GameSnapshot frame, EntitySnapshot entity) { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static GameSnapshot Remove(GameSnapshot frame, int id, string key) { var entity = frame.Entities[id]; var tags = entity.Tags.ToDictionary(); tags.Remove(key); return Add(frame, entity with { Tags = tags.ToFrozenDictionary() }); }
    private static IReadOnlyDictionary<string,CardRecord> Alter(IReadOnlyDictionary<string,CardRecord> cards, string id, string text, string[] mechanics)
    { var changed = cards.ToDictionary(); var raw = JsonNode.Parse(cards[id].RawJson)!.AsObject(); raw["text"] = text; raw["mechanics"] = new JsonArray(mechanics.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()); changed[id] = cards[id] with { Text = text, RawJson = raw.ToJsonString() }; return changed; }
    private static IReadOnlyDictionary<string,CardRecord> Cards()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "explosive-trap-255790.json")));
        Check(input.RootElement.GetProperty("build").GetString() == "255790", "Unpinned trap fixture.");
        return input.RootElement.GetProperty("cards").EnumerateArray().Select(raw => new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
            raw.TryGetProperty("text", out var text) ? text.GetString()! : "", raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
            raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText())).ToFrozenDictionary(card => card.Id);
    }
    private static void Unknown(ExplosiveTrapOutcome result) => Check(!result.ResolvesDirectDamage && result.Targets.Count == 0 && result.AttackStoppedByDirectDamage is null, "Unknown context acquired an exact damage batch or attack continuation.");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
