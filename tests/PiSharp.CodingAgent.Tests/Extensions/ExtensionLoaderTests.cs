using System.Reflection;
using PiSharp.CodingAgent;

namespace PiSharp.CodingAgent.Tests;

public sealed class ExtensionLoaderTests
{
    [Fact]
    public void LoadFromDirectory_ReturnsEmpty_WhenDirectoryDoesNotExist()
    {
        var result = ExtensionLoader.LoadFromDirectory("/nonexistent/path");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromAssembly_ReturnsEmpty_WhenFileDoesNotExist()
    {
        var result = ExtensionLoader.LoadFromAssembly("/nonexistent/test.dll");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadFromAssembly_FindsExtensionsInCurrentAssembly()
    {
        var assembly = typeof(ExtensionLoaderTests).Assembly;

        var result = ExtensionLoader.LoadFromAssembly(assembly);

        Assert.Contains(result, ext => ext is TestExtension);
    }

    [Fact]
    public void LoadFromAssembly_SkipsAbstractAndInterfaceTypes()
    {
        var assembly = typeof(ExtensionLoaderTests).Assembly;

        var result = ExtensionLoader.LoadFromAssembly(assembly);

        Assert.DoesNotContain(result, ext => ext.GetType().IsAbstract);
    }

    [Fact]
    public void LoadFromAssembly_SkipsTypesWithoutParameterlessConstructor()
    {
        var assembly = typeof(ExtensionLoaderTests).Assembly;

        var result = ExtensionLoader.LoadFromAssembly(assembly);

        Assert.DoesNotContain(result, ext => ext is ExtensionWithConstructorArgs);
    }
}

public sealed class TestExtension : ICodingAgentExtension
{
}

public sealed class ExtensionWithConstructorArgs : ICodingAgentExtension
{
    public ExtensionWithConstructorArgs(string required)
    {
        _ = required;
    }
}
