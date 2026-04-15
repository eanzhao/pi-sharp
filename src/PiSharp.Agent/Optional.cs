namespace PiSharp.Agent;

public readonly record struct Optional<T>
{
    public Optional(T? value)
    {
        HasValue = true;
        Value = value;
    }

    public bool HasValue { get; }

    public T? Value { get; }

    public static Optional<T> Unset => default;

    public static implicit operator Optional<T>(T? value) => new(value);
}
