using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Rules;
using Stride.Core.Extensions;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Rendering.Images;
using Stride.Rendering.Compositing;
using Stride.Core.Presentation.Quantum.Presenters;

namespace Stride.Editor.ImGui;

internal sealed class CompositorProperty(NodeAccessor accessor, string key, string label)
{
    public NodeAccessor Accessor { get; } = accessor;
    internal INodePresenter? NativePresenter { get; set; }
    public string Key { get; } = key;
    public string Label { get; internal set; } = label;
    public Type DeclaredType => Accessor.IsItem ? Accessor.Node.Descriptor.GetInnerCollectionType() : Accessor.Node.Type;
    public Type Type => Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
    public bool IsNullable => Nullable.GetUnderlyingType(DeclaredType) != null;
    public IReadOnlyList<CompositorProperty> Children { get; internal set; } = [];
    public IReadOnlyList<CompositorProperty> Items { get; internal set; } = [];
    public IAssetObjectNode? Collection { get; internal set; }
    public IReadOnlyList<Type> Types { get; internal set; } = [];
    public string[] EnumNames { get; internal set; } = [];
    public string[] EnumLabels { get; internal set; } = [];
    public PropertyNumericRange? Range { get; internal set; }
    public PropertyNumericMetadata? AttachedNumericMetadata { get; internal set; }
    public bool UsesRangedNumericTemplate { get; internal set; }
    public IReadOnlyList<PropertyNumericRange?> VectorRanges { get; internal set; } = [];
    public ItemId ItemId { get; internal set; }
    public IReadOnlyList<Type> ItemTypes { get; internal set; } = [];
    public MemberCollectionAttribute? Rules { get; internal set; }
    public CompositorProperty? ParentCollection { get; internal set; }
    public CompositorProperty? InlineMember { get; internal set; }
    public CompositorProperty? PresentationOwner { get; internal set; }
    public CompositorProperty? EnabledMember { get; internal set; }
    public IReadOnlyList<CompositorProperty> Details { get; internal set; } = [];
    public ExpandRule Expansion { get; internal set; } = ExpandRule.Auto;
    public string Category { get; internal set; } = "";
    public int Order { get; internal set; }
    public CategoryOrderAttribute? CategoryMetadata { get; internal set; }
    public int? NativeCategoryOrder { get; internal set; }
    public ExpandRule? NativeCategoryExpansion { get; internal set; }
    public bool IsNavigationGroup { get; internal set; }
    public bool CanBeNull { get; internal set; }
    public bool AllowsNull => CanBeNull && ParentCollection?.Rules?.NotNullItems != true;
}

internal partial class AssetMetadataDocument
{
    private readonly Dictionary<IObjectNode, IReadOnlyList<CompositorProperty>> compositorProperties = [];
    private readonly HashSet<CompositorProperty> compositorPropertyMembers = [];
    private long compositorPropertiesRevision = -1;
    private int compositorPropertiesAssemblyVersion = -1;
    private long? compositorPropertiesExtensionVersion;
    private CompositorProperty? activeCompositorProperty;
    private object? compositorPropertyStart, compositorPropertyPending;
    private readonly Dictionary<Guid, Stride.Core.Mathematics.Vector2> pendingCompositorPositions = [];
    internal bool HasCompositorSettingChange => pendingCompositorPositions.Count > 0 || activeCompositorProperty != null && !Equals(compositorPropertyStart, compositorPropertyPending);
    public IReadOnlyList<CompositorProperty> GetCompositorProperties(IObjectNode root, bool entries = false)
    {
        if (compositorPropertiesRevision != EditRevision || compositorPropertiesAssemblyVersion != CompositorTypeCatalog.Version
            || compositorPropertiesExtensionVersion != SessionHistory?.Extensions?.Version)
        {
            compositorProperties.Clear(); compositorNativeProperties.Clear(); compositorPropertyMembers.Clear(); compositorPropertyGroups.Clear(); compositorAggregateGroups.Clear(); compositorPropertiesRevision = EditRevision;
            compositorPropertiesAssemblyVersion = CompositorTypeCatalog.Version;
            compositorPropertiesExtensionVersion = SessionHistory?.Extensions?.Version;
        }
        if (!compositorProperties.TryGetValue(root, out var properties))
            compositorProperties.Add(root, properties = ReadCompositorProperties(root, "", 0, new HashSet<IObjectNode>(), entries));
        return properties;
    }
    private IReadOnlyList<CompositorProperty> ReadCompositorProperties(IObjectNode target, string prefix, int depth, HashSet<IObjectNode> visited, bool entries = false)
    {
        if (target.Retrieve() is Stride.Core.Yaml.IUnloadable || depth > 24 || !visited.Add(target)) return [];
        var properties = new List<CompositorProperty>();
        foreach (var member in target.Members)
        {
            if (member.Name == nameof(IIdentifiable.Id) && typeof(IIdentifiable).IsAssignableFrom(target.Type)
                || entries && member.Name is not "Game" and not "Editor" and not "SingleView") continue;
            if (target.Retrieve() is FXAAEffect { Dither: FXAAEffect.DitherType.None } && member.Name == nameof(FXAAEffect.Quality)) continue;
            var display = member.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault(); if (display?.Browsable == false) continue;
            var property = CreateCompositorProperty(new(member, NodeIndex.Empty), prefix + "." + member.Name, display?.Name ?? member.Name);
            PopulateCompositorProperty(property, member.Target, depth + 1, visited,
                member.MemberDescriptor.GetCustomAttributes<MemberCollectionAttribute>(true).FirstOrDefault());
            ApplyCompositorPresentation(property);
            if (Stride.Assets.Presentation.NodePresenters.Rules.NavigationNodeRules.BypassMember(target.Type, member.Name)) properties.AddRange(property.Children);
            else properties.Add(property);
        }
        visited.Remove(target); return properties;
    }
    private void PopulateCompositorProperty(CompositorProperty property, IObjectNode? target, int depth, HashSet<IObjectNode> visited, MemberCollectionAttribute? rules = null)
    {
        if (target == null || depth > 24) return;
        var value = property.Accessor.RetrieveValue();
        // Native unknown objects serialize their retained event stream, not
        // reflected members of the generated proxy's base class.
        if (value is Stride.Core.Yaml.IUnloadable) return;
        if (property.Accessor.Node is IMemberNode member && AuthoringGraph.Definition.IsMemberTargetObjectReference(member, value)
            || property.Accessor.Node is IObjectNode owner && property.Accessor.IsItem && AuthoringGraph.Definition.IsTargetItemObjectReference(owner, property.Accessor.Index, value)) return;
        if (target is IAssetObjectNode collection && collection.Descriptor is CollectionBaseDescriptor)
        {
            property.Collection = collection;
            property.Rules = rules ?? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(collection.Type);
            var elementType = collection.Descriptor.GetInnerCollectionType();
            var contentTypes = AssetRegistry.CanPropertyHandleContent(elementType, out var registeredContent) ? registeredContent : [];
            property.ItemTypes = elementType.GetInheritedInstantiableTypes().Append(Nullable.GetUnderlyingType(elementType) ?? elementType).Distinct()
                .Where(type => (type.IsValueType || ObjectFactoryRegistry.CanCreateInstance(type)) && !IsExistingCompositorReferenceType(type)
                    && !contentTypes.Contains(type) && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) == null
                    && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ObsoleteAttribute>(type) == null)
                .OrderBy(DisplayAttribute.GetDisplayName, StringComparer.Ordinal).ToArray();
            if (!visited.Add(collection)) return;
            property.Items = (collection.Indices ?? []).Where(index => IsExtensionItemVisible(collection, index)).Select(index =>
            {
                var item = CreateCompositorProperty(new(collection, index), property.Key + "/" + collection.IndexToId(index), index.ToString());
                item.ItemId = collection.IndexToId(index); item.ParentCollection = property;
                PopulateCompositorProperty(item, collection.ItemReferences != null ? collection.IndexedTarget(index) : null, depth + 1, visited);
                ApplyCompositorPresentation(item); return item;
            }).ToArray();
            visited.Remove(collection);
        }
        else if (!IsCompositorValueType(property.Type) && !IsCompositorReferenceType(property.Type, value))
            property.Children = ReadCompositorProperties(target, property.Key, depth, visited);
    }
    private void ApplyCompositorPresentation(CompositorProperty property)
    {
        var type = property.Accessor.RetrieveValue()?.GetType() ?? property.Type;
        var definition = InlineMemberRules.FindMainProperty(type);
        property.InlineMember = property.Children.FirstOrDefault(child => child.Accessor.IsMember && ((IMemberNode)child.Accessor.Node).Name == definition.Name);
        property.EnabledMember = property.Children.FirstOrDefault(child => child.Accessor.IsMember && ((IMemberNode)child.Accessor.Node).Name == "Enabled" && child.Type == typeof(bool));
        property.Details = property.Children.Where(child => !ReferenceEquals(child, property.InlineMember) && !ReferenceEquals(child, property.EnabledMember)).ToArray();
        var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(type);
        var memberDisplay = property.Accessor.Node is IMemberNode member ? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(member.MemberDescriptor.MemberInfo) : null;
        property.Expansion = property.InlineMember != null ? definition.Expand
            : memberDisplay is { Expand: not ExpandRule.Auto } ? memberDisplay.Expand : display?.Expand ?? ExpandRule.Auto;
        if (property.InlineMember is { } inline)
        {
            inline.Label = property.Label;
            inline.PresentationOwner = property;
            if (property.AttachedNumericMetadata is { } source && AssetSettingNumericMetadata.Supports(inline.Type))
            {
                var target = inline.AttachedNumericMetadata ?? new();
                var transferred = new PropertyNumericMetadata(source.Minimum ?? target.Minimum, source.Maximum ?? target.Maximum,
                    source.SmallStep ?? target.SmallStep, source.LargeStep ?? target.LargeStep, source.DecimalPlaces ?? target.DecimalPlaces);
                inline.AttachedNumericMetadata = transferred;
                inline.Range = new(transferred, inline.Type, transferred.DecimalPlaces != null);
                inline.UsesRangedNumericTemplate = transferred.Minimum != null && transferred.Maximum != null
                    && transferred.SmallStep != null && transferred.LargeStep != null;
            }
        }
        ApplyCompositorNativeMetadata(property);
    }
    private CompositorProperty CreateCompositorProperty(NodeAccessor accessor, string key, string label)
    {
        var property = new CompositorProperty(accessor, key, label);
        if (accessor.Node is IMemberNode described)
        {
            var display = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(described.MemberDescriptor.MemberInfo);
            property.Category = display?.Category ?? ""; property.Order = display?.Order ?? described.MemberDescriptor.Order ?? 0;
            property.CategoryMetadata = TypeDescriptorFactory.Default.AttributeRegistry.GetAttributes<CategoryOrderAttribute>(described.MemberDescriptor.DeclaringType)
                .FirstOrDefault(category => category.Name == property.Category);
            property.IsNavigationGroup = described.Parent.Type == typeof(Stride.Navigation.NavigationComponent) && described.Name == nameof(Stride.Navigation.NavigationComponent.GroupId);
        }
        property.CanBeNull = !property.DeclaredType.IsValueType || property.IsNullable;
        if (accessor.Node is IMemberNode nullMember && (TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(nullMember.MemberDescriptor.MemberInfo) != null
            || TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(nullMember.MemberDescriptor.MemberInfo) != null)) property.CanBeNull = false;
        var contentTypes = AssetRegistry.CanPropertyHandleContent(property.Type, out var registeredContent) ? registeredContent : [];
        property.Types = (property.Type.IsInterface || property.Type.IsAbstract)
            ? property.Type.GetInheritedInstantiableTypes().Where(type => ObjectFactoryRegistry.CanCreateInstance(type)
                && !IsExistingCompositorReferenceType(type) && !contentTypes.Contains(type)
                && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NonInstantiableAttribute>(type) == null
                && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<ObsoleteAttribute>(type) == null)
                .OrderBy(DisplayAttribute.GetDisplayName, StringComparer.Ordinal).ToArray() : [];
        property.EnumNames = property.Type.IsEnum ? Enum.GetNames(property.Type) : [];
        property.EnumLabels = property.EnumNames.Select(name => TypeDescriptorFactory.Default.AttributeRegistry
            .GetAttribute<DisplayAttribute>(property.Type.GetField(name)!)?.Name ?? name).ToArray();
        if (accessor.Node is IMemberNode member)
        {
            property.Range = AssetSettingNumericMetadata.Read(member);
            var declaredRange = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(member.MemberDescriptor.MemberInfo);
            var numeric = AssetSettingNumericMetadata.Supports(member.Type);
            var integral = numeric && member.Type != typeof(float) && member.Type != typeof(double) && member.Type != typeof(decimal);
            var byteSized = member.Type == typeof(byte) || member.Type == typeof(sbyte);
            property.AttachedNumericMetadata = new(declaredRange?.Minimum ?? (numeric ? Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMinimum(member.Type)) : null),
                declaredRange?.Maximum ?? (numeric ? Convert.ToDouble(Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters.NumericValueNodeUpdater.GetMaximum(member.Type)) : null),
                declaredRange?.SmallStep ?? (byteSized ? 1 : null), declaredRange?.LargeStep ?? (byteSized ? 5 : null),
                declaredRange?.DecimalPlaces ?? (integral ? 0 : null));
            property.UsesRangedNumericTemplate = !property.IsNullable && AssetSettingNumericMetadata.Supports(property.Type)
                && (property.Type == typeof(byte) || property.Type == typeof(sbyte)
                    || declaredRange?.SmallStep != null && declaredRange.LargeStep != null);
            if (property.Range == null && property.IsNullable && AssetSettingNumericMetadata.Supports(property.Type)
                && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DataMemberRangeAttribute>(member.MemberDescriptor.MemberInfo) is { } nullableRange)
                property.Range = new(nullableRange, property.Type);
        }
        property.VectorRanges = accessor.Node is IMemberNode vector ? VectorPropertyNumericMetadata.Create(vector) : VectorPropertyNumericMetadata.Create(property.Type);
        if (accessor.Node is IMemberNode quality && quality.Name == nameof(FXAAEffect.Quality) && quality.Parent.Retrieve() is FXAAEffect fxaa)
        {
            var (minimum, maximum) = FXAAEffect.GetQualityRange(fxaa.Dither);
            property.Range = new(new DataMemberRangeAttribute(minimum, maximum, 1, 1, 0), property.Type);
            property.AttachedNumericMetadata = new(minimum, maximum, 1, 1, 0);
            property.UsesRangedNumericTemplate = true;
        }
        ApplyCompositorNativeMetadata(property);
        compositorPropertyMembers.Add(property); return property;
    }
    internal static bool IsCompositorReferenceType(Type type, object? value) => value is Stride.Rendering.Compositing.ISharedRenderer
        || value == null && typeof(Stride.Rendering.IGraphicsRendererBase).IsAssignableFrom(type) || type == typeof(Stride.Rendering.RenderStage)
        || type == typeof(Stride.Rendering.Compositing.SceneCameraSlot) || type == typeof(Stride.Rendering.Compositing.SceneCameraSlotId);
    internal static bool IsCompositorValueType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
        || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
        || typeof(Stride.Core.IO.UPath).IsAssignableFrom(type)
        || type == typeof(Stride.Core.Mathematics.Vector2) || type == typeof(Stride.Core.Mathematics.Vector3) || type == typeof(Stride.Core.Mathematics.Vector4)
        || type == typeof(Stride.Core.Mathematics.Int2) || type == typeof(Stride.Core.Mathematics.Int3) || type == typeof(Stride.Core.Mathematics.Int4)
        || type == typeof(Stride.Core.Mathematics.UInt4)
        || type == typeof(Stride.Core.Mathematics.Color) || type == typeof(Stride.Core.Mathematics.Color3) || type == typeof(Stride.Core.Mathematics.Color4)
        || type == typeof(Stride.Core.Mathematics.Rectangle) || type == typeof(Stride.Core.Mathematics.RectangleF)
        || type == typeof(Stride.Core.Mathematics.Quaternion) || type == typeof(Stride.Core.Mathematics.Matrix) || type == typeof(Stride.Core.Mathematics.AngleSingle);
    }
    private static bool IsExistingCompositorReferenceType(Type type) => typeof(Stride.Rendering.Compositing.ISharedRenderer).IsAssignableFrom(type)
        || typeof(Stride.Rendering.RenderStage).IsAssignableFrom(type) || typeof(Stride.Rendering.Compositing.SceneCameraSlot).IsAssignableFrom(type);
    public object? CompositorPropertyValue(CompositorProperty property) => ReferenceEquals(activeCompositorProperty, property) ? compositorPropertyPending : property.Accessor.RetrieveValue();
    public bool CanEditCompositorProperty(CompositorProperty property)
        => CanEditCompositor && compositorPropertyMembers.Contains(property) && IsCompositorNativePresenterCurrent(property)
            && !CompositorPresenterReadOnly(property) && (property.Accessor.IsItem
            ? property.Accessor.Node is IAssetObjectNode collection && collection.HasId(property.ItemId)
                && collection.IdToIndex(property.ItemId) == property.Accessor.Index && CanEditAssetSettingItem(collection, property.Accessor.Index)
            : property.Accessor.Node is IMemberNode member && CanEditAssetSettingNode(member) && CanAssignAuthoringNode(member));
    public void UpdateCompositorProperty(CompositorProperty property, object? value)
    {
        var normalized = NormalizeCompositorProperty(property, value);
        if (!ReferenceEquals(activeCompositorProperty, property))
        {
            CommitEdits(); activeCompositorProperty = property;
            compositorPropertyStart = compositorPropertyPending = property.Accessor.RetrieveValue();
        }
        compositorPropertyPending = normalized;
    }
    private object? NormalizeCompositorProperty(CompositorProperty property, object? value)
    {
        if (!CanEditCompositorProperty(property) || !property.Accessor.AcceptValue(value) || value == null && !property.AllowsNull) throw new InvalidOperationException("This compositor property cannot be changed.");
        if (CompositorPresenterReadOnly(property)) throw new InvalidOperationException("This compositor property is read-only.");
        AssertCompositorAxesEditable(property, property.Accessor.RetrieveValue(), value);
        if ((value is Stride.Rendering.Compositing.ISharedRenderer or Stride.Rendering.RenderStage or Stride.Rendering.Compositing.SceneCameraSlot)
            && !CompositorPropertyReferenceItems(property).Any(item => ReferenceEquals(item, value)))
            throw new InvalidOperationException("The selected reference is not part of this compositor.");
        if (value is Stride.Rendering.Compositing.SceneCameraSlotId cameraId && !cameraId.IsEmpty && !GetCompositorCameraSlotChoices().Any(camera => camera.Value.Id == cameraId.Id))
            throw new InvalidOperationException("The selected camera slot is not part of this package.");
        var normalized = PropertyValueValidation.Normalize(value, property.Range);
        if (normalized is Stride.Core.Mathematics.AngleSingle angle && !float.IsFinite(angle.Radians))
            throw new ArgumentException("Numeric property values must be finite.");
        normalized = NativeNumericAxes.Normalize(normalized, property.Accessor.RetrieveValue(), CompositorAxisRanges(property));
        normalized = MatrixPropertyValue.Normalize(normalized, property.Accessor.RetrieveValue());
        if (normalized is Stride.Core.Mathematics.RectangleF rectangle
            && (!float.IsFinite(rectangle.X) || !float.IsFinite(rectangle.Y) || !float.IsFinite(rectangle.Width) || !float.IsFinite(rectangle.Height)))
            throw new ArgumentException("Numeric property values must be finite.");
        return normalized;
    }
    internal void EndCompositorSettingEdit()
    {
        if (pendingCompositorPositions.Count > 0)
        {
            var positions = new Dictionary<Guid, Stride.Core.Mathematics.Vector2>(pendingCompositorPositions);
            pendingCompositorPositions.Clear(); SetCompositorBlockPositions(positions);
        }
        if (activeCompositorProperty == null) return;
        var property = activeCompositorProperty; var before = compositorPropertyStart; var after = compositorPropertyPending;
        activeCompositorProperty = null; compositorPropertyStart = compositorPropertyPending = null;
        if (Equals(before, after)) return;
        if (!CanEditCompositorProperty(property)) throw new InvalidOperationException("The compositor property no longer exists or is read-only.");
        IMemberNode? qualityNode = null; var normalizedQuality = 0;
        if (property.Accessor.Node is IMemberNode dither && dither.Name == nameof(FXAAEffect.Dither) && dither.Parent.Retrieve() is FXAAEffect
            && after is FXAAEffect.DitherType mode)
        {
            var (minimum, maximum) = FXAAEffect.GetQualityRange(mode);
            var quality = dither.Parent[nameof(FXAAEffect.Quality)];
            normalizedQuality = Math.Clamp((int)quality.Retrieve(), minimum, maximum);
            if (!Equals(normalizedQuality, quality.Retrieve()))
            {
                if (!CanEditAssetSettingNode(quality) || !CanAssignAuthoringNode(quality)) throw new InvalidOperationException("The FXAA quality required by this dither mode is read-only.");
                qualityNode = quality;
            }
        }
        ApplyInheritedAssetMutation(() =>
        {
            property.Accessor.UpdateValue(after);
            qualityNode?.Update(normalizedQuality);
        }, "Edit " + property.Label);
    }
    public Stride.Core.Mathematics.Vector2 CompositorBlockPosition(Guid id, Stride.Core.Mathematics.Vector2 fallback)
        => pendingCompositorPositions.GetValueOrDefault(id, CompositorAsset!.BlockPositions.GetValueOrDefault(id, fallback));
    public void StageCompositorBlockPosition(Guid id, Stride.Core.Mathematics.Vector2 position)
    {
        if (!CanMoveCompositorBlock(id))
            throw new InvalidOperationException("This block position is read-only.");
        pendingCompositorPositions[id] = position;
    }
    public void CreateCompositorPropertyObject(CompositorProperty property, Type? type)
    {
        if (type != null && (IsExistingCompositorReferenceType(type) || AssetRegistry.CanPropertyHandleContent(property.Type, out var contentTypes) && contentTypes.Contains(type)
            || !property.Types.Contains(type) && type != property.Type)) throw new InvalidOperationException("The compositor property type cannot be created.");
        var value = type == null ? null : ObjectFactoryRegistry.NewInstance(type);
        AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        UpdateCompositorProperty(property, value); EndCompositorSettingEdit();
    }
    public void AddCompositorPropertyItem(CompositorProperty property, Type type, string? key = null)
    {
        var collection = property.Collection;
        if (!CanAddCompositorPropertyItems(property) || collection == null
            || !property.ItemTypes.Contains(type)) throw new InvalidOperationException("This collection item cannot be created.");
        var value = ObjectFactoryRegistry.NewInstance(type); AssetCollectionItemIdHelper.GenerateMissingItemIds(value);
        AddCompositorPropertyCollectionValue(property, value, key);
    }
    private bool CanMutateCompositorCollection(CompositorProperty property) => CanEditCompositor && compositorPropertyMembers.Contains(property)
        && IsCompositorNativePresenterCurrent(property) && !CompositorPresenterReadOnly(property)
        && property.Collection is { } collection && CanEditAssetSettingNode(collection) && !collection.Type.IsArray && property.Rules?.ReadOnly != true
        && ReferenceEquals(property.Accessor.RetrieveValue(), collection.Retrieve());
    public bool CanAddCompositorPropertyItems(CompositorProperty property) => CanMutateCompositorCollection(property)
        && (property.Collection!.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.CanAdd(dictionary.KeyType) && !dictionary.IsReadOnly(property.Collection.Retrieve())
            || property.Collection.Descriptor is CollectionDescriptor { HasAdd: true } list && !list.IsReadOnly(property.Collection.Retrieve()));
    public bool CanRemoveCompositorPropertyItems(CompositorProperty property) => CanMutateCompositorCollection(property)
        && (property.Collection!.Descriptor is DictionaryDescriptor dictionary && !dictionary.IsReadOnly(property.Collection.Retrieve())
            || property.Collection.Descriptor is CollectionDescriptor list && (list.HasRemoveAt || list.HasRemove) && !list.IsReadOnly(property.Collection.Retrieve()));
    public bool CanResizeCompositorPropertyCollection(CompositorProperty property) => CanAddCompositorPropertyItems(property) && CanRemoveCompositorPropertyItems(property);
    public IEnumerable<object> CompositorPropertyReferenceItems(CompositorProperty property)
    {
        var type = property.Collection?.Descriptor.GetInnerCollectionType() ?? property.Type;
        if (type == typeof(SceneCameraSlotId)) return GetCompositorCameraSlotChoices().Select(choice => (object)choice.Value).Prepend(default(SceneCameraSlotId));
        return CompositorAsset!.SharedRenderers.Cast<object>().Concat(CompositorAsset.RenderStages).Concat(CompositorAsset.Cameras).Where(type.IsInstanceOfType);
    }
    public void AddCompositorPropertyReference(CompositorProperty property, object? value, string? key = null)
    {
        if (!CanAddCompositorPropertyItems(property) || value == null && property.Rules?.NotNullItems == true)
            throw new InvalidOperationException("This compositor reference cannot be added.");
        ValidateCompositorCollectionReference(property, value);
        AddCompositorPropertyCollectionValue(property, value, key);
    }
    private void ValidateCompositorCollectionReference(CompositorProperty property, object? value)
    {
        var type = property.Collection!.Descriptor.GetInnerCollectionType();
        if (value == null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new ArgumentException("This collection item cannot contain null.");
        }
        else if (!CompositorPropertyReferenceItems(property).Any(item => type.IsValueType ? Equals(item, value) : ReferenceEquals(item, value)))
        {
            if (!ContentAssetReference.Supports(type)) throw new ArgumentException("The reference is not part of this compositor.");
            ContentAssetReference.Validate(type, value, false);
        }
    }
    private void AddCompositorPropertyCollectionValue(CompositorProperty property, object? value, string? key)
    {
        var collection = property.Collection!;
        var index = collection.Descriptor is DictionaryDescriptor dictionary ? new NodeIndex(NativeDictionaryKey.CreateForAdd(dictionary.KeyType, key))
            : collection.Descriptor is SetDescriptor ? new NodeIndex(value) : new NodeIndex((collection.Indices ?? []).Count());
        if (collection.Descriptor is DictionaryDescriptor map && map.ContainsKey(collection.Retrieve(), index.Value)) throw new ArgumentException("The dictionary already contains that key.");
        ApplyInheritedAssetMutation(() =>
        {
            if (collection.Descriptor is DictionaryDescriptor) collection.Add(value!, index);
            else collection.Add(value!); // Quantum determines the true list/set index.
        }, "Add " + property.Label);
    }
    public void RemoveCompositorPropertyItem(CompositorProperty item)
    {
        if (!CanEditCompositorProperty(item) || item.Accessor.Node is not IAssetObjectNode collection || item.ParentCollection == null || !CanRemoveCompositorPropertyItems(item.ParentCollection)) throw new InvalidOperationException("This compositor item cannot be removed.");
        var value = item.Accessor.RetrieveValue();
        ApplyInheritedAssetMutation(() => collection.Remove(value, item.Accessor.Index), "Remove " + item.Label);
    }
    public void MoveCompositorPropertyItem(CompositorProperty item, int destination)
    {
        if (!CanEditCompositorProperty(item) || item.ParentCollection?.Rules?.CanReorderItems != true || !CanRemoveCompositorPropertyItems(item.ParentCollection)
            || item.Accessor.Node is not IAssetObjectNode collection || !item.Accessor.Index.IsInt || collection.Descriptor is not CollectionDescriptor { HasInsert: true })
            throw new InvalidOperationException("This compositor collection cannot be reordered.");
        if (destination < 0 || destination >= collection.Indices!.Count()) throw new ArgumentOutOfRangeException(nameof(destination));
        var index = collection.IdToIndex(item.ItemId); if (index.Int == destination) return;
        var value = collection.Retrieve(index);
        ApplyInheritedAssetMutation(() =>
        {
            var overridden = collection.IsItemOverridden(index); collection.RemoveAndDiscard(value, index, item.ItemId);
            var next = new NodeIndex(destination); collection.Restore(value, next, item.ItemId); collection.OverrideItem(overridden, next);
        }, "Move " + item.Label);
    }
    public void RenameCompositorDictionaryKey(CompositorProperty item, string text)
    {
        CommitEdits();
        if (!CanRenameCompositorDictionaryKey(item) || item.Accessor.Node is not IAssetObjectNode collection || collection.Descriptor is not DictionaryDescriptor dictionary)
            throw new InvalidOperationException("This compositor dictionary key cannot be edited.");
        var key = ParseImportDictionaryKey(dictionary.KeyType, text); if (Equals(key, item.Accessor.Index.Value)) return;
        if (dictionary.ContainsKey(collection.Retrieve(), key)) return;
        ApplyInheritedAssetMutation(() =>
        {
            var index = collection.IdToIndex(item.ItemId); var value = collection.Retrieve(index);
            var itemOverride = collection.IsItemOverridden(index); var keyOverride = collection.IsKeyOverridden(index);
            collection.RemoveAndDiscard(value, index, item.ItemId); var next = new NodeIndex(key); collection.Restore(value, next, item.ItemId);
            collection.OverrideItem(itemOverride, next); collection.OverrideKey(keyOverride, next);
        }, "Rename key");
    }
    public bool CanRenameCompositorDictionaryKey(CompositorProperty item)
        => CanEditCompositorProperty(item) && item.ParentCollection != null && CanMutateCompositorCollection(item.ParentCollection)
            && item.Accessor.Node.Descriptor is DictionaryDescriptor dictionary && !dictionary.IsReadOnly(item.Accessor.Node.Retrieve())
            && NativeDictionaryKey.SupportsText(dictionary.KeyType);
}
