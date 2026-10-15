using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Engine.Processors;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void RestoreNativeSceneDisplaySettings(SceneToolbarContext? context = null)
    {
        if (context != null && !context.Current()) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        var workspace = context?.View.Workspace ?? this.workspace;
        var showSceneSelection = context == null ? this.showSceneSelection : engineViewport!.ShowSelection;
        var lightProbeBounces = context?.LightProbeBounces ?? this.lightProbeBounces;
        if (workspace == null || engineViewport?.PresentedDocument is not { } presented) return;
        var package = workspace.FindAsset(presented.Asset.Id).Package;
        var values = package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
        if (!values.TryGetValue(presented.Asset.Id, out var native)) return;
        lightProbeBounces = Math.Max(0, native.LightProbeBounces);
        if (context == null) this.lightProbeBounces = lightProbeBounces; else context.LightProbeBounces = lightProbeBounces;
        if (engineViewport.LightProbeVolumes is { } volumes) volumes.Visible = native.LightProbeWireframe;
        engineViewport.RestoreVisibleNavigationGroups(native.VisibleNavigationGroups ?? []);
        engineViewport.RestoreSceneRenderMode(native.ActiveStream);
        var camera = engineViewport.Camera;
        var current = camera.Capture(showSceneSelection);
        var settings = current with
        {
            Position = CameraVector.From(native.CamPosition), Yaw = native.CamPitchYaw.Y, Pitch = native.CamPitchYaw.X,
            Speed = native.CamMoveSpeed, Orthographic = native.CamProjection == CameraProjectionMode.Orthographic,
            Size = native.CamOrthographicSize, Fov = native.CamVerticalFieldOfView,
            Near = native.CamNearClipPlane, Far = native.CamFarClipPlane,
            SceneUnit = native.SceneUnit <= MathUtil.ZeroTolerance ? 1 : native.SceneUnit,
            Orientation = null, Selection = native.SelectionMaskVisible,
        };
        if (settings.Valid) camera.Restore(settings);
        showSceneSelection = native.SelectionMaskVisible; engineViewport.ShowSelection = showSceneSelection;
        if (context == null) this.showSceneSelection = showSceneSelection;
        engineViewport.ShowCameraPreview = native.CameraPreviewVisible;
        RestoreNativeSceneToolsSettings(native, context);
    }
    private void SaveNativeSceneDisplaySettings(SceneToolbarContext? context = null, bool retiring = false)
    {
        if (context != null && !(retiring ? context.OwnerCurrent() : context.Current())) return;
        var engineViewport = context?.Viewport ?? this.engineViewport;
        var workspace = context?.View.Workspace ?? this.workspace;
        var showSceneSelection = context == null ? this.showSceneSelection : engineViewport!.ShowSelection;
        var lightProbeBounces = context?.LightProbeBounces ?? this.lightProbeBounces;
        if (workspace == null || sessionHistory == null || engineViewport?.PresentedDocument is not { } presented) return;
        var package = workspace.FindAsset(presented.Asset.Id).Package;
        if (package.IsReadOnly) return;
        var values = package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
        var visible = engineViewport.CaptureVisibleNavigationGroups();
        var wireframe = engineViewport.LightProbeVolumes?.Visible ?? false;
        var camera = engineViewport.Camera;
        if (values.TryGetValue(presented.Asset.Id, out var previous) && previous.LightProbeWireframe == wireframe
            && NativeSceneToolsMatch(previous, context) && previous.LightProbeBounces == lightProbeBounces && (previous.VisibleNavigationGroups ?? []).Order().SequenceEqual(visible)
            && previous.CamPosition == camera.Position && previous.CamPitchYaw == new Vector2(camera.Pitch, camera.Yaw)
            && previous.CamProjection == camera.Component.Projection && previous.CamOrthographicSize == camera.OrthographicSize
            && previous.CamVerticalFieldOfView == camera.Component.VerticalFieldOfView && previous.CamNearClipPlane == camera.Component.NearClipPlane
            && previous.CamFarClipPlane == camera.Component.FarClipPlane && previous.CamMoveSpeed == camera.MoveSpeed && previous.SceneUnit == camera.SceneUnit
            && previous.ActiveStream == engineViewport.ActiveMaterialStream && previous.RenderMode == engineViewport.NativeRenderMode
            && previous.SelectionMaskVisible == showSceneSelection && previous.CameraPreviewVisible == engineViewport.ShowCameraPreview) return;
        // Clone the actual dictionary/entries, preserving all unrelated camera, load, snap and extension values.
        var next = (Dictionary<AssetId, SceneSettingsData>)AssetCloner.Clone(values, AssetClonerFlags.None, out _)!;
        if (!next.TryGetValue(presented.Asset.Id, out var entry)) next.Add(presented.Asset.Id, entry = SceneSettingsData.CreateDefault());
        CaptureNativeSceneToolsSettings(entry, context);
        entry.LightProbeWireframe = wireframe; entry.LightProbeBounces = lightProbeBounces; entry.VisibleNavigationGroups = visible.ToList();
        entry.CamPosition = camera.Position; entry.CamPitchYaw = new Vector2(camera.Pitch, camera.Yaw);
        entry.CamProjection = camera.Component.Projection; entry.CamOrthographicSize = camera.OrthographicSize;
        entry.CamVerticalFieldOfView = camera.Component.VerticalFieldOfView; entry.CamNearClipPlane = camera.Component.NearClipPlane;
        entry.CamFarClipPlane = camera.Component.FarClipPlane; entry.CamMoveSpeed = camera.MoveSpeed; entry.SceneUnit = camera.SceneUnit;
        entry.ActiveStream = engineViewport.ActiveMaterialStream; entry.RenderMode = engineViewport.NativeRenderMode;
        entry.SelectionMaskVisible = showSceneSelection; entry.CameraPreviewVisible = engineViewport.ShowCameraPreview;
        var owner = GetPackageSettingsDocument(package, sessionHistory);
        owner.SaveAutomaticPreference(PackageSceneSettings.SceneSettings, next);
    }
}
