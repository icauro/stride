namespace Stride.Assets.Presentation.AssetEditors.Gizmos;

internal static class GizmoResources
{
    private static readonly Dictionary<string, byte[]> Cache = [];
    private static byte[] Read(string name)
    {
        if (Cache.TryGetValue(name, out var bytes)) return bytes;
        using var source = typeof(GizmoResources).Assembly.GetManifestResourceStream("Stride.Editor.ImGui.Gizmos." + name + ".png")
            ?? throw new InvalidOperationException("Missing editor gizmo image: " + name);
        using var destination = new MemoryStream(); source.CopyTo(destination);
        return Cache[name] = destination.ToArray();
    }
    internal static byte[] CameraGizmo => Read(nameof(CameraGizmo));
    internal static byte[] AudioEmitterGizmo => Read("AudioGizmo");
    internal static byte[] AudioListenerGizmo => Read(nameof(AudioListenerGizmo));
    internal static byte[] SunLightGizmo => Read(nameof(SunLightGizmo));
    internal static byte[] PointLightGizmo => Read(nameof(PointLightGizmo));
    internal static byte[] SpotLightGizmo => Read(nameof(SpotLightGizmo));
    internal static byte[] AmbientLightGizmo => Read(nameof(AmbientLightGizmo));
    internal static byte[] SkyboxLightGizmo => Read(nameof(SkyboxLightGizmo));
    internal static byte[] BackgroundGizmo => Read(nameof(BackgroundGizmo));
    internal static byte[] ScriptGizmo => Read(nameof(ScriptGizmo));
    internal static byte[] UIGizmo => Read(nameof(UIGizmo));
    internal static byte[] NodeLinkGizmo => Read(nameof(NodeLinkGizmo));
    internal static byte[] FallbackGizmo => Read(nameof(FallbackGizmo));
    internal static byte[] ParticleGizmo => Read(nameof(ParticleGizmo));
}
