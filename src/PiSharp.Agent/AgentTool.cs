using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Agent;

public delegate ValueTask AgentToolUpdateCallback(AgentToolResult partialResult);

public sealed record AgentToolResult(
    object? Value,
    IReadOnlyList<AIContent> Content,
    object? Details = null)
{
    private static readonly JsonSerializerOptions DefaultSerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static AgentToolResult FromText(string text, object? details = null) =>
        new(text, [new TextContent(text)], details ?? text);

    public static AgentToolResult FromValue(
        object? value,
        JsonSerializerOptions? serializerOptions = null,
        object? details = null) =>
        new(value, CreateDisplayContent(value, serializerOptions), details ?? value);

    private static IReadOnlyList<AIContent> CreateDisplayContent(
        object? value,
        JsonSerializerOptions? serializerOptions)
    {
        switch (value)
        {
            case null:
                return Array.Empty<AIContent>();

            case string text:
                return [new TextContent(text)];

            case AIContent content:
                return [content];

            case IEnumerable<AIContent> contents:
                return contents.ToArray();

            default:
                var json = JsonSerializer.Serialize(value, serializerOptions ?? DefaultSerializerOptions);
                return [new TextContent(json)];
        }
    }
}

public sealed class AgentTool
{
    private readonly Func<string, AIFunctionArguments, AgentToolUpdateCallback?, CancellationToken, ValueTask<AgentToolResult>> _executeAsync;

    public AgentTool(
        AIFunction function,
        string? label = null,
        Func<AIFunctionArguments, AIFunctionArguments>? prepareArguments = null,
        Func<string, AIFunctionArguments, AgentToolUpdateCallback?, CancellationToken, ValueTask<AgentToolResult>>? executeAsync = null)
    {
        Function = function ?? throw new ArgumentNullException(nameof(function));
        Label = string.IsNullOrWhiteSpace(label) ? Function.Name : label;
        PrepareArguments = prepareArguments;
        _executeAsync = executeAsync ?? DefaultExecuteAsync;
    }

    public string Name => Function.Name;

    public string Label { get; }

    public AIFunction Function { get; }

    public Func<AIFunctionArguments, AIFunctionArguments>? PrepareArguments { get; }

    public ValueTask<AgentToolResult> ExecuteAsync(
        string toolCallId,
        AIFunctionArguments arguments,
        AgentToolUpdateCallback? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(arguments);

        return _executeAsync(toolCallId, arguments, onUpdate, cancellationToken);
    }

    public static AgentTool Create(
        Delegate handler,
        string? name = null,
        string? description = null,
        string? label = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var options = new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
        };

        return new AgentTool(AIFunctionFactory.Create(handler, options), label);
    }

    private async ValueTask<AgentToolResult> DefaultExecuteAsync(
        string _,
        AIFunctionArguments arguments,
        AgentToolUpdateCallback? __,
        CancellationToken cancellationToken)
    {
        var result = await Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        return AgentToolResult.FromValue(result, Function.JsonSerializerOptions);
    }
}
