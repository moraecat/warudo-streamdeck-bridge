using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal sealed class TestServer : IAsyncDisposable
{
    private WebApplication app = null!;
    private int sequence;
    public Uri Uri { get; private set; } = null!;
    public ConcurrentQueue<string> Requests { get; } = new();
    public string Mode { get; set; } = "normal";
    public int Connections;
    public string? ExtraTriggerName { get; set; }
    public bool ToggleState { get; private set; }
    public const string Name = "웃음 \"특수\" \\ 🎉";

    public static async Task<TestServer> StartAsync(int port = 0)
    {
        var server = new TestServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        server.app = builder.Build();
        server.app.UseWebSockets();
        server.app.Run(server.HandleAsync);
        await server.app.StartAsync();
        var address = server.app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        server.Uri = new Uri(address.Replace("http:", "ws:") + "/");
        return server;
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Interlocked.Increment(ref Connections);
        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                var raw = Encoding.UTF8.GetString(message.ToArray());
                Requests.Enqueue(raw);
                using var doc = JsonDocument.Parse(raw);
                var action = doc.RootElement.GetProperty("action").GetString();
                var data = doc.RootElement.GetProperty("data");
                if (Mode == "drop") { socket.Abort(); return; }
                if (Mode == "timeout") continue;
                if (Mode == "error")
                {
                    await SendAsync(socket, new { action, error = "test error" }, context.RequestAborted);
                    continue;
                }
                if (action == "getReceivers")
                {
                    var type = data.GetProperty("type").GetString();
                    var names = type == "trigger"
                        ? new[] { Name, Name, "공통", "   ", "길게 표시되는 트리거 이름 테스트 — 의상 전환과 표정 변화" }
                        : new[] { "공통", Name };
                    if (type == "trigger" && ExtraTriggerName is not null) names = names.Append(ExtraTriggerName).ToArray();
                    await SendAsync(socket, new { action, data = names }, context.RequestAborted);
                    Interlocked.Increment(ref sequence);
                }
                else if (action == "getToggles")
                {
                    await SendAsync(socket, new { action, data = new Dictionary<string, bool> { ["공통"] = true, [Name] = ToggleState } }, context.RequestAborted);
                }
                else if (action == "toggle")
                {
                    ToggleState = !ToggleState;
                    if (Mode == "silentToggle") continue;
                    await SendAsync(socket, new { action, data = new { receiverName = data.GetProperty("receiverName").GetString(), state = ToggleState, isResponse = true } }, context.RequestAborted);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
    }

    private static async Task SendAsync(WebSocket socket, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        // Split inside multibyte UTF-8 too; the client must assemble bytes before decoding.
        var middle = Math.Max(1, bytes.Length / 2);
        await socket.SendAsync(new ArraySegment<byte>(bytes, 0, middle), WebSocketMessageType.Text, false, token);
        await socket.SendAsync(new ArraySegment<byte>(bytes, middle, bytes.Length - middle), WebSocketMessageType.Text, true, token);
    }

    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
