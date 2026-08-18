using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

public sealed record SignalInbound(string Sender, string Text, bool IsGroupMessage);

/// <summary>
/// The one connection to the signal-cli sidecar's JSON-RPC daemon, in both directions. The daemon
/// runs with --receive-mode=on-start, so inbound messages arrive as unsolicited notifications over
/// a persistent TCP connection - genuinely event-driven, not polled.
///
/// Sends go out over that *same* connection rather than a short-lived one per message. Opening a
/// fresh socket per send is simpler but leaves an unanswered question: whether the daemon
/// fans receive-notifications out to every connected client or hands each to one. If it is the
/// latter, a send racing an inbound message could swallow it - an unacceptable failure mode for
/// the only channel in. One connection removes the question entirely.
///
/// It also reads the response. Writing the request and disposing the stream - as an earlier
/// notifier did - leaves a connection that succeeded but was rejected (unregistered number, bad
/// recipient, rate limit) indistinguishable from success, so the alert channel is never positively
/// confirmed to deliver anything. Here a failed send throws.
/// </summary>
public sealed class SignalClient(string host, int port, ILogger<SignalClient> logger)
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectionWait = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private volatile StreamWriter? _writer;

    public bool IsConnected => _writer is not null;

    /// <summary>Listens forever, reconnecting on failure. Runs for the process lifetime.</summary>
    public async Task RunAsync(Func<SignalInbound, Task> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct);
                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream);
                var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

                _writer = writer;
                logger.LogInformation("Connected to signal-cli at {Host}:{Port}", host, port);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                        break; // sidecar closed the connection - reconnect below

                    await DispatchAsync(line, onMessage);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("signal-cli connection error: {Message}", ex.Message);
            }
            finally
            {
                _writer = null;
                FailPending("signal-cli connection closed");
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(ReconnectDelay, ct);
        }
    }

    public async Task SendAsync(string recipient, string message, CancellationToken ct)
    {
        var writer = await WaitForConnectionAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "send",
            @params = new { recipient = new[] { recipient }, message },
            id,
        });

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await writer.WriteLineAsync(request.AsMemory(), ct);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            var result = await tcs.Task.WaitAsync(timeout.Token);

            ThrowIfDeliveryFailed(result);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>signal-cli returns HTTP-200-shaped success for a request it accepted but could not
    /// deliver - the per-recipient verdict is inside result.results[].type. Anything other than
    /// SUCCESS is a real failure the caller needs to know about.</summary>
    private static void ThrowIfDeliveryFailed(JsonElement result)
    {
        if (!result.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return;

        foreach (var entry in results.EnumerateArray())
        {
            var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is not null && !type.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"signal-cli refused delivery: {type}");
        }
    }

    private async Task<StreamWriter> WaitForConnectionAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectionWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_writer is { } writer)
                return writer;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        throw new InvalidOperationException($"No connection to signal-cli after {ConnectionWait.TotalSeconds:0}s.");
    }

    private async Task DispatchAsync(string line, Func<SignalInbound, Task> onMessage)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            logger.LogWarning("Unparseable line from signal-cli, ignoring");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;

            // A response to one of our own sends - hand it to whoever is waiting on that id.
            if (root.TryGetProperty("id", out var idProp) && idProp.GetString() is { } id
                && _pending.TryGetValue(id, out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                    tcs.TrySetException(new InvalidOperationException($"signal-cli error: {message}"));
                }
                else
                {
                    // Clone: the JsonDocument is disposed the moment this scope exits, and the
                    // awaiting send may not have resumed by then.
                    tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                }

                return;
            }

            if (TryParseInbound(root) is { } inbound)
                await onMessage(inbound);
        }
    }

    private static SignalInbound? TryParseInbound(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var method) || method.GetString() != "receive")
            return null;

        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("envelope", out var envelope))
            return null;

        if (!envelope.TryGetProperty("dataMessage", out var dataMessage))
            return null; // receipts, typing indicators, sync messages - not an actual text message

        var text = dataMessage.TryGetProperty("message", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // sourceNumber is often null - Signal's phone-number-privacy feature means a contact's
        // envelope frequently carries only their UUID. Confirmed live against a real contact.
        // Either identifier works as a "recipient" when replying, so prefer the number when
        // present but fall back to the UUID rather than silently dropping the message.
        var source = envelope.TryGetProperty("sourceNumber", out var num) ? num.GetString() : null;
        if (string.IsNullOrWhiteSpace(source))
            source = envelope.TryGetProperty("sourceUuid", out var uuid) ? uuid.GetString() : null;
        if (string.IsNullOrWhiteSpace(source))
            return null;

        // A dataMessage carries "groupInfo" when it was sent to a group rather than as a direct
        // message. Group traffic is rejected outright: a group's membership is a far wider trust
        // surface than one linked account's DMs, and there's no evidence it's how the owner talks
        // to this agent.
        var isGroup = dataMessage.TryGetProperty("groupInfo", out _);

        return new SignalInbound(source, text!, isGroup);
    }

    private void FailPending(string reason)
    {
        foreach (var (id, tcs) in _pending)
        {
            tcs.TrySetException(new InvalidOperationException(reason));
            _pending.TryRemove(id, out _);
        }
    }
}
