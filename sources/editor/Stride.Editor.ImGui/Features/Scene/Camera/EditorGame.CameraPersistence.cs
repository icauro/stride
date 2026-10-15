namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private SceneCameraSettingsStore? cameraSettings;
    private SceneCameraSettingsStore CameraSettings => cameraSettings ??= new SceneCameraSettingsStore(
        args.Contains("--ui-test") || args.Contains("--verify-document") || args.Contains("--measure-ui-controls")
            ? Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "camera-settings", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "scene-camera"));
    private void SaveSceneCameraSettings()
    {
        if (IsRecoveryWorkspacePresentationRestoring) return;
        if (engineViewport?.PresentedDocument is not { } presented || workspace == null) return;
        SaveSceneMaterialSelectionState();
        SaveSceneTransformationViewState();
        try { CameraSettings.Save(PresentedCameraPackagePath(), presented.Asset.Id.ToString(), engineViewport.Camera.Capture(showSceneSelection) with
            { Grid = engineViewport.Grid?.Settings, CameraPreview = engineViewport.ShowCameraPreview,
                Transform = engineViewport.Gizmos?.CaptureSettings(), Components = engineViewport.ComponentGizmos?.CaptureSettings(),
                LightProbeVolumes = engineViewport.LightProbeVolumes?.Visible ?? false, LightProbeBounces = lightProbeBounces,
                VisibleNavigationGroups = engineViewport.CaptureVisibleNavigationGroups(), ActiveMaterialStream = engineViewport.ActiveMaterialStream });
            SaveNativeSceneDisplaySettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Console.Error.WriteLine("Camera preferences were not saved: " + error.Message); }
    }
    private void RestoreSceneCameraSettings()
    {
        RestoreSceneMaterialSelectionState();
        RestoreSceneTransformationViewState(true);
        var settings = CameraSettings.Load(PresentedCameraPackagePath(), (engineViewport?.PresentedDocument ?? document)!.Asset.Id.ToString());
        engineViewport!.Gizmos?.RestoreSettings(settings?.Transform ?? SceneTransformSettings.Default);
        if (settings?.Transform != null) configuredGizmos = engineViewport.Gizmos;
        engineViewport.ComponentGizmos?.RestoreSettings(settings?.Components ?? SceneComponentGizmoSettings.Default);
        if (engineViewport.LightProbeVolumes is { } volumes) volumes.Visible = settings?.LightProbeVolumes ?? false;
        lightProbeBounces = settings?.LightProbeBounces ?? 1;
        engineViewport.RestoreVisibleNavigationGroups(settings?.VisibleNavigationGroups ?? []);
        engineViewport.RestoreSceneRenderMode(settings?.ActiveMaterialStream);
        if (settings == null) { showSceneSelection = true; engineViewport!.ShowSelection = true; engineViewport.ShowCameraPreview = true; RestoreNativeSceneDisplaySettings(); return; }
        engineViewport!.Camera.Restore(settings); showSceneSelection = settings.Selection;
        engineViewport.Grid?.ApplySettings(settings.Grid ?? SceneGridSettings.Default);
        engineViewport.ShowSelection = showSceneSelection;
        engineViewport.ShowCameraPreview = settings.CameraPreview;
        RestoreNativeSceneDisplaySettings();
    }
    private string PresentedCameraPackagePath()
    {
        var presented = engineViewport?.PresentedDocument ?? document;
        if (presented == null) return workspace!.PackagePath;
        var package = workspace!.FindAsset(presented.Asset.Id).Package;
        // Native read-only library generations may be backed by private mirrors;
        // the real session path plus stable asset ID survives mirror replacement.
        return package.IsReadOnly ? workspace.OpenPath : package.FullPath.ToOSPath();
    }
}
