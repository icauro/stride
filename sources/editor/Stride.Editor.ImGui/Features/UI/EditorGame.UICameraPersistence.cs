using Stride.Assets.Presentation.SceneEditor;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Processors;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private UICameraView LoadNativeUICameraView(UIDocument source)
    {
        var item = workspace?.Session.FindAsset(source.Asset.Id)
            ?? throw new InvalidOperationException("The UI asset is no longer in the current package.");
        var values = item.Package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
        if (values.TryGetValue(source.Asset.Id, out var saved)) return UICameraView.FromNative(saved);
        var defaults = SceneSettingsData.CreateDefault();
        defaults.CamProjection = CameraProjectionMode.Orthographic;
        defaults.CamPosition = new Vector3(0, 0, 500);
        defaults.CamPitchYaw = Vector2.Zero;
        return UICameraView.FromNative(defaults);
    }

    private void SaveNativeUICameraView(UIDocument source, UICameraView view)
    {
        if (workspace == null || sessionHistory == null) return;
        var item = workspace.Session.FindAsset(source.Asset.Id);
        var state = authoring?.Find(source.Asset.Id);
        if (item == null || item.Package.IsReadOnly || state is not { IsPublished: true, IsDisposed: false }
            || !ReferenceEquals(state.Document, source)) return;
        if (!view.Valid) throw new InvalidOperationException("The UI camera settings are invalid.");
        var package = item.Package;
        var values = package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
        var owner = GetPackageSettingsDocument(package, sessionHistory);
        if (values.TryGetValue(source.Asset.Id, out var previous) && UICameraView.FromNative(previous) == view
            && previous.SceneUnit == view.SceneUnit)
        {
            return;
        }
        // Preserve every unrelated native setting and every other asset's entry.
        var next = (Dictionary<AssetId, SceneSettingsData>)AssetCloner.Clone(values, AssetClonerFlags.None, out _)!;
        if (!next.TryGetValue(source.Asset.Id, out var entry)) next.Add(source.Asset.Id, entry = SceneSettingsData.CreateDefault());
        view.WriteNative(entry);
        owner.SaveAutomaticPreference(PackageSceneSettings.SceneSettings, next);
    }
}
