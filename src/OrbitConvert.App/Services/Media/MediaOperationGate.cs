namespace OrbitConvert.App.Services.Media;

/// <summary>One external-media operation at a time; no batch queue or persisted jobs.</summary>
internal static class MediaOperationGate
{
    internal static readonly SemaphoreSlim Instance = new(1, 1);
}
