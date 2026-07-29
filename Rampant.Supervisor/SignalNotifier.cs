using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Rampant.Supervisor;

/// <summary>Fire-and-forget outbound alerts to the owner over the same signal-cli sidecar the
/// agent uses for conversation - see PLAN.md -> "Opening holes" -> Signal. Deliberately separate
/// from the agent's own SignalClient (different repo, different concern): the supervisor never
/// listens for or reacts to inbound messages, it only pushes operational alerts (build failures,
/// unexpected crashes) to a fixed recipient. This is the one piece of the supervisor that talks to
/// the outside world - everything else about it is purely internal (build/restart/log).</summary>
public sealed class SignalNotifier(string host, int port, string? ownerIdentifier, ILogger<SignalNotifier> logger)
{
    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ownerIdentifier))
            return; // Not configured - notifications are optional, never block supervisor logic.

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, ct);
            using var stream = tcpClient.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

            var request = new
            {
                jsonrpc = "2.0",
                method = "send",
                @params = new { recipient = new[] { ownerIdentifier }, message },
                id = Guid.NewGuid().ToString(),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        }
        catch (Exception ex)
        {
            // Best-effort - a notification failure must never take down the supervisor itself.
            logger.LogWarning(ex, "Failed to send Signal notification");
        }
    }
}
