using Stride.Assets.Skyboxes;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetItem? skyboxSettingsItem;
    private AssetPropertyGraph? skyboxSettingsGraph;
    private readonly LoggerResult skyboxSettingsLog = new();
    private IReadOnlyList<SkyboxSettingField> skyboxSettingsFields = [];
    private SkyboxSettingField? activeSkyboxSetting;
    private object? skyboxSettingStart;
    private object? skyboxSettingPending;
    private long skyboxSettingsRevision, savedSkyboxSettingsRevision;
    public SkyboxAsset? SkyboxSettings => AuthoredAsset as SkyboxAsset;
    public bool CanEditSkyboxSettings => CanEditArchetypeAsset && SkyboxSettings != null;
    public long SkyboxSettingsEditVersion { get; private set; }
    public long SavedSkyboxSettingsRevision => savedSkyboxSettingsRevision;
    public long SkyboxSettingsSaveVersion { get; private set; }
    private bool HasSkyboxSettingChange => activeSkyboxSetting != null && !ContentAssetReference.AreEqual(skyboxSettingStart, skyboxSettingPending);
    public IReadOnlyList<SkyboxSettingField> SkyboxSettingsFields { get { EnsureSkyboxSettingsGraph(); return skyboxSettingsFields; } }
    private void EnsureSkyboxSettingsGraph()
    {
        if (skyboxSettingsGraph != null || SkyboxSettings == null) return;
        skyboxSettingsGraph = EnsureSettingsAuthoringGraph(out skyboxSettingsItem);
        skyboxSettingsFields = new[] { nameof(SkyboxAsset.CubeMap), nameof(SkyboxAsset.IsSpecularOnly), nameof(SkyboxAsset.DiffuseSHOrder), nameof(SkyboxAsset.SpecularCubeMapSize) }
            .Select(name => skyboxSettingsGraph.RootNode[name]).Select(node => new SkyboxSettingField(node,
                TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo)?.Name ?? node.Name)).ToArray();
    }
    public void UpdateSkyboxSetting(SkyboxSettingField field, object? value)
    {
        EnsureSkyboxSettingsGraph();
        if (!CanEditSkyboxSettings || !skyboxSettingsFields.Any(member => ReferenceEquals(member, field)) || !field.CanAssign || !CanEditAssetSettingNode(field.Node) || IsSkyboxSettingReadOnly(field))
            throw new InvalidOperationException("This skybox setting cannot be edited.");
        value = PropertyValueValidation.Normalize(value, SkyboxSettingRange(field));
        if (value == null ? field.Type.IsValueType || !field.AllowsNull : !field.Type.IsInstanceOfType(value))
            throw new ArgumentException("The skybox setting value has an incompatible type.");
        if (ContentAssetReference.Supports(field.Type)) ContentAssetReference.Validate(field.Type, value, field.AllowsNull);
        if (!ReferenceEquals(activeSkyboxSetting, field))
        {
            CommitEdits(); activeSkyboxSetting = field; skyboxSettingStart = field.Node.Retrieve();
            skyboxSettingPending = skyboxSettingStart;
        }
        skyboxSettingPending = value;
    }
    public object? ReadSkyboxSetting(SkyboxSettingField field)
        => ReferenceEquals(activeSkyboxSetting, field) ? skyboxSettingPending : field.Node.Retrieve();
    public void EndSkyboxSettingEdit()
    {
        if (activeSkyboxSetting == null) return;
        var field = activeSkyboxSetting; var before = skyboxSettingStart; var after = skyboxSettingPending;
        activeSkyboxSetting = null; skyboxSettingStart = skyboxSettingPending = null;
        if (ContentAssetReference.AreEqual(before, after)) return;
        if (!skyboxSettingsFields.Any(current => ReferenceEquals(current, field)) || !field.CanAssign || !CanEditAssetSettingNode(field.Node) || IsSkyboxSettingReadOnly(field))
            throw new InvalidOperationException("This skybox setting cannot be edited.");
        if (!ContentAssetReference.AreEqual(field.Node.Retrieve(), before))
            throw new InvalidOperationException("The skybox setting changed during editing.");
        ApplyInheritedAssetMutation(() => field.Node.Update(after), "Edit " + field.Label);
    }
    private void PrepareSkyboxSettingsForSave()
    {
        if (skyboxSettingsGraph == null) return;
        skyboxSettingsGraph.PrepareForSave(skyboxSettingsLog, skyboxSettingsItem!);
        if (skyboxSettingsLog.HasErrors) throw new InvalidOperationException(skyboxSettingsLog.ToText());
    }
    private void MarkSkyboxSettingsSaved()
    {
        if (skyboxSettingsRevision != savedSkyboxSettingsRevision) SkyboxSettingsSaveVersion++;
        savedSkyboxSettingsRevision = skyboxSettingsRevision;
    }
    private void UpdateSkyboxSettingsPath(string path) => DocumentAssetPath.Update(skyboxSettingsItem, path);
    private void DisposeSkyboxSettings() => skyboxSettingsGraph?.Dispose();
}
