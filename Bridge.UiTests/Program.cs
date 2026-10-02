using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WarudoBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WarudoBridge;component/Styles.xaml", UriKind.Relative) });
        var exit = 0;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await VerifyAsync(args.FirstOrDefault() ?? "artifacts/ui-preview.png"); }
            catch (Exception e) { exit = 1; Console.WriteLine("FAIL UI: " + e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        System.Windows.Threading.Dispatcher.Run();
        return exit;
    }

    private static async Task VerifyAsync(string imagePath)
    {
        await using var server = await TestServer.StartAsync();
        var folder = Path.Combine(Path.GetTempPath(), "WarudoBridgeUiTests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        store.Save(new BridgeSettings { Address = server.Uri.ToString() });
        var window = new MainWindow(store);
        var list = (ItemsControl)window.FindName("ReceiverList");
        var root = (FrameworkElement)window.Content;
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            try { await Until(() => list.Items.Count == 5, "automatic discovery"); }
            catch
            {
                Console.WriteLine($"Discovery diagnostic: requests={server.Requests.Count}; items={list.Items.Count}; status={((TextBlock)window.FindName("StatusLabel")).Text}");
                throw;
            }
            Layout(root);
            var toggle = list.Items.Cast<ReceiverItem>().Single(i => i.Key == new ReceiverKey(ReceiverKind.Toggle, "공통"));
            Check(toggle.ToggleState == true, "initial authoritative toggle state");
            var search = (TextBox)window.FindName("SearchBox");
            search.Text = "공통";
            Check(list.Items.Count == 2, "search finds same name in both types");
            ((RadioButton)window.FindName("ToggleFilter")).IsChecked = true;
            Check(list.Items.Count == 1, "toggle filter narrows search");
            search.Text = "";
            ((RadioButton)window.FindName("AllFilter")).IsChecked = true;
            Layout(root);
            var action = ActionButton(root, toggle);
            var countBefore = server.Requests.Count(s => s.Contains("\"action\":\"toggle\""));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !toggle.IsBusy, "toggle response");
            Check(server.Requests.Count(s => s.Contains("\"action\":\"toggle\"")) == countBefore + 1, "double click while pending sends once");
            Check(toggle.ToggleState == true, "state uses server confirmation rather than local inversion");
            var favorite = Descendants<Button>(root).Single(b => ReferenceEquals(b.DataContext, toggle) && b.Content?.ToString() == "☆");
            favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Load().Favorites.SequenceEqual(new[] { toggle.Key }), "favorite saved by type and name");
            Check(server.Requests.Count(s => s.Contains("\"action\":\"toggle\"")) == countBefore + 1, "favorite star must not activate deck key");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Layout(root);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
            Render(root, imagePath);
            Console.WriteLine("UI preview: " + Path.GetFullPath(imagePath));
            server.Mode = "silentToggle";
            var connectionsBefore = server.Connections;
            action = ActionButton(root, toggle);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => server.Connections > connectionsBefore, "toggle timeout replaces ambiguous connection");
            await Until(() => toggle.IsConnected, "reconnect after unconfirmed toggle");
            server.Mode = "normal";
            server.ExtraTriggerName = new string('가', 5000);
            ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => list.Items.Count == 6, "long receiver discovery");
            Layout(root);
            var longItem = list.Items.Cast<ReceiverItem>().Single(i => i.Name.Length == 5000);
            var longAction = ActionButton(root, longItem);
            longAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !longItem.IsBusy, "long receiver action");
            Layout(root);
            Check(((TextBlock)window.FindName("StatusLabel")).ActualHeight < 70, "long status must not collapse panel");
            server.Mode = "drop";
            ((Button)window.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !toggle.IsConnected, "disconnected UI");
            Check(!action.IsEnabled && toggle.ToggleState is null, "offline buttons disabled and stale state cleared");
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Render(root, Path.ChangeExtension(imagePath, ".offline.png"));
            Console.WriteLine("PASS UI: discovery, search, filters, toggle click, duplicate guard, favorites, offline state, rendering");
        }
        finally
        {
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            await Until(() => closed, "window shuts down its connection loop");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static void Layout(FrameworkElement root)
    {
        root.Measure(new Size(1100, 780));
        root.Arrange(new Rect(0, 0, 1100, 780));
        root.UpdateLayout();
    }
    private static void Render(FrameworkElement root, string path)
    {
        Layout(root);
        var bitmap = new RenderTargetBitmap(1100, 780, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 19, 28)), null, new Rect(0, 0, 1100, 780));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null,
                new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(backdrop);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var next in Descendants<T>(child)) yield return next;
        }
    }
    private static Button ActionButton(DependencyObject root, ReceiverItem item) =>
        Descendants<Button>(root).Single(b => ReferenceEquals(b.DataContext, item) && AutomationProperties.GetName(b) == item.Name);
    private static async Task Until(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timeout: " + description);
            await Task.Delay(25);
        }
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
}
