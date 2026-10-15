using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal static class CameraSettingsRegression
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "camera-preference-regressions", Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Project.sdpkg"); var store = new SceneCameraSettingsStore(root);
        var camera = new EditorViewportCamera { Orthographic = true, OrthographicSize = 7.25f, MoveSpeed = 12 };
        camera.Focus(new Vector3(2, 3, 4), 1); camera.Orient(EditorCameraOrientation.Top);
        camera.SetLens(60, 0.25f, 500);
        var state = camera.Capture(false); store.Save(package, "scene-one", state);
        var restarted = new SceneCameraSettingsStore(root); var loaded = restarted.Load(package, "scene-one");
        Require(loaded == state, "A fresh settings store must round-trip exact camera state and selection preference");
        var restored = new EditorViewportCamera(); restored.Restore(loaded!);
        Require(restored.Component.ViewMatrix == camera.Component.ViewMatrix && restored.Orientation == EditorCameraOrientation.Top
            && restored.Orthographic && restored.MoveSpeed == 12, "Restoration must recover the actual named camera view/mode/speed");
        Require(restarted.Load(package, "scene-two") == null && restarted.Load(package + ".copy", "scene-one") == null,
            "Scenes and copied projects must have independent camera preferences");
        var legacyPath = store.SettingsPath(package, "legacy");
        File.WriteAllText(legacyPath, System.Text.Json.JsonSerializer.Serialize(state with { Version = 1 }));
        var migrated = restarted.Load(package, "legacy");
        Require(migrated is { Version: 2, Fov: 45, Near: 0.1f, Far: 1000 } && migrated.Position == state.Position
            && migrated.Orientation == state.Orientation, "Version 1 camera preferences must migrate with default lens values and preserved view");
        var view = camera.Component.ViewMatrix;
        foreach (var invalid in new[] { (0f, 0.25f, 500f), (180f, 0.25f, 500f), (60f, -1f, 500f), (60f, 500f, 0.25f), (float.NaN, 0.25f, 500f) })
        {
            try { camera.SetLens(invalid.Item1, invalid.Item2, invalid.Item3); throw new Exception("Invalid lens should be rejected"); } catch (ArgumentException) { }
            Require(camera.Capture(false) == state && camera.Component.ViewMatrix == view, "Rejected lens edits must retain the complete valid camera state");
        }
        var path = store.SettingsPath(package, "scene-one"); var validBytes = File.ReadAllBytes(path);
        try { store.Save(package, "scene-one", state with { Size = float.NaN }); throw new Exception("Invalid state must be rejected"); }
        catch (ArgumentException) { }
        Require(File.ReadAllBytes(path).SequenceEqual(validBytes), "Rejected camera state must retain the last valid file");
        File.WriteAllText(path, "{ malformed");
        var damagedStore = new SceneCameraSettingsStore(root);
        Require(damagedStore.Load(package, "scene-one") == null, "Malformed settings must fall back without breaking scene loading");
        try { damagedStore.Save(package, "scene-one", state); throw new Exception("Unreadable settings should be preserved"); } catch (IOException) { }
        Require(File.ReadAllText(path) == "{ malformed", "Unreadable camera settings must not be overwritten");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(state with { Version = 99 }));
        var futureBytes = File.ReadAllBytes(path); var futureStore = new SceneCameraSettingsStore(root);
        Require(futureStore.Load(package, "scene-one") == null, "Unsupported camera settings versions must fall back");
        try { futureStore.Save(package, "scene-one", state); throw new Exception("Future settings should be preserved"); } catch (IOException) { }
        Require(File.ReadAllBytes(path).SequenceEqual(futureBytes), "Future-version settings must be preserved for migration");
        Console.WriteLine("CAMERA SETTINGS REGRESSION PASS: new-store round trip, real camera restore, project/scene isolation, invalid state preservation and malformed/future-version recovery.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
