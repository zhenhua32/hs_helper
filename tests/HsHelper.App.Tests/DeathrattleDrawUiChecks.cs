using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.Agent;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Coaching;
using HsHelper.Core;
using HsHelper.Storage;

/// <summary>Real current-body controls and final advice, without simulating drawn card identities.</summary>
internal static class DeathrattleDrawUiChecks
{
    private const string Build = "255790";
    private const string Gift = "EDR_100t4e";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Step[] DeathThenFace = [new(30, 40), new(31, 21)];

    public static void RunOnDispatcher(MainWindow unused)
    {
        var directory = Path.Combine(Workspace(), ".local", "deathrattle-draw-app", "data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var launch = typeof(App).GetProperty(nameof(App.LaunchSettings))!; var previous = launch.GetValue(null);
        launch.SetValue(null, new AppLaunchSettings(directory, false, null, null));
        var window = new MainWindow(); using var handler = new AdviceHandler(); using var http = new HttpClient(handler);
        using var rejectCards = new RejectCardsHandler(); using var cardHttp = new HttpClient(rejectCards);
        try
        {
            ((HttpClient)Field(window, "_http")!).Dispose(); Set(window, "_http", http);
            ((HttpClient)Field(window, "_cardHttp")!).Dispose(); Set(window, "_cardHttp", cardHttp);
            var cards = FixtureCards(); var catalog = (CardCatalog)Field(window, "_catalog")!;
            Await(catalog.InitializeAsync()); Import(catalog, cards, Build);
            var decoy = (JsonArray)cards.DeepClone(); foreach (var card in decoy) card!["name"] = "WRONG_DEATHRATTLE_BUILD";
            Import(catalog, decoy, "999999");
            var store = new SessionStore(Path.Combine(directory, "sessions.db")); Await(store.InitializeAsync()); Set(window, "_store", store);
            var archive = new AdviceArchiveStore(Path.Combine(directory, "advice.db")); Await(archive.InitializeAsync()); Set(window, "_adviceArchive", archive);
            var traces = new AnalysisTraceStore(Path.Combine(directory, "traces.db")); Await(traces.InitializeAsync()); Set(window, "_traces", traces);
            var knowledge = new CoachKnowledgeStore(Path.Combine(directory, "knowledge.db")); Await(knowledge.InitializeAsync()); Set(window, "_knowledge", knowledge);
            C<TextBox>(window, "EndpointBox").Text = "https://deathrattle-draw-ui.invalid/v1/chat/completions";
            C<TextBox>(window, "ModelBox").Text = "deathrattle-draw-ui"; C<PasswordBox>(window, "KeyBox").Password = "fixture-deathrattle-draw-key";
            Set(window, "_loadedModelSettings", new ModelSettings(new("https://deathrattle-draw-ui.invalid/v1/chat/completions"), "deathrattle-draw-ui", 2)
                { EnableTools = true, BudgetLimits = new(MaxTokensPerMatch: 1000000) });
            C<TextBox>(window, "SessionBuildBox").Text = Build; C<ComboBox>(window, "TaskBox").SelectedIndex = 2;

            var full = Frame("full-hand", hand: 10, deck: 2); var original = SnapshotCodec.Serialize(full);
            PinSaved(window, store, full); Await(Invoke(window, "RenderBattleAsync"));
            var battle = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
            Check(battle.Local.Board.Any(card => card.EntityId == 30 && card.Zone == "PLAY" && card.Health == 1 && card.Status.Contains("亡语", StringComparison.Ordinal)) &&
                !battle.Local.Board.Single(card => card.EntityId == 30).Name.Contains("WRONG_DEATHRATTLE_BUILD", StringComparison.Ordinal),
                "The actual battle view lost its current body/health/observed numeric deathrattle or used the active decoy build.");
            var tools = Tools(window); var fullPreview = Preview(tools);
            Check(fullPreview["Kind"]?.ToString() == "DeathrattleOrdinary" && fullPreview["SourceEntityId"]?.GetValue<int>() == 30 &&
                fullPreview["TriggerEntityId"]?.GetValue<int>() == 30 && fullPreview["SourceCardId"]?.ToString() == "EDR_852" &&
                fullPreview["EnchantmentEntityId"]?.GetValue<int>() == 50 && fullPreview["EnchantmentCardId"]?.ToString() == Gift,
                "The actual button bound the enchantment or an arbitrary entity instead of the current own PLAY body.");
            Exact(fullPreview, "RequestedDraws", 2); Exact(fullPreview, "CardsRemovedFromDeck", 2); Exact(fullPreview, "CardsAddedToHand", 0);
            Exact(fullPreview, "CardsBurned", 2); Exact(fullPreview, "HandCountAfter", 10); Exact(fullPreview, "DeckCountAfter", 0);
            Check(tools.ResultText.Contains("亡语抽牌直接后果预览", StringComparison.Ordinal) && tools.ResultText.Contains("不会杀死随从", StringComparison.Ordinal) &&
                tools.ResultText.Contains("入手 0；满手损失 2", StringComparison.Ordinal) && !tools.ResultText.Contains("成功离手", StringComparison.Ordinal) &&
                !tools.ResultText.Contains("WRONG_DEATHRATTLE_BUILD", StringComparison.Ordinal),
                "The Chinese preview hid the two burned cards or incorrectly freed a hand slot: " + tools.ResultText);
            Check(SnapshotCodec.Serialize(full) == original && full.Entities[30].Zone == "PLAY" && full.Entities.Values.Count(entity => entity.Zone == "HAND" && entity.ControllerId == 1) == 10,
                "Preview killed the body, created a drawn hand entity, or mutated the original frame.");
            var opponentTurn = Frame("opponent-turn-risk", hand: 10, deck: 2, opponentTurn: true);
            PinSaved(window, store, opponentTurn); var opponentOriginal = SnapshotCodec.Serialize(opponentTurn); tools = Tools(window); var offTurn = Preview(tools);
            Check(opponentTurn.ActivePlayerId == 2 && offTurn["ResolvesEffects"]?.GetValue<bool>() == true &&
                offTurn["SourceEntityId"]?.GetValue<int>() == 30, "The hypothetical deathrattle preview was incorrectly restricted to an own-turn attack.");
            Exact(offTurn, "CardsBurned", 2); Check(opponentOriginal == SnapshotCodec.Serialize(opponentTurn) && opponentTurn.Entities[30].Zone == "PLAY",
                "The opponent-turn risk preview executed a death or changed the current body.");
            Pass("actual current-PLAY source button preserves a full hand and shows two burned cards with fixed-build body/enchantment provenance");

            foreach (var immune in new[] { false, true })
            {
                var frame = Frame("fatigue-" + immune, hand: 9, deck: 1, armor: 2, immune: immune);
                PinSaved(window, store, frame); tools = Tools(window); var draw = Preview(tools);
                Exact(draw, "RequestedDraws", 2); Exact(draw, "CardsAddedToHand", 1); Exact(draw, "CardsBurned", 0);
                Exact(draw, "EmptyDeckAttempts", 1); Exact(draw, "FatigueAfter", 3); Exact(draw, "FatigueDamage", immune ? 0 : 3);
                Exact(draw, "HandCountAfter", 10); Exact(draw, "HeroHealthAfter", immune ? 20 : 19); Exact(draw, "HeroArmorAfter", immune ? 2 : 0);
                Check(tools.ResultText.Contains("入手 1；满手损失 0", StringComparison.Ordinal) &&
                    tools.ResultText.Contains("直接疲劳伤害 " + (immune ? "0" : "3"), StringComparison.Ordinal) &&
                    tools.ResultText.Contains("英雄生命 " + (immune ? "20" : "19") + " / 护甲 " + (immune ? "2" : "0"), StringComparison.Ordinal),
                    "The direct preview omitted current armor/immunity or changed deathrattle into a play-from-hand draw: " + tools.ResultText);
            }
            Pass("one remaining deck card adds exactly one card and then applies the third fatigue packet with observed armor and immunity");

            var lineFrame = Frame("death-line", hand: 9, deck: 1, armor: 2); var cutoff = PinSaved(window, store, lineFrame);
            tools = Tools(window); Author(tools, DeathThenFace.Take(1)); var prefix = Evaluate(tools);
            Check(prefix["Verification"]?.ToString() == "Conditional" && prefix["AppliedActions"]?.GetValue<int>() == 1 &&
                prefix["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == true && prefix["IsLethal"]?.GetValue<bool>() != true,
                "The single covered combat prefix did not expose its unresolved direct-draw boundary.");
            Author(tools, DeathThenFace.Skip(1)); var line = Evaluate(tools);
            Check(line["Verification"]?.ToString() == "Unsupported" && line["AppliedActions"]?.GetValue<int>() == 1 &&
                line["IsLethal"]?.GetValue<bool>() != true && line["Result"]?["DrawBoundaryReached"]?.GetValue<bool>() == true,
                "Actual combat did not stop after the first death/draw boundary: " + line);
            var linkedDraw = line["Result"]!["DrawPreviews"]!.AsArray().Single()!;
            Exact(linkedDraw, "CardsAddedToHand", 1); Exact(linkedDraw, "FatigueDamage", 3); Exact(linkedDraw, "HeroHealthAfter", 19);
            var entities = line["Result"]!["Entities"]!.AsArray();
            Check(linkedDraw["TriggerEntityId"]?.GetValue<int>() == 30 && linkedDraw["EnchantmentEntityId"]?.GetValue<int>() == 50 &&
                !entities.Any(entity => entity!["EntityId"]!.GetValue<int>() is 30 or 40) &&
                entities.Single(entity => entity!["EntityId"]!.GetValue<int>() == 21)!["RemainingHealth"]!.GetValue<int>() == 3 &&
                entities.Single(entity => entity!["EntityId"]!.GetValue<int>() == 11)!["RemainingHealth"]!.GetValue<int>() == 20 &&
                entities.Single(entity => entity!["EntityId"]!.GetValue<int>() == 11)!["Armor"]!.GetValue<int>() == 2 &&
                entities.Where(entity => entity?["Zone"]?.ToString() == "HAND").Select(entity => entity!["EntityId"]!.GetValue<int>()).Order()
                    .SequenceEqual(lineFrame.Entities.Values.Where(entity => entity.Zone == "HAND" && entity.ControllerId == 1).Select(entity => entity.Id).Order()) &&
                tools.ResultText.Contains("后续动作须等待新局面", StringComparison.Ordinal) && !tools.ResultText.Contains("已验证斩杀：是", StringComparison.Ordinal),
                "A draw preview became a new hand, a future action, or a verified lethal result.");
            handler.Prepare(DeathThenFace, "DEATHRATTLE_DRAW_UI_BOUNDARY"); Await(Invoke(window, "AnalyzeCurrentAsync"));
            var saved = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == lineFrame.MatchId);
            Check(saved.Advice.Verification == "unsupported" && saved.Advice.Actions.Count == 0 && !saved.Advice.VerifiedLethal &&
                saved.Advice.KnowledgeAsOf == cutoff && saved.Advice.CardBuild == Build && saved.Advice.StateVersion == lineFrame.StateVersion &&
                saved.Advice.EventSequence == lineFrame.EventSequence && saved.Advice.HostRisks is { Count: > 0 } &&
                C<TextBox>(window, "AdviceBox").Text.Contains("规则未覆盖（具体动作已移除）", StringComparison.Ordinal) &&
                !C<TextBox>(window, "AdviceBox").Text.Contains("本地规则确认此序列可斩杀", StringComparison.Ordinal),
                "Final host advice or its durable archive trusted the model's forged post-draw action/lethal claim.");
            Check(handler.LastEvaluation!["AppliedActions"]!.GetValue<int>() == 1 && handler.LastEvaluation["Result"]!["DrawBoundaryReached"]!.GetValue<bool>(),
                "The actual fake-HTTP tool round did not use the same real death/draw boundary.");
            ArchiveRender(archive, saved); Check(SnapshotCodec.Serialize(lineFrame) == SnapshotCodec.Serialize((GameSnapshot)Field(tools, "_snapshot")!), "The candidate line mutated the bound source frame.");
            Pass("actual authored combat death, final host and reopened archive retain one applied action and the draw boundary without new hand entities or verified lethal");

            foreach (var scope in new[] { "HAND", "DECK", "SETASIDE", "ENEMY_PLAY", "unknown-attachment", "suppressed" })
            {
                var frame = Frame("denied-" + scope, hand: 9, deck: 1, scope: scope, opponentTurn: scope == "ENEMY_PLAY"); PinSaved(window, store, frame); tools = Tools(window);
                if (scope == "suppressed")
                {
                    Await(Invoke(window, "RenderBattleAsync")); var current = (BattleViewState)C<FrameworkElement>(window, "BattleViewRoot").DataContext;
                    Check(!current.Local.Board.Single(card => card.EntityId == 30).Status.Contains("亡语", StringComparison.Ordinal), "An explicit observed numeric deathrattle zero was shown as an active keyword.");
                }
                C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1;
                if (HasSource(tools, 30))
                {
                    Select(C<ComboBox>(tools, "LocalSourceBox"), 30);
                    var button = C<Button>(tools, "LocalDeathrattleDrawButton");
                    if (button.IsEnabled)
                    {
                        var denied = Preview(tools);
                        Check(denied["ResolvesEffects"]?.GetValue<bool>() != true || denied["RequestedDraws"]?["Maximum"]?.GetValue<long>() == 0,
                            "An unknown or suppressed attachment acquired an active exact draw preview: " + scope);
                    }
                }
                else Check(scope is "HAND" or "DECK" or "SETASIDE" or "ENEMY_PLAY", "A confirmed own body disappeared for a non-privacy reason: " + scope);
                var coach = (SnapshotCoach)Field(tools, "_coach")!;
                var forged = Await(coach.ExecuteAsync("evaluate_deathrattle_draw", new() { ["source_entity_id"] = 30 }, CancellationToken.None));
                Check(forged["error"] is not null || forged["ResolvesEffects"]?.GetValue<bool>() != true || forged["RequestedDraws"]?["Maximum"]?.GetValue<long>() == 0,
                    "The host accepted a forged private/opponent/unknown/suppressed source as active exact draw.");
                if (scope is "HAND" or "DECK" or "SETASIDE") Check(!forged.ToJsonString().Contains(Gift, StringComparison.Ordinal) &&
                    !forged.ToJsonString().Contains("EDR_852", StringComparison.Ordinal) &&
                    !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey(Gift) &&
                    !((IReadOnlyDictionary<string, CardRecord>)Field(tools, "_cards")!).ContainsKey("EDR_852"), "A private opponent body/attachment identity entered the calculator result or metadata.");
            }
            var bad = new CardCatalog(Path.Combine(directory, "wrong-definition.db")); Await(bad.InitializeAsync());
            var wrong = (JsonArray)cards.DeepClone(); wrong.Single(card => card!["id"]!.ToString() == Gift)!["dbfId"] = 1; Import(bad, wrong, Build);
            var invalidTools = new LocalToolsWindow(Frame("wrong-definition", 9, 1), Build, bad, true);
            try { Await(invalidTools.BindingReady); var denied = Preview(invalidTools); Check(denied["ResolvesEffects"]?.GetValue<bool>() != true, "A wrong fixed ENCH definition produced an exact active draw."); }
            finally { invalidTools.Close(); }
            (Field(window, "_localTools") as LocalToolsWindow)?.Close(); Set(window, "_catalog", bad);
            try
            {
                var badFrame = Frame("wrong-definition-analysis", 9, 1); PinSaved(window, store, badFrame);
                handler.Prepare(DeathThenFace, "DEATHRATTLE_DRAW_UI_UNAUDITED"); Await(Invoke(window, "AnalyzeCurrentAsync"));
                var refused = Await(archive.ListAsync()).Single(record => record.Advice.MatchId == badFrame.MatchId);
                Check(refused.Advice.Verification == "unsupported" && refused.Advice.Actions.Count == 0 && !refused.Advice.VerifiedLethal &&
                    handler.LastEvaluation!["AppliedActions"]!.GetValue<int>() == 0 &&
                    C<TextBox>(window, "AdviceBox").Text.Contains("规则未覆盖（具体动作已移除）", StringComparison.Ordinal),
                    "A provider's forged verified answer bypassed fixed ENCH metadata at final host/main/archive.");
                ArchiveRender(archive, refused);
            }
            finally { Set(window, "_catalog", catalog); }
            foreach (var close in new[] { false, true })
            {
                var frame = Frame("late-" + close, 10, 2); var pending = new LocalToolsWindow(frame, Build, catalog, true); var completed = false; string? replacement = null;
                try
                {
                    Await(pending.BindingReady); C<ComboBox>(pending, "LocalKindBox").SelectedIndex = 1; Select(C<ComboBox>(pending, "LocalSourceBox"), 30);
                    Set(Field(pending, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
                    {
                        Check(observation.Name == "evaluate_deathrattle_draw" && observation.Success &&
                            JsonNode.Parse(observation.ResultJson!)!["CardsBurned"]!["Minimum"]!.GetValue<long>() == 2, "The lifecycle gate did not follow the real complete preview.");
                        if (close) pending.Close(); else pending.UpdateBinding(Frame("replacement", 9, 1), Build, true);
                        replacement = pending.ResultText; completed = true;
                    }));
                    Click(C<Button>(pending, "LocalDeathrattleDrawButton")); WaitUntil(() => completed && !pending.HasPendingCalculation, "The real-preview lifecycle gate did not finish.");
                    Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task); if (!close) Await(pending.BindingReady);
                    Check(pending.ResultText == replacement && !pending.ResultText.Contains("满手损失 2", StringComparison.Ordinal) &&
                        (close ? !pending.IsBindingValid : pending.BoundMatchId != frame.MatchId), "An old successful draw preview crossed a rebind/closed-window boundary."); Passive(pending);
                }
                finally { if (!close || !completed) pending.Close(); }
            }
            Check(rejectCards.Calls == 0, "The deathrattle-draw UI attempted a card download instead of using its fixed local fixture."); Passive(window);
            Pass("private opposing sources, unknown attachments, suppressed deathrattle and bad metadata never become active exact previews; old completed previews cannot cross rebind or close");
        }
        finally { window.Close(); launch.SetValue(null, previous); }
    }

    private static JsonArray FixtureCards()
    {
        var wanted = new HashSet<string>(["EDR_852", "CS2_172", "HERO_05", "HERO_08", Gift], StringComparer.Ordinal);
        var cards = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var fixture in new[] { "deathrattle-draw-255790.json", "native-definitions-255790.json", "terror-end-turn-255790.json", "dark-gift-static-255790.json" })
        {
            var source = JsonNode.Parse(File.ReadAllText(Path.Combine(Workspace(), "tests", "HsHelper.Rules.Tests", "Fixtures", fixture)))!;
            Check(source["build"]?.ToString() == Build, "A deathrattle-draw UI fixture did not bind build255790.");
            foreach (var card in source["cards"]!.AsArray()) if (wanted.Contains(card!["id"]!.ToString())) cards.TryAdd(card["id"]!.ToString(), card.DeepClone());
        }
        Check(wanted.SetEquals(cards.Keys), "A public fixed body, enchantment or hero fixture was missing."); return new(cards.Values.ToArray());
    }

    private static GameSnapshot Frame(string suffix, int hand, int deck, int armor = 0, bool immune = false, string? scope = null, bool opponentTurn = false)
    {
        var engine = new PowerLogStateEngine(1, "deathrattle-draw-ui-" + suffix);
        void Feed(params string[] lines) { foreach (var line in lines) engine.ProcessLine(line); }
        Feed("GameState.DebugPrintGame() - BuildNumber=255790 FormatType=FT_STANDARD GameType=GT_RANKED", "CREATE_GAME", "GameEntity EntityID=1", "tag=STATE value=RUNNING", "tag=TURN value=0",
            "Player EntityID=2 PlayerID=1", "tag=CONTROLLER value=1", $"tag=CURRENT_PLAYER value={(opponentTurn ? 0 : 1)}", "tag=HERO_ENTITY value=11", "tag=RESOURCES value=10", "tag=RESOURCES_USED value=0", "tag=TEMP_RESOURCES value=0",
            $"tag=NUM_CARDS_IN_HAND value={hand}", $"tag=NUM_CARDS_IN_DECK value={deck}", "tag=MAXHANDSIZE value=10", "tag=FATIGUE value=2",
            "Player EntityID=3 PlayerID=2", "tag=CONTROLLER value=2", $"tag=CURRENT_PLAYER value={(opponentTurn ? 1 : 0)}", "tag=HERO_ENTITY value=21");
        foreach (var (id, card, owner, cls) in new[] { (11, "HERO_05", 1, "HUNTER"), (21, "HERO_08", 2, "MAGE") })
            Feed($"FULL_ENTITY - Creating ID={id} CardID={card}", $"tag=CONTROLLER value={owner}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=HERO", $"tag=CLASS value={cls}",
                $"tag=HEALTH value={(id == 11 ? 30 : 3)}", $"tag=DAMAGE value={(id == 11 ? 10 : 0)}", $"tag=ARMOR value={(id == 11 ? armor : 0)}", "tag=ATK value=0", $"tag=IMMUNE value={(id == 11 && immune ? 1 : 0)}");
        var privateTarget = scope is "HAND" or "DECK" or "SETASIDE"; var owner30 = privateTarget || scope == "ENEMY_PLAY" ? 2 : 1;
        Feed("FULL_ENTITY - Creating ID=30 CardID=EDR_852", $"tag=CONTROLLER value={owner30}", $"tag=ZONE value={(privateTarget ? scope : "PLAY")}", "tag=CARDTYPE value=MINION", "tag=COST value=2", "tag=ATK value=3", "tag=HEALTH value=1", "tag=DAMAGE value=0",
            "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=NUM_ATTACKS_THIS_TURN value=0", $"tag=217 value={(scope == "suppressed" ? 0 : 1)}");
        Feed("FULL_ENTITY - Creating ID=31 CardID=CS2_172", "tag=CONTROLLER value=1", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=3", "tag=HEALTH value=4", "tag=DAMAGE value=0", "tag=EXHAUSTED value=0", "tag=NUM_TURNS_IN_PLAY value=1", "tag=NUM_ATTACKS_THIS_TURN value=0");
        Feed("FULL_ENTITY - Creating ID=40 CardID=CS2_172", "tag=CONTROLLER value=2", "tag=ZONE value=PLAY", "tag=CARDTYPE value=MINION", "tag=ATK value=1", "tag=HEALTH value=3", "tag=DAMAGE value=0");
        for (var index = 0; index < hand; index++) Feed($"FULL_ENTITY - Creating ID={100 + index} CardID=CS2_172", "tag=CONTROLLER value=1", "tag=ZONE value=HAND", "tag=CARDTYPE value=MINION", "tag=COST value=2", "tag=ATK value=3", "tag=HEALTH value=2", "tag=DAMAGE value=0");
        for (var index = 0; index < deck; index++) Feed($"FULL_ENTITY - Creating ID={200 + index} CardID=", "tag=CONTROLLER value=1", "tag=ZONE value=DECK");
        Feed($"FULL_ENTITY - Creating ID=50 CardID={Gift}", $"tag=CONTROLLER value={owner30}", "tag=ZONE value=PLAY", "tag=CARDTYPE value=ENCHANTMENT", $"tag=ATTACHED value={(scope == "unknown-attachment" ? 999 : 30)}");
        Feed("TAG_CHANGE Entity=1 tag=TURN value=1", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_READY", "TAG_CHANGE Entity=1 tag=STEP value=MAIN_ACTION", "PowerProcessor.EndCurrentTaskList() - complete");
        var result = engine.Snapshot();
        Check(result.Quality.HasLocalPerspective && (scope == "unknown-attachment" || result.Quality.IsStable && result.Quality.IsComplete), "The real parser fixture did not establish its declared current scope."); return result;
    }

    private static JsonObject Preview(LocalToolsWindow tools)
    {
        C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1; Select(C<ComboBox>(tools, "LocalSourceBox"), 30);
        var button = C<Button>(tools, "LocalDeathrattleDrawButton"); Check(button.Content?.ToString() == "预览所选随从亡语抽牌", "The actual deathrattle preview button was not present.");
        JsonObject? result = null; Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation =>
        { Check(observation.Name == "evaluate_deathrattle_draw" && observation.Success, "The actual button did not call the deathrattle-draw host."); result = JsonNode.Parse(observation.ResultJson!)!.AsObject(); }));
        Click(button); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "The actual deathrattle preview did not settle."); return result!;
    }
    private static JsonObject Evaluate(LocalToolsWindow tools)
    {
        JsonObject? result = null; Set(Field(tools, "_coach")!, "_toolCompleted", (Action<CoachToolObservation>)(observation => result = JsonNode.Parse(observation.ResultJson!)!.AsObject()));
        Click(C<Button>(tools, "LocalEvaluateButton")); WaitUntil(() => result is not null && !tools.HasPendingCalculation, "The actual candidate line did not settle."); return result!;
    }
    private static void Author(LocalToolsWindow tools, IEnumerable<Step> steps)
    {
        C<ComboBox>(tools, "LocalKindBox").SelectedIndex = 1;
        foreach (var step in steps) { Select(C<ComboBox>(tools, "LocalSourceBox"), step.Source); Select(C<ComboBox>(tools, "LocalTargetBox"), step.Target); Click(C<Button>(tools, "LocalAddActionButton")); }
    }
    private static DateTimeOffset PinSaved(MainWindow window, SessionStore store, GameSnapshot frame)
    {
        var recorder = new SessionRecorder(store, new(null, Build, 1, PlayerClass: "HUNTER")); recorder.Observe(frame, 1); Await(recorder.CompleteAsync());
        var point = Await(store.ListDecisionPointsAsync(frame.MatchId, true)).Single();
        Set(window, "_historyContext", new HistoricalContext(frame.MatchId, Build, null, null, frame, [])); Set(window, "_historyObservedAt", point.RecordedAt); Set(window, "_liveFrame", frame); return point.RecordedAt;
    }
    private static void ArchiveRender(AdviceArchiveStore store, ArchivedAdviceRecord saved)
    {
        var browser = new AdviceArchiveWindow(store, new(saved.Advice.MatchId, "合成亡语抽牌局面", AdviceArchiveScopeKind.Session));
        try
        {
            Await(browser.InitializeAsync()); C<ListBox>(browser, "ArchiveRecordsList").SelectedIndex = 0;
            var text = C<TextBox>(browser, "ArchiveDetailText").Text;
            Check(browser.SelectedRecord?.Id == saved.Id && text.Contains(saved.Advice.Summary, StringComparison.Ordinal) && text.Contains(Build, StringComparison.Ordinal) &&
                text.Contains("原验证状态：" + saved.Advice.Verification, StringComparison.Ordinal) && saved.Advice.HostRisks!.Any(risk => text.Contains(risk, StringComparison.Ordinal)), "Archive rendering lost the saved real-host draw limitation."); Passive(browser);
        }
        finally { browser.Close(); }
    }
    private static void Exact(JsonNode value, string field, long expected) => Check(value[field]?["Minimum"]?.GetValue<long>() == expected && value[field]?["Maximum"]?.GetValue<long>() == expected, field + " was not exactly " + expected + ": " + value[field]);
    private static LocalToolsWindow Tools(MainWindow window) { var tools = (LocalToolsWindow)InvokeObject(window, "BindLocalToolsWindow")!; Await(tools.BindingReady); return tools; }
    private static void Import(CardCatalog catalog, JsonArray cards, string build) { using var input = new MemoryStream(Encoding.UTF8.GetBytes(cards.ToJsonString())); Await(catalog.ImportAsync(input, build, source: "fixture://deathrattle-draw-ui")); }
    private static string Workspace() { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName; throw new FileNotFoundException("The deathrattle-draw fixture was not found."); }
    private static bool HasSource(LocalToolsWindow tools, int id) => C<ComboBox>(tools, "LocalSourceBox").Items.Cast<object>().Any(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static void Select(ComboBox box, int id) => box.SelectedItem = box.Items.Cast<object>().Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
    private static T C<T>(FrameworkElement element, string name) where T : class => element.FindName(name) as T ?? throw new InvalidOperationException("Missing " + name);
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, Private)!.GetValue(instance);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetField(name, Private)!.SetValue(instance, value);
    private static object? InvokeObject(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Private)!.Invoke(instance, args);
    private static Task Invoke(object instance, string name, params object?[] args) => (Task)InvokeObject(instance, name, args)!;
    private static void Click(Button button) { Check(button.IsEnabled, "An actual test button was disabled: " + button.Content); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)); }
    private static void Passive(Window window) => Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "A deathrattle-draw regression showed a window or created a native handle.");
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { WaitUntil(() => task.IsCompleted, "Deathrattle-draw UI operation timed out."); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> ready, string error)
    {
        if (ready()) return; var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || watch.Elapsed > TimeSpan.FromSeconds(15)) frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), error);
    }
    private static void Check(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private static void Pass(string message) => Console.WriteLine("PASS deathrattle draw UI: " + message);
    private sealed record Step(int Source, int Target);

    private sealed class RejectCardsHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Unexpected card download in the isolated deathrattle-draw UI fixture."); }
    }

    private sealed class AdviceHandler : HttpMessageHandler
    {
        private Step[] _steps = []; private string _summary = ""; public JsonObject? LastEvaluation { get; private set; }
        public void Prepare(Step[] steps, string summary) { _steps = steps; _summary = summary; LastEvaluation = null; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            var tool = json["messages"]!.AsArray().LastOrDefault(message => message?["role"]?.ToString() == "tool");
            var actions = _steps.Select(step => new { kind = "attack", source_entity_id = step.Source, target_entity_id = (int?)step.Target }).ToArray();
            if (tool is null) return Response(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null,
                tool_calls = new[] { new { id = "deathrattle_draw_ui_line", type = "function", function = new { name = "evaluate_line", arguments = JsonSerializer.Serialize(new { actions }) } } } } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
            LastEvaluation = JsonNode.Parse(tool["content"]!.GetValue<string>())!.AsObject();
            return Response(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = JsonSerializer.Serialize(new {
                summary = _summary, reasons = Array.Empty<string>(), alternative = (string?)null, risks = Array.Empty<string>(), assumptions = Array.Empty<string>(), unknowns = Array.Empty<string>(), unsupported_mechanics = Array.Empty<string>(),
                actions, verification = "verified", verified_lethal = true }) } } }, usage = new { prompt_tokens = 7, completion_tokens = 3, total_tokens = 10 } });
        }
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
