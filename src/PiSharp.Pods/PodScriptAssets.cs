using System.Reflection;

namespace PiSharp.Pods;

internal static class PodScriptAssets
{
    private const string PodSetupResourceName = "PiSharp.Pods.Scripts.pod_setup.sh";
    private const string ModelRunResourceName = "PiSharp.Pods.Scripts.model_run.sh";

    public static string LoadPodSetupScript() => LoadRequiredText(PodSetupResourceName);

    public static string LoadModelRunScript() => LoadRequiredText(ModelRunResourceName);

    private static string LoadRequiredText(string resourceName)
    {
        using var stream = typeof(PodScriptAssets).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded script resource '{resourceName}' was not found.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
