using Microsoft.JSInterop;

namespace PiSharp.WebUi.Tests.Support;

internal sealed class FakeJsRuntime : IJSRuntime
{
    private readonly Dictionary<string, FakeJsModule> _modules = new(StringComparer.Ordinal);

    public Dictionary<string, string> LocalStorage { get; } = new(StringComparer.Ordinal);

    public void RegisterModule(string path, FakeJsModule module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(module);

        _modules[path] = module;
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();

        object? result = identifier switch
        {
            "import" => _modules[(string)args![0]!],
            "localStorage.getItem" => LocalStorage.TryGetValue((string)args![0]!, out var value) ? value : null,
            "localStorage.setItem" => SetLocalStorageValue(args),
            "localStorage.removeItem" => RemoveLocalStorageValue(args),
            _ => throw new InvalidOperationException($"Unsupported JS runtime invocation '{identifier}'."),
        };

        return ValueTask.FromResult(ConvertResult<TValue>(result));
    }

    private object? SetLocalStorageValue(object?[]? args)
    {
        LocalStorage[(string)args![0]!] = (string)args[1]!;
        return null;
    }

    private object? RemoveLocalStorageValue(object?[]? args)
    {
        LocalStorage.Remove((string)args![0]!);
        return null;
    }

    private static TValue ConvertResult<TValue>(object? result)
    {
        if (typeof(TValue) == typeof(IJSObjectReference))
        {
            return (TValue)result!;
        }

        if (typeof(TValue).Name == "IJSVoidResult")
        {
            return default!;
        }

        if (result is null)
        {
            return default!;
        }

        return (TValue)result;
    }
}

internal sealed class FakeJsModule : IJSObjectReference
{
    private readonly Dictionary<string, Func<object?[]?, object?>> _handlers = new(StringComparer.Ordinal);

    public void Register(string identifier, Func<object?[]?, object?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[identifier] = handler;
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier,
        CancellationToken cancellationToken,
        object?[]? args)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_handlers.TryGetValue(identifier, out var handler))
        {
            throw new InvalidOperationException($"Unsupported JS module invocation '{identifier}'.");
        }

        var result = handler(args);
        if (typeof(TValue).Name == "IJSVoidResult")
        {
            return ValueTask.FromResult(default(TValue)!);
        }

        if (result is null)
        {
            return ValueTask.FromResult(default(TValue)!);
        }

        return ValueTask.FromResult((TValue)result);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
