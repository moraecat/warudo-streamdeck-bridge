using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WarudoBridge;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ReceiverItem> items = [];
    private readonly Dictionary<ReceiverKey, TaskCompletionSource<bool>> confirmations = [];
    private readonly Dictionary<string, (long Revision, bool State)> latestToggles = new(StringComparer.Ordinal);
    private readonly SettingsStore store;
    private readonly BridgeSettings settings;
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim refreshSignal = new(0, 1);
    private readonly ICollectionView view;
    private CancellationTokenSource? session;
    private WarudoConnection? connection;
    private Task? runner;
    private long revision;
    private bool online, closing;

    public MainWindow(SettingsStore? settingsStore = null)
    {
        InitializeComponent();
        store = settingsStore ?? new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarudoBridge", "settings.json"));
        settings = store.Load();
        AddressBox.Text = settings.Address;
        view = CollectionViewSource.GetDefaultView(items);
        view.Filter = obj => obj is ReceiverItem item && item.Matches(SearchBox.Text,
            TriggerFilter.IsChecked == true ? ReceiverKind.Trigger : ToggleFilter.IsChecked == true ? ReceiverKind.Toggle : null,
            FavoriteFilter.IsChecked == true);
        view.SortDescriptions.Add(new(nameof(ReceiverItem.IsFavorite), ListSortDirection.Descending));
        view.SortDescriptions.Add(new(nameof(ReceiverItem.Name), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new(nameof(ReceiverItem.KindLabel), ListSortDirection.Ascending));
        ReceiverList.ItemsSource = view;
        if (store.Warning is not null) StatusLabel.Text = store.Warning;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e) => runner = ConnectionLoopAsync();

    private async Task ConnectionLoopAsync()
    {
        var failures = 0;
        while (!shutdown.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            session = attempt;
            await using var client = new WarudoConnection();
            connection = client;
            latestToggles.Clear();
            client.ToggleChanged += update => OnUi(() =>
            {
                if (!ReferenceEquals(connection, client) || attempt.IsCancellationRequested) return;
                latestToggles[update.Name] = (++revision, update.State);
                var key = new ReceiverKey(ReceiverKind.Toggle, update.Name);
                var item = items.FirstOrDefault(i => i.Key == key);
                if (item is not null) item.ToggleState = update.State;
                if (update.IsResponse && confirmations.TryGetValue(key, out var confirmation)) confirmation.TrySetResult(update.State);
            });
            client.Disconnected += _ => OnUi(() =>
            {
                if (!ReferenceEquals(connection, client) || attempt.IsCancellationRequested) return;
                SetOnline(false);
                attempt.Cancel();
            });
            client.ProtocolError += message => OnUi(() => { if (ReferenceEquals(connection, client)) StatusLabel.Text = message; });
            try
            {
                ConnectionBadge.Text = "● 연결 중";
                ConnectionBadge.Foreground = Brush("#E3C784");
                await client.ConnectAsync(new Uri(settings.Address), attempt.Token);
                while (!attempt.IsCancellationRequested)
                {
                    var beforeRefresh = revision;
                    var snapshot = await client.RefreshAsync(attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    SetOnline(true);
                    ApplySnapshot(snapshot, beforeRefresh);
                    failures = 0;
                    StatusLabel.Text = $"{DateTime.Now:HH:mm:ss} 목록 갱신 · 5초마다 자동 갱신 · 같은 종류의 같은 이름은 함께 실행됩니다.";
                    await refreshSignal.WaitAsync(TimeSpan.FromSeconds(5), attempt.Token);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception e)
            {
                if (!shutdown.IsCancellationRequested)
                    StatusLabel.Text = e is OperationCanceledException or System.Net.WebSockets.WebSocketException
                        ? "연결되지 않았습니다. Warudo 실행과 Stream Deck 노드를 확인하세요. 자동으로 다시 연결합니다."
                        : "연결 또는 목록 조회 실패: " + Brief(e.Message);
            }
            finally
            {
                SetOnline(false);
                foreach (var completion in confirmations.Values) completion.TrySetCanceled();
                confirmations.Clear();
                if (ReferenceEquals(connection, client)) connection = null;
                session = null;
            }
            if (shutdown.IsCancellationRequested) break;
            // Dispose the old transport before sleeping or starting a new connection.
            await client.DisposeAsync();
            var delay = failures++ switch { 0 => 1, 1 => 2, _ => 5 };
            try { await Task.Delay(TimeSpan.FromSeconds(delay), shutdown.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ApplySnapshot(ReceiverSnapshot snapshot, long beforeRefresh)
    {
        var current = snapshot.Receivers.ToHashSet();
        // ObservableCollection change events must run outside a deferred CollectionView refresh.
        {
            for (var i = items.Count - 1; i >= 0; i--) if (!current.Contains(items[i].Key)) items.RemoveAt(i);
            var byKey = items.ToDictionary(i => i.Key);
            foreach (var key in snapshot.Receivers)
            {
                if (!byKey.TryGetValue(key, out var item))
                {
                    item = new ReceiverItem(key, settings.Favorites.Contains(key));
                    items.Add(item);
                }
                item.IsConnected = true;
                if (key.Kind == ReceiverKind.Toggle)
                    item.ToggleState = latestToggles.TryGetValue(key.Name, out var fresh) && fresh.Revision > beforeRefresh
                        ? fresh.State : snapshot.Toggles.TryGetValue(key.Name, out var state) ? state : null;
            }
        }
        UpdateCounts();
    }

    private void SetOnline(bool value)
    {
        online = value;
        ConnectionBadge.Text = value ? "● Warudo 연결됨" : "● 연결 대기";
        ConnectionBadge.Foreground = Brush(value ? "#80DFB7" : "#E3C784");
        RefreshButton.IsEnabled = value;
        foreach (var item in items)
        {
            item.IsConnected = value;
            if (!value) { item.IsBusy = false; item.ToggleState = null; }
        }
        UpdateCounts();
    }

    private async void ExecuteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReceiverItem item } || !item.CanExecute || !online || connection is null || session is null) return;
        var client = connection;
        var token = session.Token;
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        item.IsBusy = true;
        if (item.IsToggle) confirmations[item.Key] = confirmation;
        try
        {
            await client.SendActionAsync(item.Key, token);
            if (item.IsToggle)
            {
                await confirmation.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
                StatusLabel.Text = $"{item.Name} · {item.StateLabel}";
            }
            else StatusLabel.Text = $"{item.Name} · 전송됨";
        }
        catch (TimeoutException)
        {
            item.ToggleState = null;
            StatusLabel.Text = "토글 확인이 늦어 연결을 새로 맺고 현재 상태를 조회합니다.";
            // Toggle acknowledgements also have no request ID. A late reply must not confirm a later click.
            if (ReferenceEquals(connection, client))
            {
                SetOnline(false);
                session?.Cancel();
                await client.DisposeAsync();
            }
        }
        catch (OperationCanceledException) { StatusLabel.Text = "연결이 바뀌었습니다. 이전 클릭은 다시 전송하지 않습니다."; }
        catch (Exception error) { StatusLabel.Text = "전송 실패: " + Brief(error.Message); }
        finally
        {
            if (ReferenceEquals(connection, client)) item.IsBusy = false;
            if (confirmations.TryGetValue(item.Key, out var current) && ReferenceEquals(current, confirmation)) confirmations.Remove(item.Key);
        }
    }

    private void FavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReceiverItem item }) return;
        item.IsFavorite = !item.IsFavorite;
        if (item.IsFavorite) settings.Favorites.Add(item.Key); else settings.Favorites.RemoveAll(f => f == item.Key);
        SaveSettings();
        view.Refresh();
        UpdateCounts();
    }

    private void ConnectClicked(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo))
        { StatusLabel.Text = "ws://localhost:19069/ 형식의 연결 주소를 입력하세요."; return; }
        settings.Address = address;
        SaveSettings();
        SetOnline(false);
        session?.Cancel();
    }

    private void SaveSettings()
    {
        try { store.Save(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { StatusLabel.Text = "설정을 저장하지 못했습니다. 폴더의 쓰기 권한을 확인하세요."; }
    }

    private void RefreshClicked(object sender, RoutedEventArgs e) => RequestRefresh();
    private void RequestRefresh() { if (refreshSignal.CurrentCount == 0) refreshSignal.Release(); }
    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (view is null || SearchHint is null) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view.Refresh();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        if (view is null) return;
        var visible = view.Cast<ReceiverItem>().Count();
        CountLabel.Text = $"{visible} / {items.Count}개";
        EmptyPanel.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = !online ? "Warudo를 기다리는 중" : items.Count == 0 ? "수신자가 없습니다" : "검색 결과가 없습니다";
        EmptyDescription.Text = !online || items.Count == 0
            ? "Warudo에서 Stream Deck Trigger 또는 Toggle 노드를 추가하고 수신자 이름을 지정하세요."
            : "다른 이름을 검색하거나 필터를 변경해 보세요.";
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        // Holding Enter/Space on a focused action button must not continuously fire commands.
        if (e.IsRepeat && e.Key is Key.Enter or Key.Space && Keyboard.FocusedElement is Button) e.Handled = true;
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        closing = true;
        IsEnabled = false;
        shutdown.Cancel();
        if (runner is not null) await runner;
        Close();
    }

    private void OnUi(Action action)
    {
        if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => { if (!closing) action(); });
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static string Brief(string message) => message.Length <= 180 ? message : message[..180] + "…";
}
