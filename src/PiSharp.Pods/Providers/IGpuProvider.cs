namespace PiSharp.Pods.Providers;

public interface IGpuProvider
{
    string Name { get; }

    string SetupInstructions { get; }

    string DefaultMountCommand { get; }

    string RecommendedVolumeConfig { get; }

    string DefaultModelsPath { get; }
}
