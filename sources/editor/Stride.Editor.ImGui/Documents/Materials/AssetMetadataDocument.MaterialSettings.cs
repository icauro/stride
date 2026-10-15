using Stride.Assets.Materials;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Rendering.Materials;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private AssetItem? materialSettingsItem;
    private AssetPropertyGraph? materialSettingsGraph;
    private readonly LoggerResult materialSettingsLog = new();
    private readonly HashSet<MaterialSettingField> materialFields = [];
    private IReadOnlyList<MaterialSettingField> materialSettingsFields = [];
    private IReadOnlyList<MaterialSettingLayer> materialSettingLayers = [];
    private MaterialSettingField? activeMaterialSetting;
    private object? materialSettingStart;
    private object? materialSettingPending;
    private long materialSettingsRevision, savedMaterialSettingsRevision;
    private long materialSettingsChangeVersion;
    public MaterialAsset? MaterialSettings => AuthoredAsset as MaterialAsset;
    public bool CanEditMaterialSettings => CanEditArchetypeAsset && MaterialSettings != null;
    public long MaterialSettingsSaveVersion { get; private set; }
    public long MaterialSettingsEditVersion => materialSettingsChangeVersion;
    private bool HasMaterialSettingChange => activeMaterialSetting != null && !ContentAssetReference.AreEqual(materialSettingStart, materialSettingPending);
    public IReadOnlyList<MaterialSettingField> MaterialSettingsFields
    {
        get { EnsureMaterialSettingsGraph(); return materialSettingsFields; }
    }
    public IReadOnlyList<MaterialSettingLayer> MaterialSettingLayers
    {
        get { EnsureMaterialSettingsGraph(); return materialSettingLayers; }
    }
    private void EnsureMaterialSettingsGraph()
    {
        if (materialSettingsGraph != null || MaterialSettings == null) return;
        materialSettingsGraph = EnsureSettingsAuthoringGraph(out materialSettingsItem);
        RefreshMaterialSettingsFields();
    }
    private void RefreshMaterialSettingsFields()
    {
        materialFields.Clear();
        importSettingsGraph = materialSettingsGraph;
        importSettingsMembers.Clear(); importSettingsItems.Clear();
        // WPF bypasses the Attributes node and presents its members directly.
        materialSettingsFields = materialSettingsGraph!.RootNode[nameof(MaterialAsset.Attributes)].Target is { } attributes
            ? ReadFields(attributes, "Attributes", 0, new HashSet<IObjectNode>()) : [];
        var layers = (IAssetObjectNode)materialSettingsGraph.RootNode[nameof(MaterialAsset.Layers)].Target!;
        materialSettingLayers = (layers.Indices ?? []).Select(index =>
        {
            var id = layers.IndexToId(index);
            return new MaterialSettingLayer(id, (MaterialBlendLayer)layers.Retrieve(index)!,
                ReadFields(layers.IndexedTarget(index)!, "Layers." + id, 0, new HashSet<IObjectNode>()));
        }).ToArray();
    }
    private IReadOnlyList<MaterialSettingField> ReadFields(IObjectNode target, string path, int depth, HashSet<IObjectNode> ancestors)
    {
        if (depth > 16 || !ancestors.Add(target)) return [];
        var fields = new List<MaterialSettingField>();
        foreach (var node in target.Members)
        {
            var registry = TypeDescriptorFactory.Default.AttributeRegistry;
            var display = registry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo);
            if (display?.Browsable == false || registry.GetAttribute<DataMemberIgnoreAttribute>(node.MemberDescriptor.MemberInfo) != null) continue;
            var field = new MaterialSettingField(node, path + "." + node.Name, display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name), display?.Category ?? "");
            if (node.Target is IAssetObjectNode { IsEnumerable: true })
            {
                var collection = new AssetImportSettingField(node, field.Key, field.Label, field.Category, display?.Order ?? 0);
                importSettingsMembers.Add(collection);
                collection.Items = ReadImportCollectionItems(collection, depth, ancestors);
                field.CollectionField = collection;
            }
            // Content references have identity semantics and are handled by the shared asset picker.
            else if (!IsMaterialScalar(node.Type) && !ContentAssetReference.Supports(node.Type) && node.Target is { } child)
                field.Children = ReadFields(child, field.Key, depth + 1, ancestors);
            fields.Add(field); materialFields.Add(field);
        }
        ancestors.Remove(target);
        return fields;
    }
    private static bool IsMaterialScalar(Type type) => Nullable.GetUnderlyingType(type) is { } underlying
        ? IsMaterialScalar(underlying) : type.IsPrimitive || type.IsEnum || type == typeof(string)
        || type == typeof(Stride.Core.Mathematics.Color) || type == typeof(Stride.Core.Mathematics.Color3) || type == typeof(Stride.Core.Mathematics.Color4)
        || type == typeof(Stride.Core.Mathematics.Vector2) || type == typeof(Stride.Core.Mathematics.Vector3)
        || type == typeof(Stride.Core.Mathematics.Vector4)
        || type == typeof(Stride.Core.Mathematics.Int2) || type == typeof(Stride.Core.Mathematics.Int3) || type == typeof(Stride.Core.Mathematics.Int4)
        || type == typeof(Stride.Core.Mathematics.Quaternion) || type == typeof(Stride.Core.Mathematics.Matrix)
        || type == typeof(Stride.Core.Mathematics.Rectangle) || type == typeof(Stride.Core.Mathematics.RectangleF)
        || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(Stride.Core.Mathematics.AngleSingle)
        || type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory);
    public void BeginMaterialSettingEdit(MaterialSettingField field)
    {
        EnsureMaterialSettingsGraph();
        if (!CanEditMaterialSettings || !IsCurrentMaterialField(field) || !field.CanAssign || !CanEditAssetSettingNode(field.Node) || IsMaterialSettingReadOnly(field)) throw new InvalidOperationException("This asset can't be edited.");
        if (ReferenceEquals(activeMaterialSetting, field)) return;
        CommitEdits(); activeMaterialSetting = field; materialSettingStart = materialSettingPending = field.Node.Retrieve();
    }
    internal bool IsCurrentMaterialField(MaterialSettingField field) => materialFields.Contains(field)
        || materialFields.Any(current => ReferenceEquals(current.Node, field.Node) && current.Key == field.Key);
    public object? ReadMaterialSetting(MaterialSettingField field) => ReferenceEquals(activeMaterialSetting, field) ? materialSettingPending : field.Node.Retrieve();
    public void UpdateMaterialSetting(MaterialSettingField field, object? value)
    {
        if (value != null && !field.Type.IsInstanceOfType(value)) throw new ArgumentException("The material setting value has an incompatible type.");
        if (value == null && !field.AllowsNull) throw new ArgumentException("This setting cannot be null.");
        if (ContentAssetReference.Supports(field.Type)) ContentAssetReference.Validate(field.Type, value, true);
        value = PropertyValueValidation.Normalize(value, MaterialSettingRange(field));
        BeginMaterialSettingEdit(field);
        materialSettingPending = value;
    }
    public void EndMaterialSettingEdit()
    {
        if (activeMaterialSetting == null) return;
        var field = activeMaterialSetting; var before = materialSettingStart; var after = materialSettingPending;
        activeMaterialSetting = null; materialSettingStart = materialSettingPending = null;
        if (ContentAssetReference.AreEqual(before, after)) return;
        if (!IsCurrentMaterialField(field) || !CanEditAssetSettingNode(field.Node) || IsMaterialSettingReadOnly(field)) throw new InvalidOperationException("This asset can't be edited.");
        if (!ContentAssetReference.AreEqual(field.Node.Retrieve(), before))
            throw new InvalidOperationException("The material setting changed during editing.");
        ApplyInheritedAssetMutation(() => UpdateMaterialContextNode(field.Node, after), "Edit material setting: " + field.Label);
    }
    public void ChangeMaterialSettingType(MaterialSettingField field, Type? type)
    {
        EndMaterialSettingEdit();
        if (type == null && !field.AllowsNull) throw new InvalidOperationException("This material feature cannot be null.");
        if (type != null && !field.Types.Contains(type)) throw new InvalidOperationException("This material type cannot be selected.");
        if (type == field.Node.Retrieve()?.GetType()) return;
        var value = type == null ? null : ObjectFactoryRegistry.NewInstance(type);
        if (value != null) AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateMaterialSetting(field, value);
        EndMaterialSettingEdit(); RefreshMaterialSettingsFields();
    }
    private IAssetObjectNode EditableMaterialLayers()
    {
        EnsureMaterialSettingsGraph(); CommitEdits();
        if (!CanEditMaterialSettings) throw new InvalidOperationException("This asset can't be edited.");
        var node = (IAssetObjectNode)materialSettingsGraph!.RootNode[nameof(MaterialAsset.Layers)].Target!;
        if (!CanEditAssetSettingNode(node) || IsExtensionPropertyReadOnly(materialSettingsGraph.RootNode[nameof(MaterialAsset.Layers)])) throw new InvalidOperationException("These layers cannot be edited.");
        return node;
    }
    public void AddMaterialLayer()
    {
        AddMaterialLayerEntry(null);
    }
    internal bool CanEditMaterialLayer(MaterialSettingLayer layer)
    {
        EnsureMaterialSettingsGraph();
        var layers = (IAssetObjectNode)materialSettingsGraph!.RootNode[nameof(MaterialAsset.Layers)].Target!;
        return CanEditMaterialSettings && layers.HasId(layer.Id) && CanEditAssetSettingItem(layers, layers.IdToIndex(layer.Id)) && !IsExtensionItemReadOnly(layers, layers.IdToIndex(layer.Id));
    }
    internal bool CanResetMaterialLayer(MaterialSettingLayer layer)
    {
        EnsureMaterialSettingsGraph();
        var layers = (IAssetObjectNode)materialSettingsGraph!.RootNode[nameof(MaterialAsset.Layers)].Target!;
        return layers.HasId(layer.Id) && CanResetAssetSettingItem(layers, layers.IdToIndex(layer.Id)) && !IsExtensionItemReadOnly(layers, layers.IdToIndex(layer.Id));
    }
    internal void ResetMaterialLayer(MaterialSettingLayer layer)
    {
        var layers = EditableMaterialLayers();
        if (!layers.HasId(layer.Id) || IsExtensionItemReadOnly(layers, layers.IdToIndex(layer.Id))) throw new InvalidOperationException("This layer cannot be reset.");
        ResetAssetSettingItem(layers, layers.IdToIndex(layer.Id));
    }
    public void RemoveMaterialLayer(MaterialSettingLayer layer)
    {
        if (!CanRemoveMaterialLayer(layer)) throw new InvalidOperationException("This material layer cannot be removed.");
        var layers = EditableMaterialLayers();
        if (!layers.HasId(layer.Id)) throw new InvalidOperationException("This layer no longer exists.");
        var index = layers.IdToIndex(layer.Id);
        if (!CanEditAssetSettingItem(layers, index) || IsExtensionItemReadOnly(layers, index)) throw new InvalidOperationException("This layer cannot be edited.");
        ApplyInheritedAssetMutation(() => layers.Remove(layer.Value, index), "Remove material layer");
    }
    public void MoveMaterialLayer(MaterialSettingLayer layer, int destination)
    {
        var layers = EditableMaterialLayers();
        if (!layers.HasId(layer.Id) || destination < 0 || destination >= MaterialSettings!.Layers.Count) throw new ArgumentOutOfRangeException(nameof(destination));
        var source = layers.IdToIndex(layer.Id).Int;
        if (!CanEditAssetSettingItem(layers, new NodeIndex(source)) || IsExtensionItemReadOnly(layers, new NodeIndex(source))) throw new InvalidOperationException("This layer cannot be edited.");
        if (source == destination) return;
        var overridden = layers.IsItemOverridden(layers.IdToIndex(layer.Id));
        void Move(int index)
        {
            layers.RemoveAndDiscard(layer.Value, layers.IdToIndex(layer.Id), layer.Id);
            layers.Restore(layer.Value, new NodeIndex(index), layer.Id); layers.OverrideItem(overridden, new NodeIndex(index));
        }
        ApplyInheritedAssetMutation(() => Move(destination), "Move material layer");
    }
    private void PrepareMaterialSettingsForSave()
    {
        if (materialSettingsGraph == null) return;
        materialSettingsGraph.PrepareForSave(materialSettingsLog, materialSettingsItem!);
        if (materialSettingsLog.HasErrors) throw new InvalidOperationException(materialSettingsLog.ToText());
    }
    private void MarkMaterialSettingsSaved()
    {
        if (materialSettingsRevision != 0 || savedMaterialSettingsRevision != 0) MaterialSettingsSaveVersion++;
        savedMaterialSettingsRevision = materialSettingsRevision;
    }
}
