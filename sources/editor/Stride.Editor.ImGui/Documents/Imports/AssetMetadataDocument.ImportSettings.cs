using Stride.Audio.Assets;
using Stride.Video.Assets;
using Stride.Assets.Models;
using Stride.Navigation.Assets;
using Stride.Physics.Assets;
using Stride.Assets.Textures;
using Stride.BepuPhysics.Assets;
using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Extensions;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.SpriteStudio.Assets;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetPropertyGraph? importSettingsGraph;
    private IReadOnlyList<AssetImportSettingField> importSettingFields = [];
    private readonly HashSet<AssetImportSettingField> importSettingsMembers = new(ReferenceEqualityComparer.Instance);
    private AssetImportSettingField? activeImportSetting;
    private object? importSettingStart, importSettingPending;
    private long savedImportSettingsEditVersion;
    private long extensionImportFieldsVersion = -1;
    private static readonly PropertyNumericRange absoluteVideoDimensionRange = new(new PropertyNumericMetadata(0, float.MaxValue, DecimalPlaces: 0), typeof(float));
    public bool HasImportSettings => AuthoredAsset is SoundAsset or VideoAsset or RawAsset or RenderTextureAsset or PrefabModelAsset
        or SpriteStudioModelAsset or SpriteStudioAnimationAsset or ModelAsset or SkeletonAsset or AnimationAsset or HeightmapAsset or HullAsset
        or ColliderShapeAsset or NavigationMeshAsset || HasExtensionAssetSettings;
    public long ImportSettingsEditVersion { get; private set; }
    public long ImportSettingsSaveVersion { get; private set; }
    private void MarkImportSettingsSaved()
    {
        if (importSettingsGraph != null && savedImportSettingsEditVersion != ImportSettingsEditVersion) ImportSettingsSaveVersion++;
        savedImportSettingsEditVersion = ImportSettingsEditVersion;
    }
    private bool HasImportSettingChange => HasImportItemChange || activeImportSetting != null && !ContentAssetReference.AreEqual(importSettingStart, importSettingPending);
    public IReadOnlyList<AssetImportSettingField> ImportSettingsFields
    {
        get
        {
            if (!HasImportSettings) return [];
            if (importSettingsGraph == null)
            {
                importSettingsGraph = EnsureSettingsAuthoringGraph(out _); RefreshImportSettingsFields();
            }
            if (extensionImportFieldsVersion != (SessionHistory?.Extensions?.Version ?? 0))
            {
                EndImportSettingEdit(); RefreshImportSettingsFields();
                extensionImportFieldsVersion = SessionHistory?.Extensions?.Version ?? 0;
            }
            return importSettingFields;
        }
    }
    private void RefreshImportSettingsFields()
    {
        if (importSettingsGraph == null) return;
        if (MaterialSettings != null) return;
        importSettingsMembers.Clear();
        importSettingsItems.Clear();
        importSettingFields = ReadImportSettingsFields(importSettingsGraph.RootNode, "Asset", 0, new HashSet<IObjectNode>());
        ImportSettingsEditVersion++;
    }
    private IReadOnlyList<AssetImportSettingField> ReadImportSettingsFields(IObjectNode target, string path, int depth, HashSet<IObjectNode> visited)
    {
        if (target.Retrieve() is Stride.Core.Yaml.IUnloadable || depth > 12 || !visited.Add(target)) return [];
        var fields = new List<AssetImportSettingField>();
        foreach (var node in target.Members)
        {
            // Inline MaterialInstance.Material is hidden in native tree enumeration;
            // its parent template still edits the real reference node.
            var inlineMaterial = target.Retrieve() is Stride.Rendering.MaterialInstance && node.Name == nameof(Stride.Rendering.MaterialInstance.Material);
            if (depth == 0 && !IsImportRootMemberVisible(node) || !inlineMaterial && !IsExtensionPropertyVisible(node)) continue;
            var registry = TypeDescriptorFactory.Default.AttributeRegistry;
            var display = registry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo);
            if (display?.Browsable == false || registry.GetAttribute<DataMemberIgnoreAttribute>(node.MemberDescriptor.MemberInfo) != null
                || !IsImportChildMemberVisible(target, node)) continue;
            var label = ExtensionPropertyLabel(node) ?? display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name);
            var range = registry.GetAttribute<DataMemberRangeAttribute>(node.MemberDescriptor.MemberInfo);
            var type = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
            // WPF VideoAssetNodeUpdater substitutes integer absolute dimensions,
            // with no percentage maximum, while retaining the real float node.
            var absoluteVideoSize = depth == 0 && AuthoredAsset is VideoAsset { IsSizeInPercentage: false }
                && node.Name is nameof(VideoAsset.Width) or nameof(VideoAsset.Height);
            if (absoluteVideoSize) label = Stride.Core.Presentation.Core.Utils.SplitCamelCase("Absolute" + node.Name);
            var field = new AssetImportSettingField(node, path + "." + node.Name, label, display?.Category ?? "",
                display?.Order ?? node.MemberDescriptor.Order ?? int.MaxValue)
            {
                Range = absoluteVideoSize ? null : range != null && (type.IsPrimitive || type == typeof(decimal)) ? new(range, type) : null,
                Children = !IsImportSettingScalar(type) && SessionHistory?.Extensions?.HasPrimitiveEditor(node.Type) != true && !ContentAssetReference.Supports(type) && node.Target is { IsEnumerable: false } child
                    ? ReadImportSettingsFields(child, path + "." + node.Name, depth + 1, visited) : []
            };
            fields.Add(field); importSettingsMembers.Add(field);
            if (field.Collection != null && field.ElementType != null)
                field.Items = ReadImportCollectionItems(field, depth, visited);
        }
        visited.Remove(target);
        return fields.OrderBy(field => field.CategoryOrder?.Order ?? int.MaxValue).ThenBy(field => field.Order).ToArray();
    }
    private bool IsImportRootMemberVisible(IMemberNode node) => AuthoredAsset switch
    {
        SoundAsset => node.Name is nameof(SoundAsset.Source) or nameof(SoundAsset.Index) or nameof(SoundAsset.SampleRate)
            or nameof(SoundAsset.CompressionRatio) or nameof(SoundAsset.StreamFromDisk) or nameof(SoundAsset.Spatialized),
        VideoAsset => node.Name is nameof(VideoAsset.Source) or nameof(VideoAsset.Width) or nameof(VideoAsset.Height)
            or nameof(VideoAsset.IsSizeInPercentage) or nameof(VideoAsset.VideoDuration) or nameof(VideoAsset.IsAudioChannelMono),
        RawAsset => node.Name is nameof(RawAsset.Source) or nameof(RawAsset.Compress),
        RenderTextureAsset => node.Name is nameof(RenderTextureAsset.Width) or nameof(RenderTextureAsset.Height)
            or nameof(RenderTextureAsset.Format) or nameof(RenderTextureAsset.UseSRgbSampling),
        PrefabModelAsset => node.Name == nameof(PrefabModelAsset.Prefab),
        SpriteStudioModelAsset => node.Name == nameof(SpriteStudioModelAsset.Source),
        SpriteStudioAnimationAsset => node.Name is nameof(SpriteStudioAnimationAsset.Source) or nameof(SpriteStudioAnimationAsset.RepeatMode),
        ModelAsset model => node.Name is nameof(ModelAsset.Source) or nameof(ModelAsset.Skeleton) or nameof(ModelAsset.MergeMeshes)
            or nameof(ModelAsset.Materials) or nameof(ModelAsset.Modifiers)
            || model.Skeleton == null && node.Name is nameof(ModelAsset.PivotPosition) or nameof(ModelAsset.ScaleImport),
        SkeletonAsset => node.Name is nameof(SkeletonAsset.Source) or nameof(SkeletonAsset.PivotPosition) or nameof(SkeletonAsset.ScaleImport) or nameof(SkeletonAsset.Nodes),
        AnimationAsset animation => node.Name is nameof(AnimationAsset.Source) or nameof(AnimationAsset.AnimationStack) or nameof(AnimationAsset.ClipDuration)
            or nameof(AnimationAsset.RepeatMode) or nameof(AnimationAsset.Type) or nameof(AnimationAsset.Skeleton) or nameof(AnimationAsset.RootMotion)
            or nameof(AnimationAsset.ImportCustomAttributes) or nameof(AnimationAsset.PreviewModel)
            || animation.Skeleton == null && node.Name is nameof(AnimationAsset.PivotPosition) or nameof(AnimationAsset.ScaleImport),
        HeightmapAsset => node.MemberDescriptor.MemberInfo.Name is nameof(HeightmapAsset.Source) or nameof(HeightmapAsset.HeightConversionParameters)
            or nameof(HeightmapAsset.Resizing) or nameof(HeightmapAsset.IsSRgb) or nameof(HeightmapAsset.ScaleToHeightRange)
            or nameof(HeightmapAsset.FloatingPointComponentRange) or nameof(HeightmapAsset.IsSymmetricShortComponent),
        HullAsset => node.Name is nameof(HullAsset.Model) or nameof(HullAsset.LocalOffset) or nameof(HullAsset.LocalRotation) or nameof(HullAsset.Scaling) or nameof(HullAsset.Decomposition),
        ColliderShapeAsset => node.Name == nameof(ColliderShapeAsset.ColliderShapes),
        NavigationMeshAsset => node.Name is nameof(NavigationMeshAsset.Scene) or nameof(NavigationMeshAsset.IncludedCollisionGroups)
            or nameof(NavigationMeshAsset.BuildSettings) or nameof(NavigationMeshAsset.SelectedGroups),
        _ => HasExtensionAssetSettings && node.Name is not (nameof(Asset.Id) or nameof(Asset.Tags) or nameof(Asset.Archetype))
    };
    private static bool IsImportChildMemberVisible(IObjectNode target, IMemberNode node)
    {
        if (target.Retrieve() is AnimationAssetDuration duration && !duration.Enabled)
            return node.Name is not (nameof(AnimationAssetDuration.StartAnimationTime) or nameof(AnimationAssetDuration.EndAnimationTime));
        if (target.Retrieve() is AnimationAssetDurationUnchecked durationBox && !durationBox.Enabled)
            return node.Name is not (nameof(AnimationAssetDurationUnchecked.StartAnimationTimeBox) or nameof(AnimationAssetDurationUnchecked.EndAnimationTimeBox));
        if (target.Retrieve() is ModelMaterial) return node.Name == nameof(ModelMaterial.MaterialInstance);
        if (target.Retrieve() is NodeInformation) return node.Name == nameof(NodeInformation.Preserve);
        return true;
    }
    private static bool IsImportSettingScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string)
        || type == typeof(UFile) || type == typeof(UDirectory) || type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(decimal)
        || type == typeof(Guid) || type == typeof(AngleSingle) || type == typeof(Color) || type == typeof(Color3) || type == typeof(Color4)
        || type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4)
        || type == typeof(Quaternion) || type == typeof(Matrix) || type == typeof(Rectangle) || type == typeof(RectangleF);
    public object? ReadImportSetting(AssetImportSettingField field) => ReferenceEquals(field, activeImportSetting) ? importSettingPending : field.Node.Retrieve();
    internal bool IsCurrentImportSettingField(AssetImportSettingField field)
        => importSettingsMembers.Contains(field) || importSettingsMembers.Any(current => ReferenceEquals(current.Node, field.Node) && current.Key == field.Key);
    public bool CanEditImportSetting(AssetImportSettingField field) => (HasImportSettings || MaterialSettings != null) && SessionHistory != null && CanEditArchetypeAsset
        && IsCurrentImportSettingField(field)
        && CanEditAssetSettingNode(field.Node) && CanAssignAuthoringNode(field.Node) && !IsExtensionPropertyReadOnly(field.Node);
    internal PropertyNumericRange? ImportSettingNumericRange(AssetImportSettingField field)
        => AuthoredAsset is VideoAsset { IsSizeInPercentage: false }
            && field.Node.Name is nameof(VideoAsset.Width) or nameof(VideoAsset.Height)
            ? absoluteVideoDimensionRange
            : ExtensionNumericRange(field.Node) ?? field.Range;
    public void UpdateImportSetting(AssetImportSettingField field, object? value)
    {
        if (!CanEditImportSetting(field)) throw new InvalidOperationException("This asset setting cannot be edited.");
        value = PropertyValueValidation.Normalize(value, ImportSettingNumericRange(field));
        value = VectorPropertyNumericMetadata.Normalize(value, field.Node.Retrieve(), VectorPropertyNumericMetadata.Create(field.Node));
        if (value == null ? !field.AllowsNull : !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The asset setting value is not compatible.");
        if (ContentAssetReference.Supports(field.Type)) ContentAssetReference.Validate(field.Type, value, field.AllowsNull);
        if (value is UFile source)
        {
            var text = source.ToString();
            if (text.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Invalid source path.");
            if (!string.IsNullOrEmpty(text)) System.IO.Path.GetFullPath(text, System.IO.Path.GetDirectoryName(Path)!);
        }
        if (AuthoredAsset is VideoAsset video && field.Node.Name is nameof(VideoAsset.Width) or nameof(VideoAsset.Height) && value is float dimension)
            value = video.IsSizeInPercentage ? Math.Clamp(dimension, 0, 100) : Math.Max(0, MathF.Round(dimension));
        if (!ReferenceEquals(field, activeImportSetting))
        {
            CommitEdits(); activeImportSetting = field; importSettingStart = field.Node.Retrieve();
        }
        importSettingPending = value;
    }
    public void EndImportSettingEdit()
    {
        EndImportSettingItemEdit();
        if (activeImportSetting == null) return;
        var field = activeImportSetting; var before = importSettingStart; var after = importSettingPending;
        activeImportSetting = null; importSettingStart = importSettingPending = null;
        if (ContentAssetReference.AreEqual(before, after)) return;
        if (!CanEditImportSetting(field)) throw new InvalidOperationException("This asset setting cannot be edited.");
        ApplyInheritedAssetMutation(() =>
        {
            if (MaterialSettings != null) UpdateMaterialContextNode(field.Node, after); else field.Node.Update(after);
        }, "Edit asset setting: " + field.Label);
    }
    public void ChangeImportSettingType(AssetImportSettingField field, Type? type)
    {
        if (ExtensionAbstractEntries(field.Node) != null)
        {
            var entry = NativeAbstractEntries.TypeEntry(ImportSettingEntries(field), type) ?? throw new InvalidOperationException("This native property type cannot be selected.");
            ChangeImportSettingEntry(field, entry); return;
        }
        if (type == null ? !field.AllowsNull : !field.Types.Contains(type)) throw new InvalidOperationException("This asset setting type cannot be selected.");
        var value = type == null ? null : ObjectFactoryRegistry.NewInstance(type);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateImportSetting(field, value); EndImportSettingEdit();
    }
}
