using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using HsHelper.App;
using HsHelper.Cards;
using HsHelper.Core;

public static class AssembledBattleViewChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("battle rows retain distinct assembled text for entities sharing one card id", PerEntityText);
        yield return ("unresolved assembled text never falls back to the base card effect", MissingText);
        yield return ("assembled text needs both pinned and observed build 255790", BuildBinding);
        yield return ("assembled display keeps current observed costs and attributes", ObservedStatistics);
        yield return ("offered Assemble halves remain partial candidates rather than completed cards", OfferedHalf);
        yield return ("assembled donor prefetch excludes hidden and unidentified private entities", DonorPrivacy);
        yield return ("stale or conflicting choice identities do not query donor card data", ChoiceBinding);
        yield return ("assembled view snapshots preserve past text and global catalog records", ImmutableText);
        yield return ("actual battle grids and tooltips bind per-entity effective text", ActualPanelTemplate);
    }

    private static void PerEntityText()
    {
        var view = BattleViewBuilder.Build(Frame(), Cards(), Build);
        var first = view.Local.Hand.Single(card => card.EntityId == 30); var second = view.Local.Hand.Single(card => card.EntityId == 31);
        Check(first.CardId == second.CardId && first.IsAssembled && second.IsAssembled && !first.IsAssembledOffer &&
            first.EffectiveText == "触发甲 效果乙" && second.EffectiveText == "触发乙 效果甲" && first.EffectTextStatus.Contains("已观察", StringComparison.Ordinal),
            "Same base id overwrote one instance's assembled text with another.");
        Check(view.Opponent.Board.Single().EffectiveText == "触发甲 效果乙", "Observed public opponent text was not projected.");
    }
    private static void MissingText()
    {
        var cards = Cards(); cards.Remove("DONOR_B"); var view = BattleViewBuilder.Build(Frame(), cards, Build);
        var card = view.Local.Hand[0];
        Check(card.IsAssembled && card.EffectiveText is null && card.EffectTextDisplay == "当前效果未知" &&
            card.EffectTextStatus.Contains("未知", StringComparison.Ordinal) && !card.EffectTextDisplay.Contains("基础效果", StringComparison.Ordinal),
            "Missing donor was replaced with a native effect.");
        var incomplete = Replace(Frame(), Entity(30, "HAND", 1, ("4572", 1001)));
        Check(BattleViewBuilder.Build(incomplete, Cards(), Build).Local.Hand.Single(card => card.EntityId == 30).EffectiveText is null,
            "A missing companion half was treated as an explicit base half.");
        var conflict = Replace(Frame(), Entity(30, "HAND", 1, ("4572", 1001), ("CRAFTABLE_FIRST_HALF_DBID", 1002), ("4573", 0)));
        Check(BattleViewBuilder.Build(conflict, Cards(), Build).Local.Hand.Single(card => card.EntityId == 30).EffectiveText is null,
            "Contradictory assembled markers fell back to ordinary text.");
    }
    private static void BuildBinding()
    {
        foreach (var (observed, pinned) in new (string?, string?)[] { (null, Build), ("other", Build), (Build, "other"), (Build, null) })
        {
            var frame = Frame() with { ClientBuild = observed }; var view = BattleViewBuilder.Build(frame, Cards(), pinned);
            Check(view.Local.Hand.All(card => card.EffectiveText is null && card.IsAssembled) && BattleViewBuilder.VisibleEffectDbfIds(frame, pinned).Count == 0,
                "Unconfirmed or mismatched client version reused native custom text semantics.");
        }
    }
    private static void ObservedStatistics()
    {
        var frame = Replace(Frame(), Entity(30, "HAND", 1, ("4572", 1001), ("4573", 1002), ("COST", 1), ("ATK", 7), ("HEALTH", 9), ("DAMAGE", 2)));
        var card = BattleViewBuilder.Build(frame, Cards(), Build).Local.Hand.Single(card => card.EntityId == 30);
        Check(card.Cost == 1 && card.Attack == 7 && card.Health == 7, "Donor or base print statistics replaced current observed values.");
        var unknown = BattleViewBuilder.Build(Frame(), Cards(), Build).Local.Hand.Single(card => card.EntityId == 30);
        Check(unknown.Cost is null && unknown.Attack is null && unknown.Health is null, "Unobserved stats were filled from a donor or catalog.");
    }
    private static void OfferedHalf()
    {
        var frame = ChoiceFrame(); var choice = BattleViewBuilder.Build(frame, Cards(), Build).Choice!;
        Check(choice.Options.Single() is { IsAssembled: true, IsAssembledOffer: true, EffectiveText: "效果乙" } option &&
            option.EffectTextStatus.Contains("效果半段", StringComparison.Ordinal) && option.EffectTextStatus.Contains("尚未合成", StringComparison.Ordinal),
            "An offered half became a complete current minion effect.");
        var trigger = Replace(frame, Entity(80, "SETASIDE", 1, ("4572", 1002), ("4573", 1)) with { CardId = "DONOR_B" });
        Check(BattleViewBuilder.Build(trigger, Cards(), Build).Choice!.Options.Single().EffectiveText == "触发乙", "Offered trigger half was swapped for an effect half.");
    }
    private static void DonorPrivacy()
    {
        var frame = Frame(); var visible = BattleViewBuilder.VisibleEffectDbfIds(frame, Build);
        Check(visible.SequenceEqual([1001, 1002]) && !visible.Contains(9999), "Hidden hand or deck donor ids entered a catalog query.");
        var spectator = BattleViewBuilder.Build(frame with { IsSpectating = true }, Cards(), Build);
        Check(spectator.Local.Hand.Count == 0 && !JsonSerializer.Serialize(spectator).Contains("HIDDEN_DONOR", StringComparison.Ordinal), "Spectator view exposed private assembled effects.");
        var hiddenOnly = frame with { Entities = frame.Entities.Where(pair => pair.Value.Zone != "PLAY" && pair.Value.Id is not (30 or 31)).ToFrozenDictionary() };
        Check(BattleViewBuilder.VisibleEffectDbfIds(hiddenOnly with { IsSpectating = true }, Build).Count == 0, "Private references were prefetched while spectating.");
    }
    private static void ChoiceBinding()
    {
        var frame = Replace(ChoiceFrame(), Entity(80, "SETASIDE", 1, ("4572", 1), ("4573", 1003)) with { CardId = "DONOR_C" });
        frame = frame with { Choice = frame.Choice! with { Options = [new(80, "DONOR_C", 1, 0)] } };
        Check(BattleViewBuilder.VisibleEffectDbfIds(frame, Build).Contains(1003), "Confirmed own candidate donor was omitted.");
        foreach (var invalid in new[] { frame with { Phase = GamePhase.Main }, frame with { IsSpectating = true },
            frame with { Choice = frame.Choice! with { PlayerId = 2 } }, Replace(frame, frame.Entities[80] with { CardId = null }) })
        {
            var refs = BattleViewBuilder.VisibleEffectDbfIds(invalid, Build);
            // Frame's hand and public board references are unrelated to this special candidate marker.
            Check(!refs.Contains(1003), "A stale, opposing or unidentified choice entered donor prefetch.");
        }
        var noIdentity = Replace(frame, frame.Entities[80] with { CardId = null });
        Check(BattleViewBuilder.Build(noIdentity, Cards(), Build).Choice!.Options.Single().EffectiveText is null,
            "A descriptor identity labelled another unidentified entity's custom tags.");
    }
    private static void ImmutableText()
    {
        var cards = Cards(); var text = cards[Base].Text; var frame = Frame(); var before = BattleViewBuilder.Build(frame, cards, Build);
        var replacement = Replace(frame, Entity(30, "HAND", 1, ("4572", 1002), ("4573", 1001)));
        _ = BattleViewBuilder.Build(replacement, cards, Build); cards["DONOR_A"] = cards["DONOR_A"] with { Text = "未来触发&未来效果" };
        Check(cards[Base].Text == text && before.Local.Hand.Single(card => card.EntityId == 30).EffectiveText == "触发甲 效果乙", "Projection overwrote shared base text or changed a captured row.");
    }
    private static void ActualPanelTemplate()
    {
        Exception? failure = null; var previous = Environment.GetEnvironmentVariable("HSHELPER_DATA_DIR");
        var root = Path.Combine(FindWorkspace(), ".local", "assembled-app-smoke", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", root);
        try
        {
            var thread = new Thread(() =>
            {
                MainWindow? window = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    window = new MainWindow();
                    var catalog = (CardCatalog)typeof(MainWindow).GetField("_catalog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                    AwaitUi(catalog.InitializeAsync());
                    using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Cards().Values.Select(card => new
                    { id = card.Id, dbfId = card.DbfId, name = card.Name, text = card.Text, type = card.Type, cardClass = card.CardClass, set = card.Set, cost = card.Cost })));
                    AwaitUi(catalog.ImportAsync(json, Build));
                    typeof(MainWindow).GetField("_liveFrame", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, Frame());
                    AwaitUi((Task)typeof(MainWindow).GetMethod("RenderBattleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!);
                    var view = (BattleViewState)((FrameworkElement)window.FindName("BattleViewRoot")).DataContext;
                    Check(view.Local.Hand.Single(card => card.EntityId == 30).EffectiveText == "触发甲 效果乙" &&
                        view.Local.Hand.Single(card => card.EntityId == 31).EffectiveText == "触发乙 效果甲", "Real RenderBattleAsync did not load donor DBF records for each instance.");
                    DrainBindings();
                    Check(((DataGrid)window.FindName("OwnHandGrid")).Items.Count == 2, "Actual hand grid did not receive both distinct instance rows.");
                    foreach (var name in new[] { "OwnHandGrid", "OwnBoardGrid", "OpponentBoardGrid", "CurrentChoicesGrid" })
                    {
                        var grid = (DataGrid)window.FindName(name);
                        Check(grid.Columns.OfType<DataGridTemplateColumn>().Single().CellTemplate == window.Resources["BattleEntityTextTemplate"], "An actual battle grid still displayed only a static card name.");
                    }
                    var template = (DataTemplate)window.Resources["BattleEntityTextTemplate"];
                    foreach (var card in view.Local.Hand)
                    {
                        var panel = (StackPanel)template.LoadContent(); panel.DataContext = card;
                        DrainBindings();
                        foreach (var block in panel.Children.OfType<TextBlock>()) BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.UpdateTarget();
                        Check(panel.Children.OfType<TextBlock>().Any(block => block.Text == card.EffectiveText), "Actual row template missed its instance text.");
                        var tooltip = (ToolTip)panel.ToolTip; tooltip.PlacementTarget = panel;
                        DrainBindings();
                        foreach (var block in ((StackPanel)tooltip.Content).Children.OfType<TextBlock>()) BindingOperations.GetBindingExpression(block, TextBlock.TextProperty)?.UpdateTarget();
                        Check(((StackPanel)tooltip.Content).Children.OfType<TextBlock>().Any(block => block.Text == card.EffectiveText), "Actual tooltip showed another entity or base text.");
                    }
                    Check(!window.IsVisible && new WindowInteropHelper(window).Handle == IntPtr.Zero, "Passive template smoke displayed a native window.");
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    if (window is not null)
                    {
                        typeof(MainWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                        window.Close();
                    }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Check(thread.Join(TimeSpan.FromSeconds(20)), "Passive assembled template smoke timed out.");
            if (failure is not null) throw new Exception("Assembled template smoke failed: " + failure.Message, failure);
        }
        finally { Environment.SetEnvironmentVariable("HSHELPER_DATA_DIR", previous); }
    }

    private const string Build = "255790"; private const string Base = "APP_ASSEMBLED_BASE";
    private static void DrainBindings()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void AwaitUi(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) DrainBindings();
        Check(task.IsCompleted, "Assembled panel operation did not finish."); task.GetAwaiter().GetResult();
    }
    private static Dictionary<string, CardRecord> Cards() => new()
    {
        [Base] = Card(Base, 1000, "基础随从", "基础触发&基础效果"), ["DONOR_A"] = Card("DONOR_A", 1001, "供体甲", "<b>触发甲</b>&效果甲"),
        ["DONOR_B"] = Card("DONOR_B", 1002, "供体乙", "触发乙&效果乙"), ["DONOR_C"] = Card("DONOR_C", 1003, "供体丙", "触发丙&效果丙"),
        ["HIDDEN_DONOR"] = Card("HIDDEN_DONOR", 9999, "隐藏文字", "私有触发&私有效果")
    };
    private static CardRecord Card(string id, int dbf, string name, string text) => new(id, dbf, name, text, "NEUTRAL", "TEST", "MINION", 99, false, [], [], "{}");
    private static GameSnapshot Frame()
    {
        var entities = new[] { new EntitySnapshot(1, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "1" }.ToFrozenDictionary()),
            new EntitySnapshot(2, null, EntityKind.Player, new Dictionary<string, string> { ["PLAYER_ID"] = "2" }.ToFrozenDictionary()),
            Entity(30, "HAND", 1, ("4572", 1001), ("4573", 1002)), Entity(31, "HAND", 1, ("4572", 1002), ("4573", 1001)),
            Entity(40, "PLAY", 2, ("4572", 1001), ("4573", 1002)), Entity(50, "HAND", 2, ("4572", 9999), ("4573", 9999)),
            Entity(60, "DECK", 1, ("4572", 9999), ("4573", 9999)) };
        return new("assembled-ui", 10, 100, 5, 1, 1, GamePhase.Main, entities.ToFrozenDictionary(entity => entity.Id), new(true, true, true, true, 0, [])) { ClientBuild = Build };
    }
    private static GameSnapshot ChoiceFrame()
    {
        var frame = Replace(Frame(), Entity(80, "SETASIDE", 1, ("4572", 1), ("4573", 1002)) with { CardId = "DONOR_B" });
        return frame with { Phase = GamePhase.Choice, Choice = new(7, 1, "GENERAL", 90, 1, 1, [new(80, "DONOR_B", 1, 0)], true) };
    }
    private static EntitySnapshot Entity(int id, string zone, int owner, params (string Name, int Value)[] observed)
    {
        var tags = new Dictionary<string, string> { ["ZONE"] = zone, ["CONTROLLER"] = owner.ToString(), ["CARDTYPE"] = "MINION" };
        foreach (var (name, value) in observed) tags[name] = value.ToString(); return new(id, Base, EntityKind.Card, tags.ToFrozenDictionary());
    }
    private static GameSnapshot Replace(GameSnapshot frame, EntitySnapshot entity)
    { var entities = frame.Entities.ToDictionary(); entities[entity.Id] = entity; return frame with { Entities = entities.ToFrozenDictionary() }; }
    private static string FindWorkspace()
    {
        var path = new DirectoryInfo(Environment.CurrentDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "HsHelper.sln")) && !File.Exists(Path.Combine(path.FullName, "HsHelper.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Workspace not found.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
