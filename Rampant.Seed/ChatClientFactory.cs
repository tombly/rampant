using Microsoft.Extensions.AI;
using OpenAI;

namespace Rampant.Agent;

/// <summary>
/// Builds the chat client. Deliberately thin, and deliberately on a provider-neutral abstraction:
/// <see cref="IChatClient"/> comes from Microsoft.Extensions.AI, so swapping the model provider is
/// a change to this one file rather than to anything a tool touches.
///
/// That neutrality is load-bearing rather than decorative. This agent builds its own capabilities,
/// and if the LLM library it sat on also supplied web search, memory, and code execution as
/// built-ins, there would be no way to tell whether something impressive was built here or came
/// out of a vendor's box. The chat client does exactly one thing: pass messages and call the tools
/// it is given.
/// </summary>
public static class ChatClientFactory
{
    private const string DefaultModel = "gpt-5.6-luna";

    /// <summary>Bounds one turn's tool loop. A tool that returns something confusing can otherwise
    /// keep the model calling it, and every iteration is a paid round trip.</summary>
    private const int MaxToolIterations = 8;

    public static IChatClient Create()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        var model = Environment.GetEnvironmentVariable("RAMPANT_OPENAI_MODEL") ?? DefaultModel;

        // The Responses API (/v1/responses), not Chat Completions. Confirmed live on the Pi: a
        // reasoning model rejects function tools on /v1/chat/completions outright -
        //   "Function tools with reasoning_effort are not supported for gpt-5.6-luna in
        //    /v1/chat/completions. To use function tools, use /v1/responses or set
        //    reasoning_effort to 'none'."
        // The other branch, turning reasoning off, is the wrong trade for an agent whose entire
        // job is judgement about what is worth building.
        //
        // OPENAI001 is suppressed because Microsoft.Extensions.AI's Responses wrapper is still
        // marked evaluation-only. Worth knowing when bumping the package: this file is core, so a
        // breaking change here needs an owner-approved deploy rather than shipping as a tool.
#pragma warning disable OPENAI001
        return new OpenAIClient(apiKey)
            .GetResponsesClient()
            .AsIChatClient(model)
#pragma warning restore OPENAI001
            .AsBuilder()
            // Runs the tool loop: the model asks for a tool, this invokes it, feeds the result
            // back, and repeats until the model answers in words.
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = MaxToolIterations)
            .Build();
    }
}
