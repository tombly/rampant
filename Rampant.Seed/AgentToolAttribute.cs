namespace Rampant.Agent;

/// <summary>
/// Marks a method as a tool the model may call. This is Rampant's own contract, not a framework's,
/// which is deliberate: the tools this agent builds for itself should belong to it rather than
/// being artifacts of whichever LLM library it happens to sit on. The library underneath
/// (Microsoft.Extensions.AI) is a provider-neutral chat abstraction and can be swapped without any
/// tool changing.
///
/// The full contract a tool class has to satisfy is enforced in <see cref="ToolLoader"/> and
/// described in SELF.md. A class that gets it wrong is not loaded, and nothing crashes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AgentToolAttribute(string name) : Attribute
{
    /// <summary>The name the model sees. snake_case by convention.</summary>
    public string Name { get; } = name;
}
