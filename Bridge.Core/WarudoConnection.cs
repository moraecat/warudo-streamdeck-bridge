using System.Net.WebSockets;
using System.Text.Json;

namespace WarudoBridge;

/// <summary>One connection lifetime. Replace the instance after a disconnect or query timeout.</summary>
public sealed class WarudoConnection(TimeSpan? queryTimeout = null) : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object sync = new();
    private readonly TimeSpan timeout = queryTimeout ?? TimeSpan.FromSeconds(4);
    private PendingQuery? pending;
    private Task? receiveTask;
    private int ended;
    private sealed record PendingQuery(string Action, TaskCompletionSource<JsonElement> Completion);

    public bool IsConnected => Volatile.Read(ref ended) == 0 && socket.State == WebSocketState.Open;
    public event Action<ToggleUpdate>? ToggleChanged;
    public event Action<Exception?>? Disconnected;
    public event Action<string>? ProtocolError;

    public async Task ConnectAsync(Uri uri, CancellationToken token)
    {
        if (uri.Scheme is not ("ws" or "wss")) throw new ArgumentException("WebSocket 주소가 필요합니다.");
        if (socket.State != WebSocketState.None || Volatile.Read(ref ended) != 0)
            throw new InvalidOperationException("새 연결 인스턴스를 사용해야 합니다.");
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        connectTimeout.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
            receiveTask = ReceiveLoopAsync();
        }
        catch (Exception e) { End(e); throw; }
    }

    public async Task<ReceiverSnapshot> RefreshAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var triggers = await QueryAsync("getReceivers", new { type = "trigger" }, linked.Token).ConfigureAwait(false);
            var toggles = await QueryAsync("getReceivers", new { type = "toggle" }, linked.Token).ConfigureAwait(false);
            var states = await QueryAsync("getToggles", new { }, linked.Token).ConfigureAwait(false);
            var receivers = Names(triggers, ReceiverKind.Trigger).Concat(Names(toggles, ReceiverKind.Toggle)).ToArray();
            var dictionary = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var property in states.EnumerateObject()) dictionary[property.Name] = property.Value.GetBoolean();
            return new ReceiverSnapshot(receivers, dictionary);
        }
        catch (Exception e) { End(e); throw; }
        finally { refreshGate.Release(); }
    }

    private static IEnumerable<ReceiverKey> Names(JsonElement element, ReceiverKind kind) =>
        element.EnumerateArray().Select(e => e.GetString()).Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal).Select(n => new ReceiverKey(kind, n!));

    public Task SendActionAsync(ReceiverKey key, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key.Name) || !Enum.IsDefined(key.Kind))
            throw new ArgumentException("유효한 수신자 이름과 종류가 필요합니다.");
        return SendAsync(key.Kind == ReceiverKind.Trigger ? "trigger" : "toggle", new { receiverName = key.Name }, token);
    }

    private async Task<JsonElement> QueryAsync(string action, object data, CancellationToken token)
    {
        var request = new PendingQuery(action, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (sync)
        {
            EnsureConnected();
            if (pending is not null) throw new InvalidOperationException("조회 요청이 겹쳤습니다.");
            pending = request;
        }
        try
        {
            await SendAsync(action, data, token).ConfigureAwait(false);
            return await request.Completion.Task.WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // There is no request ID: never reuse a connection with an ambiguous late response.
            End(e);
            throw;
        }
        finally { lock (sync) { if (ReferenceEquals(pending, request)) pending = null; } }
    }

    private async Task SendAsync(string action, object data, CancellationToken token)
    {
        EnsureConnected();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        linked.CancelAfter(timeout);
        await sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { action, data });
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linked.Token).ConfigureAwait(false);
        }
        catch (Exception e) { End(e); throw; }
        finally { sendGate.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) { End(null); return; }
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("텍스트 메시지가 아닙니다.");
                    if (message.Length + result.Count > 4 * 1024 * 1024) throw new InvalidDataException("응답 크기가 너무 큽니다.");
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                HandleMessage(document.RootElement);
            }
        }
        catch (Exception e) { End(e); }
    }

    private void HandleMessage(JsonElement message)
    {
        var action = message.GetProperty("action").GetString();
        PendingQuery? request;
        lock (sync) request = pending;
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var exception = new InvalidOperationException("Warudo 응답 오류: " + error.GetString());
            if (request is not null && request.Action == action) request.Completion.TrySetException(exception);
            else ProtocolError?.Invoke(exception.Message);
            return;
        }
        if (action == "toggle")
        {
            var data = message.GetProperty("data");
            ToggleChanged?.Invoke(new ToggleUpdate(data.GetProperty("receiverName").GetString()!,
                data.GetProperty("state").GetBoolean(),
                data.TryGetProperty("isResponse", out var response) && response.GetBoolean()));
        }
        else if (request is not null && request.Action == action) request.Completion.TrySetResult(message.GetProperty("data").Clone());
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Warudo에 연결되어 있지 않습니다.");
    }

    private void End(Exception? error)
    {
        if (Interlocked.Exchange(ref ended, 1) != 0) return;
        lifetime.Cancel();
        socket.Abort();
        lock (sync) pending?.Completion.TrySetException(error ?? new IOException("Warudo 연결이 종료되었습니다."));
        Disconnected?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        End(null);
        if (receiveTask is not null) await receiveTask.ConfigureAwait(false);
        socket.Dispose();
        // Gates/CTS remain valid for any operation whose cancellation continuation is still unwinding.
    }
}
