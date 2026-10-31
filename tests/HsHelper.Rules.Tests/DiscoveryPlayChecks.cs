using System.Collections.Frozen;
using System.Text.Json;
using HsHelper.Cards;
using HsHelper.Core;
using HsHelper.Rules;

internal static class DiscoveryPlayChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        var cards = Cards();
        yield return ("Discovery registry recognizes only the seven audited source identities", () =>
        {
            Check(cards.Keys.All(DiscoveryPlayRules.SupportsCardId) && !DiscoveryPlayRules.SupportsCardId(null) &&
                !DiscoveryPlayRules.SupportsCardId("DS1_184") && !DiscoveryPlayRules.SupportsCardId("EDR_004") &&
                !DiscoveryPlayRules.SupportsCardId("core_ds1_184") && !DiscoveryPlayRules.SupportsCardId("TLC_EVENT_400"),
                "Native-use routing broadened by name, case, prefix or unrelated task source.");
        });
        yield return ("Discovery native fixed sources expose distinct operation scopes without generated results", () =>
        {
            foreach (var (id, kind) in new[] { ("CORE_DS1_184", "DeckDiscover"), ("TLC_824", "OddAttackBeastDiscover"),
                ("EDR_888", "WildGodDiscover"), ("JAIL_831", "SideboardDiscover"), ("BE_044", "AssembleFourCostMinion"),
                ("CORE_EDR_004_2026", "RewindDarkGiftBeastDiscover"), ("CORE_EDR_004", "RewindDarkGiftBeastDiscover") })
            {
                var frame = Frame(id); var original = JsonSerializer.Serialize(frame); var result = Eval(frame, cards);
                Check(result.ResolvesEffects && result.Kind == kind && result.SourceCardId == id && result.EffectActive == true &&
                    (id == "JAIL_831" ? result.RequiresChoice is null : result.RequiresChoice == true) && result.Scope.Length > 0 &&
                    result.IsLegal is null && !result.HasAuthoritativeNextState && !result.CanEvaluateFollowupActions &&
                    original == JsonSerializer.Serialize(frame), "Native operation was rejected, conflated, or presented as an authoritative future hand.");
            }
        });
        yield return ("Discovery exact DBF native text type and mechanics reject metadata substitutions", () =>
        {
            foreach (var id in cards.Keys)
            {
                var native = cards[id]; var frame = Frame(id);
                foreach (var replacement in new[] { native with { DbfId = native.DbfId + 1 }, native with { Id = "fake" },
                    native with { Type = native.Type == "MINION" ? "SPELL" : "MINION" }, native with { Text = native.Text + "x" },
                    native with { RawJson = native.RawJson.Replace("\"DISCOVER\"", "\"TAUNT\"", StringComparison.Ordinal).Replace("\"BATTLECRY\"", "\"TAUNT\"", StringComparison.Ordinal) },
                    native with { RawJson = native.RawJson.Replace("\"dbfId\":", "\"dbfId\":0,\"dbfId\":", StringComparison.Ordinal) } })
                {
                    var altered = cards.ToDictionary(); altered[id] = replacement;
                    Check(!Eval(frame, altered).ResolvesEffects, "Wrong fixed identity/text/mechanics or duplicate JSON property executed a native operation.");
                }
            }
            Check(!Eval(Frame("DS1_184"), cards).ResolvesEffects, "Unreviewed Tracking alias was accepted by name.");
        });
        yield return ("Discovery current source identity type ownership zone and entity aliases must agree", () =>
        {
            foreach (var source in new[] { Source("EDR_888") with { CardId = null }, Source("EDR_888") with { Kind = EntityKind.Player },
                Tagged(Source("EDR_888"), ("202", "SPELL")), Tagged(Source("EDR_888"), ("CARDTYPE", "bad")),
                Tagged(Source("EDR_888"), ("CONTROLLER", "2")), Tagged(Source("EDR_888"), ("50", "2")),
                Tagged(Source("EDR_888"), ("49", "1")), Tagged(Source("EDR_888"), ("ENTITY_ID", "31")),
                Tagged(Source("EDR_888"), ("53", "31")), Tagged(Source("EDR_888"), ("ZONE", "DECK")),
                Without(Source("EDR_888"), "CARDTYPE"), Source("EDR_888") with { Tags = null! } })
                Check(!Eval(Replace(Frame("EDR_888"), source), cards).ResolvesEffects, "Ambiguous source became an authorized native use.");
            var valid = Tagged(Source("EDR_888"), ("202", "4"), ("50", "1"), ("49", "3"), ("53", "30"));
            Check(Eval(Replace(Frame("EDR_888"), valid), cards).ResolvesEffects, "Equivalent source aliases were lost.");
        });
        yield return ("Discovery build perspective completeness and player counter ownership must be bound", () =>
        {
            foreach (var frame in new[] { Frame() with { ClientBuild = "255791" }, Frame() with { IsSpectating = true },
                Frame() with { ActivePlayerId = 2 }, Frame() with { Phase = GamePhase.Choice },
                Frame() with { Quality = Frame().Quality with { HasGameStart = false } },
                Frame() with { Quality = Frame().Quality with { HasLocalPerspective = false } },
                Frame() with { Quality = Frame().Quality with { IsComplete = false } },
                Frame() with { Quality = Frame().Quality with { IsStable = false } },
                Frame() with { Quality = Frame().Quality with { OpenBlockCount = 1 } },
                Replace(Frame(), Tagged(Frame().Entities[1], ("50", "2"))) })
                Check(!Eval(frame, cards).ResolvesEffects, "Unsafe observation supplied a source or deck scope.");
        });
        yield return ("Discovery custom assembled sources and offer markers cannot execute their base native effects", () =>
        {
            foreach (var id in cards.Keys)
            {
                foreach (var (tag, value) in new[] { ("4572", "1"), ("CRAFTABLE_SECOND_HALF_DBID", "123"),
                    ("4573", "bad"), ("CRAFTABLE_FIRST_HALF_DBID", "-1") })
                    Check(!Eval(Replace(Frame(id), Tagged(Source(id), (tag, value))), cards).ResolvesEffects,
                        "Custom half or offer marker executed its base native discovery/assembly.");
                var zeros = Tagged(Source(id), ("4572", "0"), ("CRAFTABLE_FIRST_HALF_DBID", "0"), ("4573", "0"));
                Check(Eval(Replace(Frame(id), zeros), cards).ResolvesEffects, "Explicit native zero halves were treated as assembled.");
            }
        });
        yield return ("Discovery suppressed battlecries request no native choice for non-Rewind minions", () =>
        {
            foreach (var id in new[] { "EDR_888", "JAIL_831", "BE_044" })
                foreach (var suppression in new[] { new[] { ("SILENCED", "1") }, new[] { ("BATTLECRY", "0") },
                    new[] { ("188", "1") }, new[] { ("218", "0") } })
                {
                    var result = Eval(Replace(Frame(id), Tagged(Source(id), suppression)), cards);
                    Check(result.ResolvesEffects && result.EffectActive == false && result.RequiresDiscoveryChoice == false && result.RequiresChoice == false,
                        "Suppressed battlecry was treated as a live native effect or forced choice.");
                }
        });
        yield return ("Discovery battlecry and silence conflicts remain unknown without choosing an alias", () =>
        {
            foreach (var tags in new[] { new[] { ("BATTLECRY", "1"), ("218", "0") }, new[] { ("SILENCED", "0"), ("188", "1") },
                new[] { ("BATTLECRY", "bad") }, new[] { ("SILENCED", "-1") }, new[] { ("218", "2") } })
            {
                var frame = Replace(Frame("EDR_888"), Tagged(Source("EDR_888"), tags));
                var result = Eval(frame, cards);
                Check(result.ResolvesEffects && result.EffectActive is null && result.RequiresDiscoveryChoice is null && result.RequiresChoice is null,
                    "Conflicting battlecry evidence became active or suppressed by fiat.");
                Check(Eval(frame, cards, State(frame, 5, true)).EffectActive is null, "Overlay repaired invalid original battlecry evidence.");
            }
        });
        yield return ("Discovery bound current battlecry overlay preserves unknown and covered suppression", () =>
        {
            var frame = Frame("EDR_888");
            Check(Eval(frame, cards, State(frame, 5, false)) is { EffectActive: false, RequiresChoice: false }, "Known current suppression was replaced by old printed battlecry.");
            Check(Eval(frame, cards, State(frame, 5, null)).EffectActive is null, "Unknown current battlecry borrowed old default activation.");
            frame = Replace(frame, Tagged(Source("EDR_888"), ("BATTLECRY", "0")));
            Check(Eval(frame, cards, State(frame, 5, true)).EffectActive is null, "Current overlay overruled explicit original suppression with true.");
        });
        yield return ("Discovery Rewind remains independent when the Raptor battlecry is absent", () =>
        {
            foreach (var id in new[] { "CORE_EDR_004_2026", "CORE_EDR_004" })
            {
                var frame = Replace(Frame(id), Tagged(Source(id), ("BATTLECRY", "0")));
                var result = Eval(frame, cards);
                Check(result is { EffectActive: false, RequiresDiscoveryChoice: false, RewindActive: true, RequiresChoice: true },
                    "Battlecry zero erased the separately printed current Rewind keyword.");
                frame = Replace(frame, Tagged(frame.Entities[30], ("SILENCED", "1"), ("REWIND", "1")));
                Check(Eval(frame, cards) is { EffectActive: false, RewindActive: true, RequiresChoice: true }, "Explicit current Rewind was erased by silence/battlecry suppression.");
                frame = Replace(frame, Tagged(frame.Entities[30], ("REWIND", "0")));
                Check(Eval(frame, cards) is { RewindActive: false, RequiresChoice: false }, "Explicit removed Rewind still forced its timeline choice.");
            }
        });
        yield return ("Discovery missing or conflicting remaining Rewind never infers no timeline choice", () =>
        {
            foreach (var extra in new[] { new[] { ("SILENCED", "1") }, new[] { ("USED_REWIND", "1") },
                new[] { ("REWIND", "1"), ("3934", "0") }, new[] { ("REWIND", "bad") },
                new[] { ("REWIND", "-1") }, new[] { ("REWIND", "0"), ("3945", "bad") } })
            {
                var source = Tagged(Source("CORE_EDR_004_2026"), ("BATTLECRY", "0")); source = Tagged(source, extra);
                var result = Eval(Replace(Frame(source.CardId!), source), cards);
                Check(result.RewindActive is null && result.RequiresChoice is null && result.RequiresDiscoveryChoice == false,
                    "Unknown current Rewind or spent marker was reinterpreted as zero remaining choices.");
            }
        });
        yield return ("Discovery Tracking requires reliable count and handles explicit empty and nonempty decks", () =>
        {
            var empty = Frame("CORE_DS1_184", 0); var result = Eval(empty, cards);
            Check(result is { EffectActive: true, DeckCountBeforeEffect: 0, RequiresDiscoveryChoice: false, RequiresChoice: false },
                "Reliable empty deck generated a forced Tracking choice or a draw/fatigue result.");
            var positive = Frame("CORE_DS1_184", 5);
            Check(Eval(positive, cards) is { DeckCountBeforeEffect: 5, RequiresChoice: true }, "Known nonempty count required guessed full identities.");
            var missing = Replace(Frame("CORE_DS1_184"), Without(Frame().Entities[1], "NUM_CARDS_IN_DECK"));
            Check(Eval(missing, cards) is { DeckCountBeforeEffect: null, RequiresChoice: null }, "Zero created DECK entities were guessed to be an empty deck.");
        });
        yield return ("Discovery Tracking count contradictions cannot be repaired by a zero overlay", () =>
        {
            foreach (var raw in new[] { "bad", "-1", "2147483648" })
            {
                var frame = Replace(Frame("CORE_DS1_184"), Tagged(Frame().Entities[1], ("NUM_CARDS_IN_DECK", raw)));
                Check(Eval(frame, cards, State(frame, 0)) is { DeckCountBeforeEffect: null, RequiresChoice: null }, "Overlay repaired malformed original deck count.");
            }
            var conflict = Replace(Frame("CORE_DS1_184", 0), Tagged(Source("TLC_824") with { Id = 70 }, ("ZONE", "DECK")));
            Check(Eval(conflict, cards, State(conflict, 0)).RequiresChoice is null, "Explicit deck zero ignored a positive owned DECK entity.");
        });
        yield return ("Discovery bound DeckTracking count supports empty scope without reusing foreign ledgers", () =>
        {
            var frame = Frame("CORE_DS1_184", null);
            var tracking = new DeckTrackingSnapshot(frame.MatchId, frame.StateVersion, "fixture", "255790", 0,
                new Dictionary<string, int>().ToFrozenDictionary(), 0, true, []);
            Check(Eval(frame with { DeckTracking = tracking }, cards) is { DeckCountBeforeEffect: 0, RequiresChoice: false }, "Bound empty deck ledger was ignored.");
            foreach (var bad in new[] { tracking with { MatchId = "other" }, tracking with { StateVersion = 11 },
                tracking with { CardBuild = "255791" }, tracking with { TotalCards = -1 } })
                Check(Eval(frame with { DeckTracking = bad }, cards).RequiresChoice is null, "Foreign/future/negative deck ledger produced a no-choice shortcut.");
            var contradiction = Frame("CORE_DS1_184", 1) with { DeckTracking = tracking };
            Check(Eval(contradiction, cards).RequiresChoice is null, "Conflicting bound deck totals selected the empty one.");
        });
        yield return ("Discovery overlay counts are current and null never borrows previous deck or sideboard scope", () =>
        {
            var frame = Frame("CORE_DS1_184", 5);
            Check(Eval(frame, cards, State(frame, 0)).RequiresChoice == false, "Earlier covered removal could not empty the current deck.");
            Check(Eval(frame, cards, State(frame, null)).RequiresChoice is null, "Unknown current deck count borrowed initial five.");
            Check(Eval(frame, cards, State(frame, -1)).RequiresChoice is null, "Negative current count became a deck scope.");
            frame = Frame("JAIL_831");
            Check(Eval(frame, cards).RequiresChoice is null, "Missing sideboard became printed three or zero.");
            foreach (var count in new[] { 0, 1, 3, -1, 4 })
            {
                var result = Eval(frame, cards, State(frame, 5) with { SideboardCandidateCount = count });
                Check(count is < 0 or > 3 ? result.SideboardCandidateCount is null && result.RequiresChoice is null :
                    result.SideboardCandidateCount == count && result.RequiresChoice == (count > 0), "Invalid, empty or nonempty current sideboard scope was misclassified.");
            }
        });
        yield return ("Discovery bound deck scalar rejects contradictory composition lower bounds", () =>
        {
            var frame = Frame("CORE_DS1_184", null);
            var empty = new DeckTrackingSnapshot(frame.MatchId, frame.StateVersion, "fixture", "255790", 0,
                new Dictionary<string, int>().ToFrozenDictionary(), 0, false, []);
            var two = new Dictionary<string, int> { ["EDR_227"] = 2 }.ToFrozenDictionary();
            var one = new Dictionary<string, int> { ["EDR_227"] = 1 }.ToFrozenDictionary();
            foreach (var invalid in new[] { empty with { KnownRemaining = null! }, empty with { KnownRemaining = two },
                empty with { UnknownCount = 1 }, empty with { UnknownCount = -1 },
                empty with { KnownRemaining = new Dictionary<string, int> { ["EDR_227"] = -1 }.ToFrozenDictionary() },
                empty with { TotalCards = 2, KnownRemaining = two, UnknownCount = 1 },
                empty with { TotalCards = 2, KnownRemaining = one, IsExact = true },
                empty with { TotalCards = 2, KnownRemaining = one, UnknownCount = 1, IsExact = true },
                empty with { TotalCards = null, IsExact = true } })
            {
                var observed = frame with { DeckTracking = invalid };
                Check(Eval(observed, cards).RequiresChoice is null && Eval(observed, cards, State(observed, 0)).RequiresChoice is null,
                    "Inconsistent bound tracker supplied a zero-count shortcut or was repaired by a zero overlay.");
            }
            var partial = empty with { TotalCards = 2, KnownRemaining = one };
            Check(Eval(frame with { DeckTracking = partial }, cards) is { DeckCountBeforeEffect: 2, RequiresChoice: true },
                "A consistent nonexact composition could not supply its reliable positive scalar.");
        });
        yield return ("Discovery overlays require exact source match state and event sequence", () =>
        {
            var frame = Frame(); var state = State(frame, 0);
            foreach (var bad in new[] { state with { MatchId = "other" }, state with { StateVersion = 11 },
                state with { EventSequence = 101 }, state with { SourceEntityId = 31 } })
                Check(!Eval(frame, cards, bad).ResolvesEffects, "Unbound overlay enabled a native effect.");
        });
        yield return ("Discovery only trusted successful leave-hand overlays permit post-use source zones", () =>
        {
            foreach (var (id, zone) in new[] { ("CORE_DS1_184", "GRAVEYARD"), ("EDR_888", "PLAY") })
            {
                var frame = Replace(Frame(id), Tagged(Source(id), ("ZONE", zone)));
                Check(!Eval(frame, cards).ResolvesEffects && !Eval(frame, cards, State(frame, 5)).ResolvesEffects,
                    "An old played source automatically repeated its native effect.");
                Check(Eval(frame, cards, State(frame, 5) with { SourceHasLeftHand = true }).ResolvesEffects,
                    "Trusted current successful use could not describe the original source's operation.");
            }
        });
        yield return ("Discovery pending earlier resolution remains conditional and independent of choice availability", () =>
        {
            var frame = Frame("CORE_DS1_184", 0);
            var result = Eval(frame, cards, State(frame, 0) with { SourceHasLeftHand = true, PriorResolutionPending = true });
            Check(result.ResolvesEffects && result.RequiresChoice == false && result.IsConditionalPreview && !result.CanEvaluateFollowupActions &&
                !result.HasAuthoritativeNextState && result.Unknowns.Count > 0, "No native choice was mistaken for settled prior effects or a usable next state.");
        });
        yield return ("Discovery projections retain observation binding and freeze explanatory collections", () =>
        {
            var frame = Frame("BE_044"); var result = Eval(frame, cards);
            Check(result.MatchId == frame.MatchId && result.StateVersion == frame.StateVersion && result.EventSequence == frame.EventSequence &&
                result.SourceEntityId == 30 && result.RequiresDiscoveryChoice == true && result.Scope.Contains("时序", StringComparison.Ordinal),
                "Assemble use claimed a primary/finished hand card or lost the original observation binding.");
            try { ((IList<string>)result.RuleCoverage).Clear(); throw new Exception("Mutable coverage."); } catch (NotSupportedException) { }
            try { ((IList<string>)result.Unknowns).Clear(); throw new Exception("Mutable unknowns."); } catch (NotSupportedException) { }
        });
    }

    private static DiscoveryPlayOutcome Eval(GameSnapshot frame, IReadOnlyDictionary<string, CardRecord> cards, DiscoveryPlayState? state = null) =>
        DiscoveryPlayRules.Evaluate(frame, 30, cards, state);
    private static DiscoveryPlayState State(GameSnapshot frame, int? deck, bool? battlecry = true) => new(frame.MatchId, frame.StateVersion, frame.EventSequence, 30, deck, battlecry);
    private static GameSnapshot Frame(string card = "TLC_824", int? deck = 5)
    {
        var tags = new Dictionary<string, string> { ["PLAYER_ID"] = "1", ["CONTROLLER"] = "1" };
        if (deck.HasValue) tags["NUM_CARDS_IN_DECK"] = deck.Value.ToString();
        return new("discovery-play", 10, 100, 5, 1, 1, GamePhase.Main,
            new[] { new EntitySnapshot(1, null, EntityKind.Player, tags.ToFrozenDictionary()),
                new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
                Source(card) }.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = "255790" };
    }
    private static EntitySnapshot Source(string card) => new(30, card, EntityKind.Card, new Dictionary<string, string>
        { ["ZONE"] = "HAND", ["CONTROLLER"] = "1", ["CARDTYPE"] = card is "CORE_DS1_184" or "TLC_824" ? "SPELL" : "MINION" }.ToFrozenDictionary());
    private static EntitySnapshot Tagged(EntitySnapshot entity, params (string Key, string Value)[] values)
    { var tags = entity.Tags.ToDictionary(); foreach (var (key, value) in values) tags[key] = value; return entity with { Tags = tags.ToFrozenDictionary() }; }
    private static EntitySnapshot Without(EntitySnapshot entity, params string[] keys) => entity with { Tags = entity.Tags.Where(pair => !keys.Contains(pair.Key)).ToFrozenDictionary() };
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static IReadOnlyDictionary<string, CardRecord> Cards()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "discovery-play-255790.json")));
        Check(input.RootElement.GetProperty("build").GetString() == "255790", "Unpinned discovery fixture.");
        return input.RootElement.GetProperty("cards").EnumerateArray().ToDictionary(raw => raw.GetProperty("id").GetString()!, raw =>
            new CardRecord(raw.GetProperty("id").GetString()!, raw.GetProperty("dbfId").GetInt32(), raw.GetProperty("name").GetString()!,
                raw.GetProperty("text").GetString()!, raw.GetProperty("cardClass").GetString()!, raw.GetProperty("set").GetString()!, raw.GetProperty("type").GetString()!,
                raw.TryGetProperty("cost", out var cost) ? cost.GetInt32() : null, raw.TryGetProperty("collectible", out var collectible) && collectible.GetBoolean(), [], [], raw.GetRawText()), StringComparer.Ordinal);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
