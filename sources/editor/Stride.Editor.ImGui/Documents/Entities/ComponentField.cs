using Stride.Core;
using Stride.Core.Quantum;
using Stride.Core.Transactions;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Extensions;

namespace Stride.Editor.ImGui;

internal sealed record ComponentField(EntityComponent Component, IMemberNode Node, string Label)
{
    private static Type ValueType(Type declared) => Nullable.GetUnderlyingType(declared) ?? declared;
    public DisplayAttribute? DisplayMetadata { get; } = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(Node.MemberDescriptor.MemberInfo);
    public bool IsCategoryHeader { get; } = TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.ComponentModel.CategoryAttribute>(Node.MemberDescriptor.MemberInfo) != null;
    public int? DisplayOrder => DisplayMetadata?.Order ?? Node.MemberDescriptor.Order;
    private readonly IReadOnlyList<PropertyNumericRange?> axisNumericRanges = VectorPropertyNumericMetadata.Create(Node);
    public IReadOnlyList<PropertyNumericRange?> AxisNumericRanges => Presentation?.MemberAxisNumericRanges(this, axisNumericRanges) ?? axisNumericRanges;
    public PropertyNumericRange? NumericRange { get; } = (ValueType(Node.Type) == typeof(float) || ValueType(Node.Type) == typeof(double) || ValueType(Node.Type) == typeof(decimal) || ValueType(Node.Type) == typeof(int)
        || ValueType(Node.Type) == typeof(byte) || ValueType(Node.Type) == typeof(sbyte) || ValueType(Node.Type) == typeof(short) || ValueType(Node.Type) == typeof(ushort)
        || ValueType(Node.Type) == typeof(uint) || ValueType(Node.Type) == typeof(long) || ValueType(Node.Type) == typeof(ulong))
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<Stride.Core.Annotations.DataMemberRangeAttribute>(Node.MemberDescriptor.MemberInfo) is { } range
        ? new PropertyNumericRange(range, ValueType(Node.Type)) : null;
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = ValueType(Node.Type).IsEnum
        ? GetEnumOptionNames(ValueType(Node.Type)).Select(name =>
        {
            var value = Enum.Parse(ValueType(Node.Type), name);
            var display = ValueType(Node.Type).GetField(name)!.GetCustomAttributes(typeof(DisplayAttribute), false).Cast<DisplayAttribute>().FirstOrDefault()?.Name;
            return new ComponentEnumOption(value, name, string.IsNullOrEmpty(display) || display == name ? name : name + " · " + display);
        }).ToArray() : [];
    internal static IEnumerable<string> GetEnumOptionNames(Type type)
        => type.IsDefined(typeof(FlagsAttribute), false)
            ? EditorFlagEnums.GetIndividualFlags(type).Select(value => Enum.GetName(type, value)!)
            : Enum.GetNames(type);
    public IReadOnlyList<IMemberDescriptor> Path { get; init; } = [Node.MemberDescriptor];
    public MemberPath? RuntimePath { get; init; }
    public IReadOnlyList<ComponentField> Children { get; init; } = [];
    internal ComponentNodePresentation? Presentation { get; init; }
    public bool IsVisible => Presentation?.IsVisible(this) != false;
    public PropertyNumericRange? EffectiveNumericRange => Presentation?.NumericRange(this) ?? NumericRange;
    public bool IsAxisReadOnly(int axis) => IsNullable && (Type == typeof(Rectangle) || Type == typeof(RectangleF)) && Node.Retrieve() == null
        || Presentation?.IsMemberAxisReadOnly(this, axis) == true;
    public ExpandRule Expansion => Presentation?.Expansion(this) ?? DisplayMetadata?.Expand ?? ExpandRule.Auto;
    public string Name { get; init; } = Node.Name;
    public Type Type { get; } = ValueType(Node.Type);
    public Type DeclaredType => Node.Type;
    public bool IsNullable => Nullable.GetUnderlyingType(Node.Type) != null;
    public System.ComponentModel.DefaultValueAttribute? DefaultValue { get; } =
        TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.ComponentModel.DefaultValueAttribute>(Node.MemberDescriptor.MemberInfo);
    public bool IsNavigationGroup => Component.GetType() == typeof(Stride.Navigation.NavigationComponent) && Node.Name == nameof(Stride.Navigation.NavigationComponent.GroupId) && Type == typeof(Guid);
    public bool IsSceneReference => SceneObjectReference.Supports(Type);
    public bool IsContentReference { get; } = ContentAssetReference.Supports(ValueType(Node.Type));
    public ITypeDescriptor CollectionDescriptor { get; } = TypeDescriptorFactory.Default.Find(Node.Type);
    public bool IsIndexedCollection => CollectionDescriptor is ArrayDescriptor or SetDescriptor
        || CollectionDescriptor is CollectionDescriptor { Category: DescriptorCategory.List, HasIndexerAccessors: true };
    public bool IsDictionary => CollectionDescriptor is DictionaryDescriptor;
    public Type? CollectionElementType => CollectionDescriptor switch
    {
        ArrayDescriptor array => array.ElementType,
        CollectionDescriptor collection => collection.ElementType,
        DictionaryDescriptor dictionary => dictionary.ValueType,
        _ => null,
    };
    public MemberCollectionAttribute? CollectionRules { get; } = TypeDescriptorFactory.Default.AttributeRegistry
        .GetAttribute<MemberCollectionAttribute>(Node.MemberDescriptor.MemberInfo)
        ?? TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<MemberCollectionAttribute>(Node.Type);
    public bool IsObjectProperty => !Type.IsValueType && Type != typeof(string) && !typeof(Stride.Core.IO.UPath).IsAssignableFrom(Type) && !IsSceneReference && !IsContentReference
        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(Type) && !AssetRegistry.IsExactContentType(Type)
        && (Type == typeof(object) || Type.IsInterface || Type.IsAbstract || Type.IsDefined(typeof(DataContractAttribute), true))
        && Node.Retrieve() is not Entity and not EntityComponent
        && AttachedReferenceManager.GetAttachedReference(Node.Retrieve()) == null;
    public bool AllowsNullObject => TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public bool SupportsObjectLifecycle => IsObjectProperty && Node.MemberDescriptor.HasSet && Presentation?.IsReadOnly(this) != true;
    public IReadOnlyList<Type> ObjectTypes { get; } = !SceneObjectReference.Supports(ValueType(Node.Type)) && !ValueType(Node.Type).IsValueType && Node.Type != typeof(string)
        && !typeof(System.Collections.IEnumerable).IsAssignableFrom(Node.Type)
        ? Node.Type.GetInheritedInstantiableTypes().Where(type => Attribute.GetCustomAttribute(type, typeof(NonInstantiableAttribute)) == null
            && !typeof(Entity).IsAssignableFrom(type) && !typeof(EntityComponent).IsAssignableFrom(type)
            && !AssetRegistry.IsExactContentType(type) && ObjectFactoryRegistry.CanCreateInstance(type))
            .OrderBy(type => DisplayAttribute.GetDisplayName(type) ?? type.Name).ToArray() : [];
    public bool IsWideInteger => Type == typeof(byte) || Type == typeof(sbyte) || Type == typeof(short)
        || Type == typeof(ushort) || Type == typeof(uint) || Type == typeof(long) || Type == typeof(ulong);
    public bool IsColor => Type == typeof(Color) || Type == typeof(Color3) || Type == typeof(Color4);
    public PropertyNumericRange? AxisNumericRange(int axis) => IsColor
        ? Children.FirstOrDefault(child => child.Node.Name == "RGBA"[axis].ToString())?.EffectiveNumericRange ?? AxisNumericRanges[axis]
        : AxisNumericRanges[axis];
    public bool IsIntegerVector => Type == typeof(Int2) || Type == typeof(Int3) || Type == typeof(Int4);
    public int VectorDimensions => Type == typeof(Vector2) || Type == typeof(Int2) ? 2
        : Type == typeof(Vector3) || Type == typeof(Int3) ? 3 : Type == typeof(Vector4) || Type == typeof(Int4) ? 4 : 0;
    public bool SupportsEditing => Node.MemberDescriptor.HasSet && Presentation?.IsReadOnly(this) != true && SupportsValueEditing;
    public bool SupportsValueEditing => Type == typeof(bool) || Type == typeof(float) || Type == typeof(char) || Type == typeof(AngleSingle) || Type == typeof(Guid) || Type == typeof(TimeSpan) || Type == typeof(DateTime)
        || Type == typeof(double) || Type == typeof(decimal) || Type == typeof(int) || Type == typeof(string)
        || Type == typeof(Stride.Core.IO.UFile) || Type == typeof(Stride.Core.IO.UDirectory)
        || Type == typeof(Color) || Type == typeof(Color3) || Type == typeof(Color4)
        || Type == typeof(Vector2) || Type == typeof(Vector3) || Type == typeof(Vector4) || Type == typeof(Quaternion)
        || Type == typeof(Int2) || Type == typeof(Int3) || Type == typeof(Int4) || Type == typeof(UInt4) || Type == typeof(Matrix) || Type == typeof(Rectangle) || Type == typeof(RectangleF)
        || Type == typeof(byte) || Type == typeof(sbyte) || Type == typeof(short) || Type == typeof(ushort)
        || Type == typeof(uint) || Type == typeof(long) || Type == typeof(ulong)
        || Type.IsEnum || IsSceneReference || IsContentReference || IsNavigationGroup
        || Type == typeof(Stride.Rendering.Compositing.SceneCameraSlotId) || Presentation?.HasPrimitiveEditor(this) == true;

    public object? ReadRuntime(EntityComponent component)
    {
        if (RuntimePath != null) return RuntimePath.GetValue(component);
        object? value = component;
        foreach (var member in Path) { if (value == null) return null; value = member.Get(value); }
        return value;
    }
    public object? ReadRuntimeOwner(EntityComponent component)
    {
        if (RuntimePath != null)
        {
            var parent = RuntimePath.Clone(); parent.Pop();
            return parent.GetValue(component);
        }
        object? value = component;
        for (var index = 0; index < Path.Count - 1; index++)
        { if (value == null) return null; value = Path[index].Get(value); }
        return value;
    }
    public void WriteRuntime(EntityComponent component, object? value)
    {
        if (RuntimePath != null)
        {
            if (!RuntimePath.Apply(component, MemberPathAction.ValueSet, value)) throw new InvalidOperationException("Unable to update the runtime property.");
        }
        else WriteMember(component, 0, value);
    }
    public object? NormalizeValue(object? value)
    {
        Presentation?.AssertMemberAxesEditable(this, Node.Retrieve(), value);
        value = PropertyValueValidation.Normalize(value, EffectiveNumericRange);
        value = MatrixPropertyValue.Normalize(value, Node.Retrieve());
        return NativeNumericAxes.Normalize(value, Node.Retrieve(), IsColor
            ? Enumerable.Range(0, AxisNumericRanges.Count).Select(AxisNumericRange).ToArray() : AxisNumericRanges);
    }
    private void WriteMember(object owner, int index, object? value)
    {
        var member = Path[index];
        if (index == Path.Count - 1) { member.Set(owner, value); return; }
        var child = member.Get(owner);
        if (child == null) return;
        WriteMember(child, index + 1, value);
        // Reflection returns a boxed copy of a struct; propagate it back through every owner.
        if (child.GetType().IsValueType) member.Set(owner, child);
    }
}
internal sealed record ComponentEnumOption(object Value, string Name, string Label);
