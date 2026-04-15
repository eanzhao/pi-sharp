using System.Reflection;
using System.Runtime.Loader;

namespace PiSharp.CodingAgent;

public static class ExtensionLoader
{
    public static IReadOnlyList<ICodingAgentExtension> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<ICodingAgentExtension>();
        }

        var extensions = new List<ICodingAgentExtension>();

        foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var loaded = LoadFromAssembly(dllPath);
            extensions.AddRange(loaded);
        }

        return extensions;
    }

    public static IReadOnlyList<ICodingAgentExtension> LoadFromAssembly(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);

        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            return Array.Empty<ICodingAgentExtension>();
        }

        try
        {
            var loadContext = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(fullPath), isCollectible: true);
            var assembly = loadContext.LoadFromAssemblyPath(fullPath);
            return InstantiateExtensions(assembly);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException)
        {
            return Array.Empty<ICodingAgentExtension>();
        }
    }

    public static IReadOnlyList<ICodingAgentExtension> LoadFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return InstantiateExtensions(assembly);
    }

    private static IReadOnlyList<ICodingAgentExtension> InstantiateExtensions(Assembly assembly)
    {
        var extensions = new List<ICodingAgentExtension>();
        var extensionInterface = typeof(ICodingAgentExtension);

        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            exportedTypes = ex.Types.Where(t => t is not null).ToArray()!;
        }

        foreach (var type in exportedTypes)
        {
            if (type.IsAbstract || type.IsInterface || !extensionInterface.IsAssignableFrom(type))
            {
                continue;
            }

            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor is null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is ICodingAgentExtension extension)
                {
                    extensions.Add(extension);
                }
            }
            catch
            {
                // skip types that fail to instantiate
            }
        }

        return extensions;
    }
}
