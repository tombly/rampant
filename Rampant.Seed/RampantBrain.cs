using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Rampant.Agent;

public sealed record BrainResult(bool Success, string Reply);

/// <summary>The agent's actual conversational mind: a direct Anthropic Messages API call with a
/// small, curated tool set, replacing what used to be a full Claude Code session on every single
/// message. Claude Code is no longer "you" for ordinary conversation - it's demoted to a single
/// tool (<see cref="ExtendSelfAsync"/>) invoked only when a request genuinely needs your own
/// source code to change. Everything else (remembering/recalling a fact, looking something up)
/// is handled directly here, with no coding-agent session at all.
///
/// A manual tool loop, not the SDK's beta ToolRunner - this keeps the whole request/response shape
/// explicit and avoids a beta dependency for a loop this small. Web search/fetch are Anthropic's
/// own server-side tools (resolved inline by the API, never surfacing a client-side ToolUseBlock);
/// recall/remember/extend_self are the only tools this class actually executes.</summary>
public sealed class RampantBrain(ClaudeCodeRunner claudeCode)
{
    private const string MemoryDir = "/workspace/memory";
    private const int MaxToolIterations = 10;

    private readonly AnthropicClient _client = new();

    public async Task<BrainResult> HandleAsync(string selfMd, string userMessage, CancellationToken ct)
    {
        var model = Environment.GetEnvironmentVariable("RAMPANT_MODEL") ?? "claude-sonnet-5";

        List<MessageParam> messages = [new() { Role = Role.User, Content = userMessage }];

        for (var iteration = 0; iteration < MaxToolIterations; iteration++)
        {
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 4096,
                System = selfMd,
                Tools = [
                    new ToolUnion(new WebSearchTool20260209()),
                    new ToolUnion(new WebFetchTool20260209()),
                    RecallTool(),
                    RememberTool(),
                    ExtendSelfTool(),
                ],
                Messages = messages,
            };

            Message response;
            try
            {
                response = await _client.Messages.Create(parameters, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                return new BrainResult(false, $"Hit an error talking to Claude: {ex.Message}");
            }

            if (response.StopReason == "pause_turn")
            {
                // Server-side tool (web search/fetch) hit its internal iteration cap - resend the
                // same transcript with no new user turn and the server picks up where it left off.
                messages = [.. messages, EchoAssistantTurn(response)];
                continue;
            }

            if (response.StopReason != "tool_use")
                return new BrainResult(true, ExtractText(response));

            List<ContentBlockParam> toolResults = [];
            foreach (var block in response.Content)
            {
                if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    var (content, isError) = await ExecuteToolAsync(toolUse.Name, toolUse.Input, selfMd, ct);
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = content, IsError = isError });
                }
            }

            messages = [.. messages, EchoAssistantTurn(response), new() { Role = Role.User, Content = toolResults }];
        }

        return new BrainResult(false, "Ran out of tool-use iterations without reaching a final answer.");
    }

    private static string ExtractText(Message response)
        => string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

    /// <summary>Reconstructs the assistant's turn as a follow-up message. There's no
    /// response-to-param helper, so every block variant is rebuilt by hand - see the tool-use
    /// conversion pattern in the Claude API docs. Any block type not handled here is dropped from
    /// the echoed turn rather than failing the cycle; that's an acceptable quality trade-off for a
    /// first cut of this loop, not a correctness one (the model still sees everything on the next
    /// send from a fresh transcript, since the raw conversation isn't otherwise reused).</summary>
    private static MessageParam EchoAssistantTurn(Message response)
    {
        List<ContentBlockParam> assistantContent = [];
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
                assistantContent.Add(new TextBlockParam { Text = text.Text });
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
        }

        return new MessageParam { Role = Role.Assistant, Content = assistantContent };
    }

    private async Task<(string Content, bool IsError)> ExecuteToolAsync(
        string name, IReadOnlyDictionary<string, JsonElement> input, string selfMd, CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "recall" => (await RecallAsync(input, ct), false),
                "remember" => (await RememberAsync(input, ct), false),
                "extend_self" => await ExtendSelfAsync(input, selfMd, ct),
                _ => ($"Unknown tool: {name}", true),
            };
        }
        catch (Exception ex)
        {
            return ($"Tool '{name}' failed: {ex.Message}", true);
        }
    }

    private static async Task<string> RecallAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var filename = input.TryGetValue("filename", out var f) ? f.GetString() : null;

        Directory.CreateDirectory(MemoryDir);
        if (string.IsNullOrWhiteSpace(filename))
        {
            var files = Directory.GetFiles(MemoryDir).Select(Path.GetFileName).ToList();
            return files.Count > 0 ? $"Memory files: {string.Join(", ", files)}" : "No memory files exist yet.";
        }

        var path = ResolveMemoryPath(filename);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : $"No memory file named '{filename}' exists yet.";
    }

    private static async Task<string> RememberAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var filename = input.TryGetValue("filename", out var f) ? f.GetString() : null;
        var content = input.TryGetValue("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Both 'filename' and 'content' are required.");

        var path = ResolveMemoryPath(filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, content.TrimEnd() + Environment.NewLine, ct);
        return $"Saved to {filename}.";
    }

    private static string ResolveMemoryPath(string filename)
    {
        var root = Path.GetFullPath(MemoryDir);
        var full = Path.GetFullPath(Path.Combine(root, filename));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("filename must stay within /workspace/memory.");
        return full;
    }

    /// <summary>The one bridge from ordinary conversation back to self-modification. Delegates to
    /// the same <see cref="ClaudeCodeRunner"/> that used to run on every message - Claude Code is
    /// still the agent's "hands" for actually rewriting its own source, just no longer invoked for
    /// requests that don't need it.</summary>
    private async Task<(string, bool)> ExtendSelfAsync(IReadOnlyDictionary<string, JsonElement> input, string selfMd, CancellationToken ct)
    {
        var task = input.TryGetValue("task", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(task))
            return ("'task' is required.", true);

        var prompt = $"""
            {selfMd}

            You're being invoked as Rampant's self-modification tool, not as the conversational
            agent itself - a separate direct-API loop handles ordinary messages and calls you only
            when a request needs your own source code to change. Here's the task:

            {task}

            Extend your own source under /workspace/agent to add whatever's missing, and - where
            possible - also carry out the underlying action in this same session (e.g. write the
            new persistence code AND record today's actual fact/result), rather than only deferring
            to a future cycle. Commit your changes when done; the external supervisor picks up the
            new commit automatically.

            Reply with a short summary of what you did or what's still missing - this becomes a
            tool result fed back to the conversational loop, not a message straight to the owner.
            """;

        var result = await claudeCode.RunAsync(prompt, ct);
        return result.Success
            ? (result.Output, false)
            : ($"Self-extension attempt failed: {result.ErrorOutput}", true);
    }

    private static Tool RecallTool() => new()
    {
        Name = "recall",
        Description = "Look up something you've remembered before. Reads from your persistent " +
            "memory store under /workspace/memory, including the running conversation history " +
            "log. Call with no filename to list what memory files exist; call with a filename to " +
            "read its contents.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["filename"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "Name of a file under /workspace/memory to read. Omit to list available files.",
                }),
            },
        },
    };

    private static Tool RememberTool() => new()
    {
        Name = "remember",
        Description = "Save a fact or note for later so you can recall it in a future " +
            "conversation. Appends to a file under /workspace/memory (creating it if needed) - " +
            "use a consistent filename like 'notes.md' unless you have a specific reason for a " +
            "different one.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["filename"] = JsonSerializer.SerializeToElement(new { type = "string", description = "File under /workspace/memory to append to." }),
                ["content"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The fact or note to save." }),
            },
            Required = ["filename", "content"],
        },
    };

    private static Tool ExtendSelfTool() => new()
    {
        Name = "extend_self",
        Description = "Call this when the request needs a capability you don't have yet - i.e. " +
            "your own source code needs to change (a new persistence mechanism, a new " +
            "integration, new behavior). Hands the task to a full coding agent (Claude Code) that " +
            "can read and rewrite your entire source tree under /workspace/agent, run builds, and " +
            "commit. It does not take effect instantly: the commit takes effect once the external " +
            "supervisor rebuilds and restarts you. Reserve this for genuine capability gaps - use " +
            "recall/remember first if the request is really just about looking something up or " +
            "saving a note.",
        InputSchema = new()
        {
            Properties = new Dictionary<string, JsonElement>
            {
                ["task"] = JsonSerializer.SerializeToElement(new
                {
                    type = "string",
                    description = "A clear description of the capability gap and what to build, " +
                        "plus the original request so the coding agent has full context. Ask it " +
                        "to also carry out the requested action immediately where possible, not " +
                        "just add the capability for next time.",
                }),
            },
            Required = ["task"],
        },
    };
}
