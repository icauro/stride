using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed record CameraVector(float X, float Y, float Z)
{
    public static CameraVector From(Vector3 value) => new(value.X, value.Y, value.Z);
    public Vector3 Vector => new(X, Y, Z);
    public bool Finite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}
internal sealed record SceneCameraSettings(int Version, CameraVector Position, CameraVector Target,
    float Yaw, float Pitch, float Radius, float Speed, bool Orthographic, float Size,
    EditorCameraOrientation? Orientation, bool Selection, float Fov = 45, float Near = 0.1f, float Far = 1000, SceneGridSettings? Grid = null, bool CameraPreview = true,
    SceneTransformSettings? Transform = null, SceneComponentGizmoSettings? Components = null,
    bool LightProbeVolumes = false, int LightProbeBounces = 1, Guid[]? VisibleNavigationGroups = null, float SceneUnit = 1, string ActiveMaterialStream = "")
{
    public bool Valid => Version == 2 && Position != null && Target != null && Position.Finite && Target.Finite
        && float.IsFinite(Yaw) && float.IsFinite(Pitch) && Math.Abs(Pitch) <= MathUtil.PiOverTwo
        && float.IsFinite(Radius) && Radius > 0 && float.IsFinite(Speed) && Speed >= 0.01f && Speed <= 10000
        && float.IsFinite(Size) && Size > 0
        && (Orientation == null || Enum.IsDefined(Orientation.Value)) && CameraLens.Valid(Fov, Near, Far) && (Grid == null || Grid.Valid)
        && (Transform == null || Transform.Valid) && (Components == null || Components.Valid) && LightProbeBounces >= 0
        && float.IsFinite(SceneUnit) && SceneUnit > MathUtil.ZeroTolerance && ActiveMaterialStream != null;
}

/// <summary>Local, scene-identity-scoped editor preferences; never writes authored project files.</summary>
internal sealed class SceneCameraSettingsStore(string root)
{
    private readonly HashSet<string> blocked = [];
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    internal string SettingsPath(string packagePath, string assetId)
    {
        var identity = Path.GetFullPath(packagePath).ToUpperInvariant() + "\n" + assetId;
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }
    public SceneCameraSettings? Load(string packagePath, string assetId)
    {
        var path = SettingsPath(packagePath, assetId);
        if (!File.Exists(path)) return null;
        try
        {
            var settings = JsonSerializer.Deserialize<SceneCameraSettings>(File.ReadAllBytes(path), Options);
            if (settings?.Version == 1) settings = settings with { Version = 2, Fov = 45, Near = 0.1f, Far = 1000 };
            if (settings?.Valid == true) return settings;
            blocked.Add(path); Console.Error.WriteLine("Camera preferences are invalid or from an unsupported version: " + path);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { blocked.Add(path); Console.Error.WriteLine("Camera preferences could not be read: " + error.Message); }
        return null;
    }
    public void Save(string packagePath, string assetId, SceneCameraSettings settings)
    {
        if (!settings.Valid) throw new ArgumentException("Camera preferences must contain a supported finite state.");
        var path = SettingsPath(packagePath, assetId);
        if (blocked.Contains(path)) throw new IOException("Preserving unreadable or unsupported camera preferences: " + path);
        Directory.CreateDirectory(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
