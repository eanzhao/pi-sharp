namespace PiSharp.Pods.Providers;

public sealed class DataCrunchProvider : IGpuProvider
{
    public string Name => "datacrunch";

    public string SetupInstructions =>
        "Attach an NFS-backed workspace, verify the mount target is reachable over the private network, then run setup with the recommended mount command.";

    public string DefaultMountCommand =>
        "mkdir -p /workspace && mount -t nfs -o vers=4.1,noresvport,nolock ${DATACRUNCH_NFS_HOST}:/ /workspace";

    public string RecommendedVolumeConfig =>
        "Use a shared NFS export mounted at /workspace for model weights, tokenizer caches, and vLLM artifacts.";

    public string DefaultModelsPath => "/workspace";
}
