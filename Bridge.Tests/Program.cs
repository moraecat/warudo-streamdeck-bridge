using System.Text.Json;
using WarudoBridge;

if (args.Contains("--serve"))
{
    await using var server = await TestServer.StartAsync(19070);
    Console.WriteLine("Mock Warudo: " + server.Uri);
    await Task.Delay(Timeout.Infinite);
    return;
}

var failed = 0;
await Run("ordered discovery, duplicate names, fragmented unicode", async () =>
{
    await using var server = await TestServer.StartAsync();
    await using var client = new WarudoConnection();
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    var snapshot = await client.RefreshAsync(CancellationToken.None);
    Check(snapshot.Receivers.Count == 5, "three unique triggers and two toggles");
    Check(snapshot.Receivers.Contains(new(ReceiverKind.Trigger, TestServer.Name)), "unicode trigger");
    Check(snapshot.Receivers.Contains(new(ReceiverKind.Toggle, "공통")), "same name in separate types");
    Check(snapshot.Toggles["공통"], "initial toggle state");
    var requests = server.Requests.Select(s => JsonDocument.Parse(s)).ToArray();
    try
    {
        Check(requests.Length == 3, "exactly three discovery requests");
        Check(requests[0].RootElement.GetProperty("data").GetProperty("type").GetString() == "trigger", "trigger first");
        Check(requests[1].RootElement.GetProperty("data").GetProperty("type").GetString() == "toggle", "toggle second");
        Check(requests[2].RootElement.GetProperty("action").GetString() == "getToggles", "states third");
    }
    finally { foreach (var d in requests) d.Dispose(); }
});
await Run("action encoding and authoritative toggle notification", async () =>
{
    await using var server = await TestServer.StartAsync();
    await using var client = new WarudoConnection();
    var update = new TaskCompletionSource<ToggleUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
    client.ToggleChanged += change => update.TrySetResult(change);
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    await client.SendActionAsync(new(ReceiverKind.Trigger, TestServer.Name), CancellationToken.None);
    await client.SendActionAsync(new(ReceiverKind.Toggle, TestServer.Name), CancellationToken.None);
    var result = await update.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(result == new ToggleUpdate(TestServer.Name, true, true), "toggle confirmation");
    Check(server.Requests.Count == 2, "commands sent once");
    using var first = JsonDocument.Parse(server.Requests.First());
    Check(first.RootElement.GetProperty("data").GetProperty("receiverName").GetString() == TestServer.Name, "exact name transport");
});
await Run("simultaneous refreshes cannot mix receiver types", async () =>
{
    await using var server = await TestServer.StartAsync();
    await using var client = new WarudoConnection();
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    var snapshots = await Task.WhenAll(client.RefreshAsync(CancellationToken.None), client.RefreshAsync(CancellationToken.None));
    Check(snapshots.All(s => s.Receivers.Count(r => r.Kind == ReceiverKind.Trigger) == 3), "typed snapshots");
});
await Run("timeout closes ambiguous session and rejects offline action", async () =>
{
    await using var server = await TestServer.StartAsync();
    server.Mode = "timeout";
    await using var client = new WarudoConnection(TimeSpan.FromMilliseconds(180));
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    await Throws(() => client.RefreshAsync(CancellationToken.None));
    Check(!client.IsConnected, "timed out connection invalidated");
    await Throws(() => client.SendActionAsync(new(ReceiverKind.Trigger, "bad"), CancellationToken.None));
    Check(server.Requests.Count == 1, "no queued/replayed action");
});
await Run("server error preserves failure instead of returning empty success", async () =>
{
    await using var server = await TestServer.StartAsync();
    server.Mode = "error";
    await using var client = new WarudoConnection();
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    await Throws(() => client.RefreshAsync(CancellationToken.None));
});
await Run("abrupt close completes pending query promptly", async () =>
{
    await using var server = await TestServer.StartAsync();
    server.Mode = "drop";
    await using var client = new WarudoConnection();
    await client.ConnectAsync(server.Uri, CancellationToken.None);
    await Throws(() => client.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    Check(!client.IsConnected, "closed state");
});
await Run("settings roundtrip separates types and preserves corrupt file", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "WarudoBridgeTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "settings.json");
    try
    {
        var store = new SettingsStore(path);
        var settings = store.Load();
        settings.Favorites.Add(new(ReceiverKind.Trigger, "공통"));
        settings.Favorites.Add(new(ReceiverKind.Toggle, "공통"));
        store.Save(settings);
        Check(store.Load().Favorites.Count == 2, "separate favorites persisted");
        File.WriteAllText(path, "broken{");
        Check(store.Load().Favorites.Count == 0, "defaults after corrupt file");
        Check(Directory.GetFiles(folder, "*.corrupt-*").Length == 1, "corrupt data preserved");
        Check(store.Warning is not null, "recovery surfaced");
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
await Run("panel filtering, offline state and pending-toggle guard", () =>
{
    var item = new ReceiverItem(new(ReceiverKind.Toggle, "웃음 ABC"), true);
    Check(item.Matches("abc", null, true), "case-insensitive favorite search");
    Check(!item.Matches("", ReceiverKind.Trigger, false), "type filter");
    Check(!item.CanExecute, "offline actions disabled");
    item.IsConnected = true;
    Check(item.CanExecute, "connected actions enabled");
    item.IsBusy = true;
    Check(!item.CanExecute, "pending action cannot repeat");
    item.ToggleState = true;
    Check(item.StateLabel == "ON", "authoritative ON label");
    item.IsConnected = false;
    Check(!item.CanExecute && item.StateLabel == "연결 끊김", "stale state not presented as current");
    return Task.CompletedTask;
});
await Run("temporary sharing lock cannot destroy unpreserved corrupt settings", () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "WarudoBridgeTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "settings.json");
    try
    {
        File.WriteAllText(path, "broken{");
        var store = new SettingsStore(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) store.Load();
        store.Save(new BridgeSettings());
        var backup = Directory.GetFiles(folder, "*.corrupt-*");
        Check(backup.Length == 1 && File.ReadAllText(backup[0]) == "broken{", "original preserved before later save");
    }
    finally { Directory.Delete(folder, true); }
    return Task.CompletedTask;
});
Console.WriteLine(failed == 0 ? "All 9 tests passed." : $"{failed} tests failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is not NotImplementedException) { return; }
    throw new Exception("Expected operation failure");
}
