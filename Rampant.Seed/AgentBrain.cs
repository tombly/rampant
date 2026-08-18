using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Rampant.Agent;

/// <summary>
/// One turn: build the system prompt, gather the tools, ask the model, return what it said.
///
/// There is no conversation history, and that is a choice rather than an omission. Genesis has no
/// memory of any kind - each turn sees the current envelope and nothing else. The expectation is
/// that memory is the first thing the agent notices it is missing and asks for; watching how it
/// gets there is the most interesting variable in this design. Adding history here would answer
/// the question before it was asked.
/// </summary>
public sealed class AgentBrain(IChatClient _chat)
{
    /// <summary>What the agent says when a wake tick warranted nothing. Recognised by the loop and
    /// never sent - see SELF.md, which tells the model about it.</summary>
    public const string SilenceMarker = "(nothing)";

    public async Task<string> HandleAsync(InboxEnvelope envelope, CancellationToken ct)
    {
        var skipped = new List<string>();

        // Core tools first, then whatever the agent has built for itself. At genesis the second
        // list is empty and request_capability is the only thing it can do besides talk.
        var tools = new List<AITool>();
        tools.AddRange(ToolLoader.FromInstance(new CapabilityTools(envelope), skipped.Add));
        tools.AddRange(ToolLoader.Load(skipped.Add));

        if (skipped.Count > 0)
        {
            await Log.AppendAsync(
                AgentPaths.HeartbeatLog,
                $"tools skipped at load: {string.Join("; ", skipped)}",
                CancellationToken.None);
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, await BuildSystemPromptAsync(envelope, tools.Count, ct)),
            new(ChatRole.User, envelope.Text),
        };

        var response = await _chat.GetResponseAsync(
            messages,
            new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto },
            ct);

        return response.Text.Trim();
    }

    private static async Task<string> BuildSystemPromptAsync(InboxEnvelope envelope, int toolCount, CancellationToken ct)
    {
        var sb = new StringBuilder();

        if (File.Exists(AgentPaths.SelfMd))
            sb.AppendLine(await File.ReadAllTextAsync(AgentPaths.SelfMd, ct));

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Computed once, here, rather than looked up mid-turn. The model otherwise reaches for
        // whatever tool it has and guesses - searching the web for "current time" has landed about
        // a day out, because search results are cached page snapshots rather than a clock.
        sb.AppendLine($"Current date/time: {DateTimeOffset.UtcNow:R} (UTC)");
        sb.AppendLine($"Tools available to you right now: {toolCount}");
        sb.AppendLine();

        sb.AppendLine(envelope.Kind switch
        {
            InboxKind.Wake =>
                // Deliberately does not state how often ticks arrive. The interval is the
                // supervisor's setting, this process cannot read it, and the envelope below says
                // so authoritatively - asserting a period here would be guessing at a fact that
                // has already been stated correctly.
                "This turn was triggered by a scheduled wake tick, not by the owner. Nobody is waiting for a reply. "
                + $"If nothing is worth saying, answer with exactly {SilenceMarker} and nothing else - that costs the owner nothing and is the right answer most hours.",

            InboxKind.Outcome =>
                "This turn was triggered by the outcome of a capability request that an earlier version of you filed. "
                + "You have no memory of asking for it; everything you know about it is in the message below. "
                + "If it succeeded and someone was waiting, do the thing they originally asked for and then tell them.",

            _ => "This turn was triggered by a message from the owner. Answer them.",
        });

        if (await ReadStatusAsync(ct) is { } status)
        {
            sb.AppendLine();
            sb.AppendLine("Your current standing with the supervisor:");
            sb.AppendLine(status);
        }

        return sb.ToString();
    }

    /// <summary>A compact rendering of status.json, so the agent can tell whether asking for
    /// something is worth doing. It cannot change any of these numbers - the supervisor enforces
    /// them regardless of what the agent concludes - but knowing them is the difference between
    /// "I'll get that built" and repeatedly filing requests that will be refused.
    ///
    /// Deserialized into a local record with case-insensitive matching rather than read key by key
    /// out of a JsonElement, whose lookups are case-sensitive. That mismatch was a live bug: the
    /// supervisor writes PascalCase, the reader asked for camelCase, and every number silently came
    /// back zero - so the agent would have believed it had no budget and quietly stopped asking for
    /// anything, with nothing in any log to say why.</summary>
    private static async Task<string?> ReadStatusAsync(CancellationToken ct)
    {
        if (!File.Exists(AgentPaths.Status))
            return null;

        StatusView? status;
        try
        {
            status = JsonSerializer.Deserialize<StatusView>(
                await File.ReadAllTextAsync(AgentPaths.Status, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (status is null)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"- Build budget left today: ${status.RemainingTodayUsd:0.00} of ${status.DailyBudgetUsd:0.00}");
        sb.AppendLine($"- Builds so far today: {status.BuildsToday}");
        sb.AppendLine(status.CooldownRemainingMinutes > 0
            ? $"- Cooldown: {status.CooldownRemainingMinutes} more minute(s) before another build can start"
            : "- Cooldown: clear");

        if (!status.CanRequestNow && !string.IsNullOrWhiteSpace(status.BlockedReason))
            sb.AppendLine($"- Cannot request right now: {status.BlockedReason}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>The agent's view of the supervisor's status file. A separate declaration from the
    /// supervisor's own, deliberately - these are two independent repositories and the agent should
    /// tolerate the file gaining fields it does not know about.</summary>
    private sealed record StatusView(
        decimal DailyBudgetUsd,
        decimal RemainingTodayUsd,
        int BuildsToday,
        int CooldownRemainingMinutes,
        bool CanRequestNow,
        string? BlockedReason);
}
