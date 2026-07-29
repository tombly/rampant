using System.Net.Sockets;
using System.Text.Json;

namespace Rampant.Agent;

/// <summary>Talks to the signal-cli sidecar's JSON-RPC daemon (see PLAN.md -> "Opening holes" ->
/// Signal). The daemon runs with `--receive-mode=on-start`, so incoming messages arrive as
/// unsolicited JSON-RPC notifications over a persistent TCP connection - genuinely event-driven,
/// not polled. This replaces the mailbox as the only interaction channel.</summary>
public sealed class SignalClient(string host, int port)
{
    /// <summary>Listens forever, reconnecting with a fixed backoff if the sidecar is unreachable
    /// or the connection drops. Calls <paramref name="onMessageReceived"/> for every incoming
    /// text message (sender identifier, message text). <paramref name="onError"/> is optional
    /// diagnostic logging for connection failures - a silent catch here previously masked a real
    /// bug (a message-parsing early-return) for long enough that it needed a raw TCP capture to
    /// diagnose; don't repeat that.</summary>
    public async Task RunAsync(Func<string, string, Task> onMessageReceived, CancellationToken ct, Func<Exception, Task>? onError = null)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(host, port, ct);
                using var stream = tcpClient.GetStream();
                using var reader = new StreamReader(stream);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break; // sidecar closed the connection - fall through and reconnect

                    await HandleLineAsync(line, onMessageReceived);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Sidecar not up yet, or the connection dropped - retry below.
                if (onError is not null)
                    await onError(ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static async Task HandleLineAsync(string line, Func<string, string, Task> onMessageReceived)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (!root.TryGetProperty("method", out var methodProp) || methodProp.GetString() != "receive")
            return; // e.g. request/response replies to our own "send" calls, not an inbound message

        if (!root.TryGetProperty("params", out var paramsProp) ||
            !paramsProp.TryGetProperty("envelope", out var envelope))
            return;

        if (!envelope.TryGetProperty("dataMessage", out var dataMessage))
            return; // receipts, typing indicators, sync messages - not an actual text message

        var text = dataMessage.TryGetProperty("message", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return;

        // sourceNumber is often null - Signal's phone-number-privacy feature means a contact's
        // envelope frequently carries only their UUID (source/sourceUuid), not their phone
        // number. Confirmed live: a real contact's envelope had sourceNumber=null. Either
        // identifier works as a "recipient" for sending a reply, so prefer the number when
        // present but fall back to the UUID rather than silently dropping the message.
        var source = envelope.TryGetProperty("sourceNumber", out var num) ? num.GetString() : null;
        if (string.IsNullOrWhiteSpace(source))
            source = envelope.TryGetProperty("sourceUuid", out var uuid) ? uuid.GetString() : null;
        if (string.IsNullOrWhiteSpace(source))
            return;

        await onMessageReceived(source, text);
    }

    /// <summary>Each send opens its own short-lived connection rather than sharing the listener's
    /// stream - simpler than coordinating writes/reads on one socket, and message volume here is
    /// tiny (a personal, single-user channel).</summary>
    public async Task SendAsync(string recipient, string message, CancellationToken ct)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, ct);
        using var stream = tcpClient.GetStream();
        using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

        var request = new
        {
            jsonrpc = "2.0",
            method = "send",
            @params = new { recipient = new[] { recipient }, message },
            id = Guid.NewGuid().ToString(),
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
    }
}
