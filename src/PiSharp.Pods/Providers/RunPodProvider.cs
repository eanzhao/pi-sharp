namespace PiSharp.Pods.Providers;

public sealed class RunPodProvider : IGpuProvider
{
    public string Name => "runpod";

    public string SetupInstructions =>
        "Attach a RunPod network volume to the pod first, then point PiSharp at the mounted path so models survive pod restarts.";

    public string DefaultMountCommand =>
        "mkdir -p /runpod-volume";

    public string RecommendedVolumeConfig =>
        "Attach a RunPod network volume and keep model data under /runpod-volume.";

    public string DefaultModelsPath => "/runpod-volume";
}
