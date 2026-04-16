namespace PiSharp.Pods.Providers;

public sealed class VastAiProvider : IGpuProvider
{
    public string Name => "vastai";

    public string SetupInstructions =>
        "Provision a persistent Vast.ai volume and mount it into the instance before setup so model downloads and logs remain available across reboots.";

    public string DefaultMountCommand =>
        "mkdir -p /workspace && test -d /workspace";

    public string RecommendedVolumeConfig =>
        "Use a persistent Vast.ai volume rooted at /workspace for models and runtime state.";

    public string DefaultModelsPath => "/workspace";
}
