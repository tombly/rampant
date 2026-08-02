using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;

namespace Rampant.Agent;

/// <summary>
/// Finds every self-built tool and hands it to the model. This is the seam the whole design rests
/// on: a new capability is one file dropped into Tools/, discovered here, with no change to any
/// other file. Get that right and changes almost never need the owner's approval; get it wrong and
/// the agent is constantly blocked from things it legitimately needs.
///
/// Discovery is by reflection over this assembly rather than by any registration list, because a
/// registration list is a core file - it would mean every new tool also edited protected code, and
/// every new tool would wait on a human.
///
/// Failures here are quiet on purpose. A malformed tool class is skipped and the agent keeps
/// running with the tools that do load; a loader that threw would let one bad self-built tool take
/// down the only channel the owner has for telling it so.
/// </summary>
public static class ToolLoader
{
    private const string ToolNamespace = "Rampant.Agent.Tools";

    public static IReadOnlyList<AIFunction> Load(Action<string>? onSkipped = null)
    {
        var functions = new List<AIFunction>();

        foreach (var type in DiscoverToolTypes())
        {
            object instance;
            try
            {
                instance = Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                onSkipped?.Invoke($"{type.Name}: could not be constructed ({ex.GetBaseException().Message})");
                continue;
            }

            functions.AddRange(CreateFunctions(instance, type, onSkipped));
        }

        return functions;
    }

    /// <summary>Wraps the [AgentTool] methods of an already-constructed object. Used for the core
    /// tools, which need per-turn context and so cannot be discovered as parameterless
    /// classes.</summary>
    public static IReadOnlyList<AIFunction> FromInstance(object instance, Action<string>? onSkipped = null)
        => CreateFunctions(instance, instance.GetType(), onSkipped);

    private static IEnumerable<Type> DiscoverToolTypes()
    {
        Type[] types;
        try
        {
            types = typeof(ToolLoader).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>().ToArray();
        }

        return types.Where(t =>
            t is { IsClass: true, IsAbstract: false, IsPublic: true }
            && t.Namespace == ToolNamespace
            && t.GetConstructor(Type.EmptyTypes) is not null
            && t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.GetCustomAttribute<AgentToolAttribute>() is not null));
    }

    private static List<AIFunction> CreateFunctions(object instance, Type type, Action<string>? onSkipped)
    {
        var functions = new List<AIFunction>();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.GetCustomAttribute<AgentToolAttribute>() is not { } tool)
                continue;

            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                // A tool with no description is worse than no tool: the model will call it and
                // guess at what it does.
                onSkipped?.Invoke($"{type.Name}.{method.Name}: missing [Description]");
                continue;
            }

            try
            {
                functions.Add(AIFunctionFactory.Create(method, instance, new AIFunctionFactoryOptions
                {
                    Name = tool.Name,
                    Description = description,
                }));
            }
            catch (Exception ex)
            {
                onSkipped?.Invoke($"{type.Name}.{method.Name}: {ex.GetBaseException().Message}");
            }
        }

        return functions;
    }
}
