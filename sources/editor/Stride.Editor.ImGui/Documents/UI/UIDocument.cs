using Stride.Assets.Quantum;
using Stride.UI.Assets;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel.CopyPasteProcessors;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Quantum.Visitors;
using Stride.Core.Annotations;
using Stride.Core.Extensions;
using Stride.Core.Serialization;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Yaml;
using Stride.Core.Transactions;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Stride.Editor.ImGui;

internal sealed record UIElementProperty(IMemberNode Node, string Label, string? Path = null, Guid? AttachedOwner = null, PropertyKey? AttachedKey = null)
{
    public Type Type => Node.Type;
    public string Name => Path ?? Node.Name;
    public PropertyNumericRange? NumericRange
    {
        get
        {
            var metadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(Node.MemberDescriptor.MemberInfo);
            var type = Nullable.GetUnderlyingType(Type) ?? Type;
            return metadata != null && (type.IsPrimitive || type == typeof(decimal)) ? new PropertyNumericRange(metadata, type) : null;
        }
    }
    public bool SupportsObjectLifecycle => !Type.IsValueType && Type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(Type)
        && !typeof(UIElement).IsAssignableFrom(Type) && !ContentAssetReference.Supports(Type) && !AssetRegistry.IsExactContentType(Type)
        && (Type == typeof(object) || Type.IsInterface || Type.IsAbstract || Type.IsDefined(typeof(DataContractAttribute), true));
    public bool AllowsNull => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public IReadOnlyList<Type> ObjectTypes => SupportsObjectLifecycle ? Type.GetInheritedInstantiableTypes()
        .Where(type => !typeof(UIElement).IsAssignableFrom(type) && !typeof(Stride.Engine.Entity).IsAssignableFrom(type) && !typeof(Stride.Engine.EntityComponent).IsAssignableFrom(type)
            && Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) == null && !AssetRegistry.IsExactContentType(type) && ObjectFactoryRegistry.CanCreateInstance(type))
        .OrderBy(type => DisplayAttribute.GetDisplayName(type) ?? type.Name).ToArray() : [];
}
internal sealed record UIAssetExport(UIAssetBase Asset, IReadOnlyDictionary<Guid, Guid> IdRemapping)
{
    internal UIDocument? Source { get; init; }
    internal long SourceRevision { get; init; }
    internal long SourceLayout { get; init; }
    internal string SourcePath { get; init; } = "";
    internal Guid[] SourceRoots { get; init; } = [];
    internal bool MatchesSource(UIDocument source) => ReferenceEquals(Source, source)
        && source.EditRevision == SourceRevision && source.LayoutVersion == SourceLayout
        && StringComparer.OrdinalIgnoreCase.Equals(source.Path, SourcePath)
        && SourceRoots.Length != 0 && SourceRoots.All(id => source.Find(id) != null);
}

/// <summary>Canonical UI asset hierarchy edits through the production UIAssetPropertyGraph and session journal.</summary>
internal sealed partial class UIDocument : AssetMetadataDocument
{
    private ITransaction? continuousEdit;
    private bool applyingUIEdit;
    private long layoutVersion;
    private UIAssetPropertyGraph? observedGraph;
    private CopyPasteService? clipboard;
    private CopyPasteService Clipboard
    {
        get
        {
            if (clipboard != null && ReferenceEquals(clipboard.PropertyGraphContainer, UIHierarchyGraph.Container)) return clipboard;
            var settings = AssetYamlSerializer.Default.GetSerializerSettings();
            settings.RegisterTagMapping("!CopyPasteData", typeof(CopyPasteData)); settings.RegisterTagMapping("!CopyPasteItem", typeof(CopyPasteItem));
            clipboard = new CopyPasteService(UIHierarchyGraph.Container);
            clipboard.RegisterProcessor(new AssetCompositeHierarchyPasteProcessor<UIElementDesign, UIElement>());
            return clipboard;
        }
    }
    public UIDocument(string path, AssetItem item, EditorSessionHistory history) : base(path, item, history)
    {
        if (AuthoredAsset is not UIAssetBase) throw new InvalidDataException("The asset is not a UI page or library.");
    }
    public UIDocument(string path, EditorSessionHistory history, bool readOnly = false) : base(path, history, readOnly)
    {
        if (AuthoredAsset is not UIAssetBase) throw new InvalidDataException("The file is not a UI page or library.");
    }
    internal UIDocument(string path, AssetLoadResult<Asset> restored, byte[] baseline, EditorSessionHistory? history, bool readOnly)
        : base(path, restored, baseline, history, readOnly)
    {
        if (AuthoredAsset is not UIAssetBase) throw new InvalidDataException("The restored asset is not a UI page or library.");
    }
    public UIAssetBase Asset => (UIAssetBase)AuthoredAsset;
    public UIAssetPropertyGraph UIHierarchyGraph
    {
        get
        {
            var graph = AuthoringGraph as UIAssetPropertyGraph ?? throw new InvalidOperationException("The UI asset has no registered UI hierarchy graph.");
            if (!ReferenceEquals(graph, observedGraph))
            {
                if (observedGraph != null)
                {
                    observedGraph.Changed -= OnUIHierarchyMemberChanged;
                    observedGraph.ItemChanged -= OnUIHierarchyItemChanged;
                }
                observedGraph = graph;
                graph.Changed += OnUIHierarchyMemberChanged;
                graph.ItemChanged += OnUIHierarchyItemChanged;
            }
            return graph;
        }
    }
    public long LayoutVersion { get { _ = UIHierarchyGraph; return layoutVersion; } }
    public UIElement? Find(Guid id) => Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.UIElement : null;
    public IReadOnlyList<UIElement> Roots => Asset.Hierarchy.RootParts;
    public IReadOnlyList<UIElement> Children(Guid id) => Find(id)?.VisualChildren ?? [];
    private IObjectNode ObjectNode(object owner) => AuthoringGraph.Container.NodeContainer.GetOrCreateNode(owner)!;
    public IReadOnlyList<UIElementProperty> Properties(Guid id, bool includeHidden = false)
    {
        var element = Find(id) ?? throw new InvalidOperationException("The selected UI element no longer exists.");
        var result = new List<UIElementProperty>();
        var visited = new HashSet<IObjectNode>(ReferenceEqualityComparer.Instance);
        Visit(ObjectNode(element), "", "", 0);
        foreach (var property in AttachedProperties(id))
        {
            var dependencies = DependencyPropertyNode(id);
            var index = new NodeIndex(property.Key);
            if (!dependencies.Indices.Contains(index) || dependencies.IndexedTarget(index) is not { } target
                || ContentAssetReference.Supports(property.Key.PropertyType) || Simple(property.Key.PropertyType)
                || AuthoringGraph.Definition.IsTargetItemObjectReference(dependencies, index, dependencies.Retrieve(index))) continue;
            var path = "@" + property.Key.OwnerType.FullName + "/" + property.Key.Name;
            if (target.IsEnumerable)
            {
                foreach (var item in target.Indices ?? [])
                    if (target.ItemReferences != null && !AuthoringGraph.Definition.IsTargetItemObjectReference(target, item, target.Retrieve(item)) && target.IndexedTarget(item) is { } child)
                        Visit(child, path + "[" + item + "]", property.Label + " [" + item + "]", 1, property.Key);
            }
            else Visit(target, path, property.Label, 1, property.Key);
        }
        return result;
        void Visit(IObjectNode owner, string path, string label, int depth, PropertyKey? attachedKey = null)
        {
            if (depth > 8 || !visited.Add(owner) || UnknownAssetData.IsProxyNode(owner)) return;
            if (owner.IsEnumerable)
            {
                foreach (var index in owner.Indices ?? [])
                    if (owner.ItemReferences != null && !AuthoringGraph.Definition.IsTargetItemObjectReference(owner, index, owner.Retrieve(index)) && owner.IndexedTarget(index) is { } child)
                        Visit(child, path + "[" + index + "]", label + " [" + index + "]", depth + 1, attachedKey);
                return;
            }
            foreach (var node in owner.Members)
            {
                if (node.Name == nameof(UIElement.Id) || node.Name == nameof(Panel.Children) || node.Name == nameof(ContentControl.Content)
                    || node.Name == nameof(UIElement.DependencyProperties)) continue;
                var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(node.MemberDescriptor.MemberInfo);
                if (display?.Browsable == false || !includeHidden && !IsExtensionPropertyVisible(node)) continue;
                var leafLabel = ExtensionPropertyLabel(node) ?? display?.Name ?? Stride.Core.Presentation.Core.Utils.SplitCamelCase(node.Name);
                var fieldLabel = label.Length == 0 ? leafLabel : label + " / " + leafLabel;
                var fieldPath = path.Length == 0 ? node.Name : path + "/" + node.Name;
                var field = new UIElementProperty(node, fieldLabel, fieldPath, attachedKey == null ? null : id, attachedKey);
                if (node.Target is { } target && !ContentAssetReference.Supports(node.Type) && !Simple(node.Type)
                    && !AuthoringGraph.Definition.IsMemberTargetObjectReference(node, node.Retrieve()))
                {
                    if (field.SupportsObjectLifecycle) result.Add(field);
                    if (target.IsEnumerable)
                    {
                        foreach (var index in target.Indices!) if (target.ItemReferences != null && !AuthoringGraph.Definition.IsTargetItemObjectReference(target, index, target.Retrieve(index)) && target.IndexedTarget(index) is { } child)
                            Visit(child, fieldPath + "[" + index + "]", fieldLabel + " [" + index + "]", depth + 1, attachedKey);
                    }
                    else if (target.Members.Count > 0) Visit(target, fieldPath, fieldLabel, depth + 1, attachedKey);
                    else if (!field.SupportsObjectLifecycle) result.Add(field);
                }
                else result.Add(new UIElementProperty(node, fieldLabel, fieldPath, attachedKey == null ? null : id, attachedKey));
            }
        }
        static bool Simple(Type declaredType)
        {
            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            return type.IsPrimitive || type.IsEnum || type == typeof(string)
            || type == typeof(Stride.Core.Mathematics.Vector2) || type == typeof(Stride.Core.Mathematics.Vector3)
            || type == typeof(Stride.Core.Mathematics.Vector4) || type == typeof(Stride.Core.Mathematics.Color)
            || type == typeof(Stride.Core.Mathematics.Color3) || type == typeof(Stride.Core.Mathematics.Color4) || type == typeof(Thickness)
            || type == typeof(Stride.Core.Mathematics.Int2) || type == typeof(Stride.Core.Mathematics.Int3) || type == typeof(Stride.Core.Mathematics.Int4)
            || type == typeof(Stride.Core.Mathematics.UInt4) || type == typeof(Stride.Core.Mathematics.Matrix)
            || type == typeof(Stride.Core.Mathematics.AngleSingle) || type == typeof(Stride.Core.Mathematics.Quaternion) || type == typeof(Stride.Core.Mathematics.Rectangle) || type == typeof(Stride.Core.Mathematics.RectangleF)
            || type == typeof(decimal) || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory);
        }
    }
    public bool CanEdit(UIElementProperty field) => CanSave && field.Node.MemberDescriptor.HasSet
        && CanEditAssetSettingNode(field.Node) && CanAssignAuthoringNode(field.Node) && !IsExtensionPropertyReadOnly(field.Node)
        && (field.AttachedOwner is not { } id || field.AttachedKey == null || CanChangeDependencyProperty(id, field.AttachedKey));
    public void SetProperty(Guid id, UIElementProperty field, object? value)
    {
        if (Find(id) == null || !Properties(id).Any(owned => ReferenceEquals(owned.Node, field.Node)) || !CanEdit(field)) throw new InvalidOperationException("The UI property cannot be edited.");
        value = NormalizeUIProperty(field, value);
        if (Equals(field.Node.Retrieve(), value)) return;
        Mutate(() => field.Node.Update(value), "Edit " + field.Label);
    }
    public IReadOnlyList<UIElementProperty> CommonProperties(IEnumerable<Guid> ids)
    {
        var members = ids.Select(id => Properties(id)).ToArray();
        if (members.Length == 0) return [];
        return members[0].Where(field => members.Skip(1).All(properties => properties.Any(other => other.Name == field.Name && other.Type == field.Type))).ToArray();
    }
    public void SetProperties(IEnumerable<Guid> ids, string path, object? value)
        => TransformProperties(ids, path, _ => value);
    public void TransformProperties(IEnumerable<Guid> ids, string path, Func<object?, object?> transform)
    {
        var fields = ids.Distinct().Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        if (fields.Length == 0 || fields.Any(field => !CanEdit(field))) throw new InvalidOperationException("The selected UI properties cannot be edited.");
        var replacements = fields.Select(field => (field.Node, Value: NormalizeUIProperty(field, transform(field.Node.Retrieve())))).ToArray();
        if (replacements.Any(change => !new NodeAccessor(change.Node, NodeIndex.Empty).AcceptValue(change.Value!))) throw new InvalidOperationException("A selected UI property has an incompatible replacement.");
        Mutate(() => { foreach (var change in replacements) if (!ContentAssetReference.AreEqual(change.Node.Retrieve(), change.Value)) change.Node.Update(change.Value); }, "Edit " + fields[0].Label);
    }
    private object? NormalizeUIProperty(UIElementProperty field, object? value)
    {
        var previous = field.Node.Retrieve();
        if (ContentAssetReference.AreEqual(previous, value)) return previous;
        if (value is Stride.Core.Mathematics.AngleSingle) PropertyValueValidation.Normalize(value, null);
        AssertUIAxesEditable(field.Node, previous, value);
        // UI layout deliberately uses NaN for automatic dimensions and Infinity
        // for unbounded maxima. Retain these through canonical engine setters.
        var type = Nullable.GetUnderlyingType(field.Type) ?? field.Type;
        var nonFinite = value is float floating && !float.IsFinite(floating) || value is double precise && !double.IsFinite(precise);
        var range = nonFinite ? field.NumericRange : ExtensionNumericRange(field.Node) ?? field.NumericRange;
        value = range?.Normalize(value) ?? (type == typeof(float) && value is double number ? (float)number : value);
        return NativeNumericAxes.Normalize(value, previous, UIAxisNumericRanges(field.Node));
    }
    public void Rename(Guid id, string name)
    {
        var field = Properties(id).Single(property => property.Name == nameof(UIElement.Name));
        SetProperty(id, field, name);
    }
    public bool CanChangePropertyObject(UIElementProperty field) => CanEdit(field) && field.SupportsObjectLifecycle
        && !AuthoringGraph.Definition.IsMemberTargetObjectReference(field.Node, field.Node.Retrieve());
    public void ChangePropertyObjects(IEnumerable<Guid> ids, string path, Type? type)
    {
        var fields = ids.Distinct().Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        if (fields.Length == 0 || fields.Any(field => !CanChangePropertyObject(field) || (type == null ? !field.AllowsNull : !field.ObjectTypes.Contains(type))))
            throw new InvalidOperationException("The selected UI objects cannot be changed.");
        var graph = AuthoringGraph;
        var prepared = fields.Where(field => field.Node.Retrieve()?.GetType() != type).Select(field =>
        {
            object? after = null;
            if (type != null)
            {
                var created = ObjectFactoryRegistry.NewInstance(type);
                if (created == null || !field.Type.IsInstanceOfType(created)) throw new InvalidOperationException("The object factory returned an incompatible UI property value.");
                var node = graph.Container.NodeContainer.GetOrCreateNode(created);
                after = AssetCloner.Clone(created, AssetClonerFlags.GenerateNewIdsForIdentifiableObjects,
                    ExternalReferenceCollector.GetExternalReferences(graph.Definition, node), out _);
                AssetCollectionItemIdHelper.GenerateMissingItemIds(after!);
            }
            var removed = field.Node.Target == null ? new HashSet<Guid>() : IdentifiableObjectCollector.Collect(graph.Definition, field.Node.Target).Keys.ToHashSet();
            if (field.Node.Retrieve() is IIdentifiable identifiable) removed.Add(identifiable.Id);
            return (field.Node, After: after, Removed: removed);
        }).ToArray();
        if (prepared.Length == 0) return;
        Mutate(() =>
        {
            foreach (var change in prepared) change.Node.Update(change.After);
            graph.ClearReferencesToObjects(prepared.SelectMany(change => change.Removed).Distinct());
        }, "Edit " + fields[0].Label);
    }
    public void CommitRuntimeLayout(Guid id, UIElement runtime)
    {
        var element = Require(id);
        if (runtime.Id != id) throw new ArgumentException("The runtime element does not match the authored element.", nameof(runtime));
        var authored = ObjectNode(element);
        Mutate(() =>
        {
            Update(nameof(UIElement.Margin), runtime.Margin);
            Update(nameof(UIElement.Width), runtime.Width);
            Update(nameof(UIElement.Height), runtime.Height);
            Update(nameof(UIElement.HorizontalAlignment), runtime.HorizontalAlignment);
            Update(nameof(UIElement.VerticalAlignment), runtime.VerticalAlignment);
        }, "Change UI element layout");
        void Update(string name, object value)
        {
            var node = authored[name];
            if (!Equals(node.Retrieve(), value)) node.Update(value);
        }
    }
    public AssetCompositeHierarchyData<UIElementDesign, UIElement> CloneSelection(IEnumerable<Guid> ids, bool newIdentifiers = true)
        => UIAssetPropertyGraph.CloneSubHierarchies(UIHierarchyGraph.Container.NodeContainer,
            UIHierarchyGraph.Container.NodeContainer, Asset, CommonRoots(ids),
            newIdentifiers ? SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects : SubHierarchyCloneFlags.None, out _);
    public IReadOnlyList<Guid> Duplicate(IEnumerable<Guid> ids)
    {
        var source = CommonRoots(ids);
        if (source.Count == 0) return [];
        if (Asset is UIPageAsset && source.Any(id => Require(id).VisualParent == null))
            throw new InvalidOperationException("A UI page can have only one root.");
        var result = new List<Guid>();
        Mutate(() =>
        {
            foreach (var id in source)
            {
                var element = Require(id);
                var clone = CloneSelection([id]);
                AssetPartsAnalysis.GenerateNewBaseInstanceIds(clone);
                var root = clone.RootParts.Single();
                UIHierarchyGraph.AddPartToAsset(clone.Parts, clone.Parts[root.Id], element.VisualParent, Asset.IndexOf(element) + 1);
                result.Add(root.Id);
            }
        }, "Duplicate UI elements");
        return result;
    }
    public string CopyHierarchy(IEnumerable<Guid> ids)
    {
        CommitEdits(); var hierarchy = CloneSelection(ids, false);
        if (hierarchy.RootParts.Count == 0) throw new InvalidOperationException("No UI elements are selected.");
        return Clipboard.CopyFromAsset(UIHierarchyGraph, Asset.Id, hierarchy, false);
    }
    public bool CanPasteHierarchy(string text, Guid? parentId)
    {
        var parent = parentId is { } id ? Find(id) : null;
        if (parentId != null && parent == null || !CanInsert(parent)) return false;
        try { return Clipboard.CanPaste(text, Asset.GetType(), parent == null ? typeof(AssetCompositeHierarchyData<UIElementDesign, UIElement>) : typeof(UIElement)); }
        catch { return false; }
    }
    public IReadOnlyList<Guid> PasteHierarchy(string text, Guid? parentId, Func<AssetId, UIDocument?> resolve)
    {
        if (!CanPasteHierarchy(text, parentId)) throw new InvalidOperationException("The clipboard cannot be pasted into this UI container.");
        CommitEdits();
        var parent = parentId is { } id ? Require(id) : null;
        var data = Clipboard.DeserializeCopiedData(text, Asset, parent == null ? typeof(AssetCompositeHierarchyData<UIElementDesign, UIElement>) : typeof(UIElement));
        var hierarchies = data.Items.Select(item => item.Data as AssetCompositeHierarchyData<UIElementDesign, UIElement>
            ?? throw new InvalidOperationException("The clipboard does not contain a UI hierarchy.")).ToArray();
        var roots = hierarchies.SelectMany(hierarchy => hierarchy.RootParts).ToArray();
        if (roots.Length == 0 || roots.Length > 1 && (parent is ContentControl || parent == null && Asset is UIPageAsset))
            throw new InvalidOperationException("The UI destination accepts only one root element.");
        PrepareHierarchyLibraryLinks(hierarchies, resolve);
        Mutate(() =>
        {
            foreach (var hierarchy in hierarchies)
                foreach (var root in hierarchy.RootParts)
                    UIHierarchyGraph.AddPartToAsset(hierarchy.Parts, hierarchy.Parts[root.Id], parent, parent?.VisualChildren.Count ?? Roots.Count);
            UIHierarchyGraph.RefreshBase(); UIHierarchyGraph.ReconcileWithBase();
        }, "Paste UI elements");
        return roots.Select(root => root.Id).ToArray();
    }
    public void SetDependencyProperty<T>(Guid id, PropertyKey<T> key, T value)
        => SetDependencyProperties([id], key, value);
    public void SetDependencyProperties<T>(IEnumerable<Guid> ids, PropertyKey<T> key, T value)
    {
        TransformDependencyProperties(ids.Distinct().ToArray(), key, _ => value);
    }
    public void AddGridDefinition(Guid id, string name)
    {
        AddCollectionItem(GridDefinitionCollection(id, name), typeof(StripDefinition));
    }
    public void RemoveGridDefinition(Guid id, string name, int index)
    {
        var collection = GridDefinitionCollection(id, name);
        var item = collection.Items.SingleOrDefault(item => item.Index == new NodeIndex(index))
            ?? throw new InvalidOperationException("The grid definition cannot be removed.");
        RemoveCollectionItem(item);
    }
    internal UIPropertyCollection GridDefinitionCollection(Guid id, string name)
    {
        var node = GridDefinitions(id, name);
        return PropertyCollections(id).Single(collection => collection.Path == name && ReferenceEquals(collection.Collection, node));
    }
    private IAssetObjectNode GridDefinitions(Guid id, string name)
    {
        if (Require(id) is not Grid || name is not nameof(Grid.RowDefinitions) and not nameof(Grid.ColumnDefinitions) and not nameof(Grid.LayerDefinitions))
            throw new ArgumentException("The selected property is not a grid definition collection.");
        return (IAssetObjectNode)ObjectNode(Require(id))[name].Target!;
    }
    public UIAssetBase ExportSelection(IEnumerable<Guid> ids, bool library)
        => PrepareExport(ids, library).Asset;
    public UIAssetExport PrepareExport(IEnumerable<Guid> ids, bool library)
    {
        var ordered = ids.Distinct().ToArray(); var common = CommonRoots(ordered).ToHashSet();
        var roots = ordered.Where(common.Contains).ToArray();
        var hierarchy = UIAssetPropertyGraph.CloneSubHierarchies(UIHierarchyGraph.Container.NodeContainer,
            UIHierarchyGraph.Container.NodeContainer, Asset, roots,
            SubHierarchyCloneFlags.CleanExternalReferences | SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects, out var remapping);
        AssetPartsAnalysis.GenerateNewBaseInstanceIds(hierarchy);
        foreach (var pair in remapping)
            if (Asset.Hierarchy.Parts.TryGetValue(pair.Key, out var original) && hierarchy.Parts.TryGetValue(pair.Value, out var clone))
                AssetPropertyGraph.ApplyOverrides((IAssetNode)ObjectNode(clone.UIElement), AssetPropertyGraph.GenerateOverridesForSerialization((IAssetNode)ObjectNode(original.UIElement)));
        if (hierarchy.RootParts.Count == 0 || !library && hierarchy.RootParts.Count != 1)
            throw new InvalidOperationException("A UI page requires exactly one root element.");
        UIAssetBase result = library ? new UILibraryAsset() : new UIPageAsset();
        result.Hierarchy = hierarchy;
        result.Design.Resolution = Asset.Design.Resolution;
        if (result is UILibraryAsset exported)
            foreach (var root in hierarchy.RootParts.Where(root => !string.IsNullOrWhiteSpace(root.Name)))
                exported.PublicUIElements.Add(root.Id, root.Name!);
        return new UIAssetExport(result, remapping)
        { Source = this, SourceRevision = EditRevision, SourceLayout = LayoutVersion, SourcePath = Path, SourceRoots = roots };
    }
    public void RelinkExportedLibrary(UIAssetExport export, UIDocument library, string libraryUrl)
    {
        if (export.Asset is not UILibraryAsset || library.Asset.Id != export.Asset.Id) throw new ArgumentException("The export is not the published UI library.");
        SessionHistory!.UiLibraryGraphs.RegisterPreparedBase(this, library);
        var instance = Guid.NewGuid();
        Mutate(() =>
        {
            foreach (var pair in export.IdRemapping)
                if (Asset.Hierarchy.Parts.TryGetValue(pair.Key, out var design) && library.Asset.Hierarchy.Parts.ContainsKey(pair.Value))
                    ObjectNode(design)[nameof(UIElementDesign.Base)].Update(new BasePart(new AssetReference(library.Asset.Id, libraryUrl), pair.Value, instance));
            UIHierarchyGraph.RefreshBase();
            foreach (var pair in export.IdRemapping)
                if (Find(pair.Key) is { } element) ((IAssetNode)ObjectNode(element)).ResetOverrideRecursively();
        }, "Link UI elements to new library");
    }
    public bool CanReset(UIElementProperty field) => CanEdit(field) && field.Node is IAssetMemberNode { CanOverride: true } node && node.IsContentOverridden();
    public bool CanResetProperties(IEnumerable<Guid> ids, string path)
    {
        var fields = ids.Distinct().Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        return fields.Length != 0 && fields.All(CanEdit) && fields.Any(CanReset);
    }
    public void ResetProperties(IEnumerable<Guid> ids, string path)
    {
        var fields = ids.Distinct().Select(id => Properties(id).Single(field => field.Name == path)).ToArray();
        if (fields.Length == 0 || !fields.All(CanEdit) || !fields.Any(CanReset)) throw new InvalidOperationException("The selected UI properties cannot be reset.");
        Mutate(() => { foreach (var field in fields.Where(CanReset)) ((IAssetNode)field.Node).ResetOverrideRecursively(); }, "Reset " + fields[0].Label);
    }
    public void ResetProperty(Guid id, UIElementProperty field)
    {
        if (!Properties(id).Any(owned => ReferenceEquals(owned.Node, field.Node)) || !CanReset(field)) throw new InvalidOperationException("The UI property cannot be reset.");
        Mutate(() => ((IAssetNode)field.Node).ResetOverrideRecursively(), "Reset " + field.Label);
    }
    public void SetResolution(Stride.Core.Mathematics.Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || value.X <= 0 || value.Y <= 0 || value.Z <= 0)
            throw new ArgumentException("UI design resolution must be finite and positive.", nameof(value));
        var node = ObjectNode(Asset.Design)[nameof(UIAssetBase.UIDesign.Resolution)];
        Mutate(() => node.Update(value), "Change UI design resolution");
    }
    public void BeginEdit(string title) => continuousEdit ??= SessionHistory!.CreateTransaction(title);
    public void EndEdit() { var edit = continuousEdit; continuousEdit = null; edit?.Dispose(); }
    public override void CommitEdits() { if (!applyingUIEdit) { CommitElementRename(); EndEdit(); } base.CommitEdits(); }
    private void Mutate(Action edit, string title)
    {
        if (!CanSave) throw new InvalidOperationException("This UI asset cannot be edited.");
        applyingUIEdit = true;
        try { ApplyInheritedAssetMutation(edit, title); }
        finally { applyingUIEdit = false; }
    }
    private UIElement Require(Guid id) => Find(id) ?? throw new InvalidOperationException("The UI element is not owned by this asset.");
    public bool CanInsert(UIElement? parent)
    {
        if (!CanSave) return false;
        if (parent == null) return Asset is UILibraryAsset || Asset.Hierarchy.RootParts.Count == 0;
        if (!Asset.Hierarchy.Parts.ContainsKey(parent.Id)) return false;
        return parent is Panel || parent is ContentControl { Content: null };
    }
    public Guid Insert(AssetCompositeHierarchyData<UIElementDesign, UIElement> hierarchy, Guid? parentId = null, int? index = null)
    {
        var parent = parentId is { } id ? Require(id) : null;
        if (!CanInsert(parent) || hierarchy.RootParts.Count != 1) throw new InvalidOperationException("The selected container cannot accept this UI element.");
        var root = hierarchy.RootParts[0]; var design = hierarchy.Parts[root.Id];
        if (hierarchy.Parts.Keys.Any(Asset.Hierarchy.Parts.ContainsKey)) throw new InvalidOperationException("The UI element identifiers are already owned.");
        var insertion = index ?? (parent == null ? Roots.Count : parent.VisualChildren.Count);
        Mutate(() => UIHierarchyGraph.AddPartToAsset(hierarchy.Parts, design, parent, insertion), "Add UI element");
        return root.Id;
    }
    public Guid InstantiateLibrary(UIDocument library, Guid elementId, string libraryUrl, bool systemLibrary, Guid? parentId = null)
        => Insert(CreateLibraryInstance(library, elementId, libraryUrl, systemLibrary), parentId);
    private AssetCompositeHierarchyData<UIElementDesign, UIElement> CreateLibraryInstance(UIDocument library, Guid elementId, string libraryUrl, bool systemLibrary)
    {
        if (library.Asset is not UILibraryAsset source || !source.PublicUIElements.ContainsKey(elementId))
            throw new InvalidOperationException("The selected UI library element is not public.");
        AssetCompositeHierarchyData<UIElementDesign, UIElement> hierarchy;
        if (systemLibrary)
        {
            hierarchy = UIAssetPropertyGraph.CloneSubHierarchies(library.UIHierarchyGraph.Container.NodeContainer,
                UIHierarchyGraph.Container.NodeContainer, source, [elementId],
                SubHierarchyCloneFlags.GenerateNewIdsForIdentifiableObjects | SubHierarchyCloneFlags.RemoveOverrides, out _);
            foreach (var part in hierarchy.Parts.Values) { part.Base = null; part.UIElement.Name = null; }
        }
        else hierarchy = source.CreateElementInstance(Asset, libraryUrl, elementId);
        if (!systemLibrary && UIHierarchyGraph.Container.TryGetGraph(source.Id) == null)
            SessionHistory!.UiLibraryGraphs.RegisterPreparedBase(this, library);
        return hierarchy;
    }
    public Guid GroupInto(IEnumerable<Guid> ids, UIDocument library, Guid elementId, string libraryUrl, bool systemLibrary)
    {
        var ordered = ids.Distinct().ToArray(); var rootIds = CommonRoots(ordered).ToHashSet();
        var roots = ordered.Where(rootIds.Contains).Select(Require).ToArray();
        if (roots.Length == 0 || roots.Any(root => !ReferenceEquals(root.VisualParent, roots[0].VisualParent)))
            throw new InvalidOperationException("Grouped UI elements must be siblings.");
        if (!systemLibrary || library.Asset is not UILibraryAsset publicLibrary || !publicLibrary.PublicUIElements.ContainsKey(elementId)
            || library.Find(elementId) is not Panel template)
            throw new InvalidOperationException("Grouping requires a standard library panel.");
        // GroupInto uses the system factory's type, not its authored clone/preset.
        // Ordinary palette Add/drag keeps CreateLibraryInstance and its native template data.
        var type = template.GetType();
        var container = (Panel)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("The layout type cannot be created."));
        var containerDesign = new UIElementDesign(container);
        var containerParts = new AssetPartCollection<UIElementDesign, UIElement>(); containerParts.Add(containerDesign);
        var parent = roots[0].VisualParent;
        var moved = new AssetPartCollection<UIElementDesign, UIElement>();
        foreach (var root in roots) foreach (var element in Enumerate(root)) moved.Add(Asset.Hierarchy.Parts[element.Id]);
        var designs = roots.Select(root => Asset.Hierarchy.Parts[root.Id]).ToArray();
        Mutate(() =>
        {
            foreach (var design in designs) UIHierarchyGraph.RemovePartFromAsset(design);
            UIHierarchyGraph.AddPartToAsset(containerParts, containerDesign, parent, parent?.VisualChildren.Count ?? Roots.Count);
            foreach (var design in designs) UIHierarchyGraph.AddPartToAsset(moved, design, container, container.VisualChildren.Count);
        }, "Group into " + type.Name);
        return container.Id;
    }
    public void Delete(IReadOnlyCollection<Guid> ids)
    {
        var roots = CommonRoots(ids).Select(id => Asset.Hierarchy.Parts[id]).ToArray();
        if (roots.Length == 0) return;
        Mutate(() =>
        {
            if (Asset is UILibraryAsset library)
            {
                var exports = ObjectNode(library)[nameof(UILibraryAsset.PublicUIElements)].Target!;
                var removed = roots.SelectMany(root => Enumerate(root.UIElement)).Select(element => element.Id).ToHashSet();
                foreach (var id in library.PublicUIElements.Keys.Where(removed.Contains).ToArray()) exports.Remove(library.PublicUIElements[id], new NodeIndex(id));
            }
            UIHierarchyGraph.DeleteParts(roots, out _);
        }, "Delete UI elements");
    }
    public IReadOnlyList<Guid> CommonRoots(IEnumerable<Guid> ids)
    {
        var owned = ids.Where(id => Find(id) != null).ToHashSet();
        return owned.Where(id => !Ancestors(Require(id)).Any(parent => owned.Contains(parent.Id))).ToArray();
    }
    private static IEnumerable<UIElement> Ancestors(UIElement element)
    {
        for (var parent = element.VisualParent; parent != null; parent = parent.VisualParent) yield return parent;
    }
    internal static IEnumerable<UIElement> Enumerate(UIElement root)
    {
        yield return root;
        foreach (var child in root.VisualChildren) foreach (var descendant in Enumerate(child)) yield return descendant;
    }
    public void Reparent(Guid id, Guid? newParentId, int? index = null)
    {
        var element = Require(id); var parent = newParentId is { } target ? Require(target) : null;
        if (parent == element || parent != null && Ancestors(parent).Contains(element)) throw new InvalidOperationException("UI hierarchies cannot contain cycles.");
        var oldParent = element.VisualParent;
        if (parent is ContentControl content && content.Content != null && content.Content != element) throw new InvalidOperationException("This control already has content.");
        if (parent != null && parent is not Panel and not ContentControl) throw new InvalidOperationException("The target cannot contain UI children.");
        if (parent == null && Asset is UIPageAsset && Roots.Any(root => root != element)) throw new InvalidOperationException("A UI page can have only one root.");
        var oldIndex = Asset.IndexOf(element);
        var insertion = index ?? (parent == null ? Roots.Count : parent.VisualChildren.Count);
        if (ReferenceEquals(oldParent, parent) && oldIndex < insertion) insertion--;
        var parts = new AssetPartCollection<UIElementDesign, UIElement>();
        foreach (var descendant in Enumerate(element)) parts.Add(Asset.Hierarchy.Parts[descendant.Id]);
        var design = Asset.Hierarchy.Parts[id];
        Mutate(() => { UIHierarchyGraph.RemovePartFromAsset(design); UIHierarchyGraph.AddPartToAsset(parts, design, parent, Math.Max(0, insertion)); }, "Move UI element");
    }
    public void Reorder(Guid id, int delta)
    {
        var element = Require(id); var index = Asset.IndexOf(element); var count = element.VisualParent?.VisualChildren.Count ?? Roots.Count;
        var target = Math.Clamp(index + delta, 0, count - 1);
        if (target != index) Reparent(id, element.VisualParent?.Id, target > index ? target + 1 : target);
    }
    public Guid ReplacePanel(Guid id, UIElementFactory factory)
    {
        if (!factory.SystemLibrary || Require(id) is not Panel source || factory.Library.Find(factory.ElementId) is not Panel template)
            throw new InvalidOperationException("Layout replacement requires a standard library panel.");
        var type = template.GetType();
        if (source.GetType() == type) return id;
        var target = (Panel)(Activator.CreateInstance(type) ?? throw new InvalidOperationException("The layout type cannot be created."));
        var sourceNode = ObjectNode(source); var targetNode = ObjectNode(target);
        foreach (var member in targetNode.Members.Where(member => member.Name is not nameof(Panel.Children) and not nameof(UIElement.Id) and not nameof(UIElement.DependencyProperties) && member.MemberDescriptor.HasSet))
            if (sourceNode.TryGetChild(member.Name) is { } original && member.Type == original.Type) member.Update(AssetCloner.Clone(original.Retrieve()));
        source.DependencyProperties.CopyTo(target.DependencyProperties);
        var children = source.Children.ToArray();
        var dimensions = source switch
        {
            Grid grid => (Columns: Math.Max(1, grid.ColumnDefinitions.Count), Rows: Math.Max(1, grid.RowDefinitions.Count), Layers: Math.Max(1, grid.LayerDefinitions.Count)),
            UniformGrid grid => (Columns: grid.Columns, Rows: grid.Rows, Layers: grid.Layers),
            StackPanel stack => (Columns: stack.Orientation == Orientation.Horizontal ? Math.Max(1, children.Length) : 1,
                Rows: stack.Orientation == Orientation.Vertical ? Math.Max(1, children.Length) : 1, Layers: stack.Orientation == Orientation.InDepth ? Math.Max(1, children.Length) : 1),
            _ => (Columns: 1, Rows: 1, Layers: 1)
        };
        if (target is StackPanel stackTarget && source is GridBase)
        {
            stackTarget.Orientation = dimensions.Rows >= dimensions.Columns && dimensions.Rows >= dimensions.Layers ? Orientation.Vertical
                : dimensions.Columns >= dimensions.Layers ? Orientation.Horizontal : Orientation.InDepth;
            children = children.OrderBy(child => child.DependencyProperties.Get(GridBase.RowPropertyKey))
                .ThenBy(child => child.DependencyProperties.Get(GridBase.ColumnPropertyKey)).ThenBy(child => child.DependencyProperties.Get(GridBase.LayerPropertyKey)).ToArray();
        }
        if (target is Grid targetGrid && source is GridBase or StackPanel)
        {
            var stripType = source is StackPanel ? StripType.Auto : StripType.Star;
            for (var index = 0; index < dimensions.Columns; index++) targetGrid.ColumnDefinitions.Add(new StripDefinition(stripType));
            for (var index = 0; index < dimensions.Rows; index++) targetGrid.RowDefinitions.Add(new StripDefinition(stripType));
            for (var index = 0; index < dimensions.Layers; index++) targetGrid.LayerDefinitions.Add(new StripDefinition(stripType));
        }
        if (target is UniformGrid uniform && source is GridBase or StackPanel)
        { uniform.Columns = dimensions.Columns; uniform.Rows = dimensions.Rows; uniform.Layers = dimensions.Layers; }
        var parent = source.VisualParent; var insertion = Asset.IndexOf(source);
        var moved = new AssetPartCollection<UIElementDesign, UIElement>();
        foreach (var child in children) foreach (var element in Enumerate(child)) moved.Add(Asset.Hierarchy.Parts[element.Id]);
        var design = new UIElementDesign(target); var parts = new AssetPartCollection<UIElementDesign, UIElement>(); parts.Add(design);
        Mutate(() =>
        {
            foreach (var child in children) UIHierarchyGraph.RemovePartFromAsset(moved[child.Id]);
            UIHierarchyGraph.RemovePartFromAsset(Asset.Hierarchy.Parts[id]);
            UIHierarchyGraph.AddPartToAsset(parts, design, parent, insertion);
            for (var index = 0; index < children.Length; index++)
            {
                var child = children[index]; UIHierarchyGraph.AddPartToAsset(moved, moved[child.Id], target, index);
                if (source is Canvas) foreach (var key in new PropertyKey[] { Canvas.AbsolutePositionPropertyKey, Canvas.PinOriginPropertyKey, Canvas.RelativePositionPropertyKey, Canvas.RelativeSizePropertyKey }) RemoveDependency(child, key);
                if (source is GridBase && target is StackPanel or Canvas)
                    foreach (var key in new PropertyKey[] { GridBase.RowPropertyKey, GridBase.RowSpanPropertyKey, GridBase.ColumnPropertyKey, GridBase.ColumnSpanPropertyKey, GridBase.LayerPropertyKey, GridBase.LayerSpanPropertyKey }) RemoveDependency(child, key);
                if (source is StackPanel oldStack && target is GridBase)
                {
                    var key = oldStack.Orientation == Orientation.Horizontal ? GridBase.ColumnPropertyKey : oldStack.Orientation == Orientation.Vertical ? GridBase.RowPropertyKey : GridBase.LayerPropertyKey;
                    var node = ObjectNode(child)[nameof(UIElement.DependencyProperties)].Target!; var position = new NodeIndex(key);
                    if (node.Indices!.Contains(position)) node.Update(index, position); else node.Add(index, position);
                }
            }
            if (Asset is UILibraryAsset library && library.PublicUIElements.TryGetValue(id, out var name))
            {
                var exports = ObjectNode(library)[nameof(UILibraryAsset.PublicUIElements)].Target!;
                exports.Remove(name, new NodeIndex(id)); exports.Add(name, new NodeIndex(target.Id));
            }
        }, "Change layout type to " + type.Name);
        return target.Id;
        void RemoveDependency(UIElement element, PropertyKey key)
        {
            var node = ObjectNode(element)[nameof(UIElement.DependencyProperties)].Target!; var position = new NodeIndex(key);
            if (node.Indices!.Contains(position)) node.Remove(node.Retrieve(position)!, position);
        }
    }
    public void BreakLibraryLinks(IReadOnlyCollection<Guid> ids)
    {
        var parts = CommonRoots(ids).SelectMany(id => Enumerate(Require(id))).Select(element => Asset.Hierarchy.Parts[element.Id]).ToArray();
        Mutate(() => UIHierarchyGraph.BreakBasePartLinks(parts), "Break link to UI library");
    }
    public BasePart? SourceLibrary(Guid id) => Asset.Hierarchy.Parts.TryGetValue(id, out var part) ? part.Base : null;
    public void SetPublicName(Guid id, string? name)
    {
        Require(id);
        if (Asset is not UILibraryAsset library) throw new InvalidOperationException("Only UI libraries expose public elements.");
        if (Require(id).VisualParent != null) throw new InvalidOperationException("Only root elements can be exposed by a UI library.");
        var node = ObjectNode(library)[nameof(UILibraryAsset.PublicUIElements)].Target!;
        Mutate(() =>
        {
            if (library.PublicUIElements.ContainsKey(id)) { if (string.IsNullOrWhiteSpace(name)) node.Remove(library.PublicUIElements[id], new NodeIndex(id)); else node.Update(name, new NodeIndex(id)); }
            else if (!string.IsNullOrWhiteSpace(name)) node.Add(name, new NodeIndex(id));
        }, "Change public UI element name");
    }
    public void ResolveLibraries(Func<AssetId, UIDocument?> resolve)
    {
        _ = UIHierarchyGraph;
        var service = SessionHistory?.UiLibraryGraphs ?? throw new InvalidOperationException("The UI asset has no canonical session.");
        service.Refresh(this);
        if (CanSave) Mutate(() => UIHierarchyGraph.ReconcileWithBase(), "Refresh UI library instances");
    }
    protected override AssetMetadataDocument CreateLoadedGeneration(string path, AssetLoadResult<Asset> restored, byte[] baseline, EditorSessionHistory? history, bool readOnly)
        => new UIDocument(path, restored, baseline, history, readOnly);
}
