using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

internal sealed record CollectionItem(ComponentField Field, IAssetObjectNode Collection, ItemId Id) : IHierarchyCollectionValue
{
    private readonly IReadOnlyList<PropertyNumericRange?> axisNumericRanges = VectorPropertyNumericMetadata.Create(Nullable.GetUnderlyingType(Field.CollectionElementType!) ?? Field.CollectionElementType!);
    public IReadOnlyList<PropertyNumericRange?> AxisNumericRanges => Field.Presentation?.ItemAxisNumericRanges(Collection, Index, Type, axisNumericRanges) ?? axisNumericRanges;
    public NodeIndex Index => Collection.IdToIndex(Id);
    public object? Value => Collection.Retrieve(Index);
    public Type DeclaredType => Field.CollectionElementType!;
    public Type Type => Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
    public bool AllowsNull => (!DeclaredType.IsValueType || Nullable.GetUnderlyingType(DeclaredType) != null) && Field.CollectionRules?.NotNullItems != true;
    public bool IsSceneReference => SceneObjectReference.Supports(Type);
    public bool IsContentReference { get; } = ContentAssetReference.Supports(Nullable.GetUnderlyingType(Field.CollectionElementType!) ?? Field.CollectionElementType!);
    public bool SupportsSimpleEditing => Type == typeof(string) || Type == typeof(bool) || Type == typeof(char) || Type == typeof(AngleSingle)
        || Type == typeof(Guid) || Type == typeof(TimeSpan) || Type == typeof(DateTime) || Type.IsEnum
        || Type == typeof(Stride.Core.IO.UFile) || Type == typeof(Stride.Core.IO.UDirectory)
        || Type == typeof(float) || Type == typeof(double) || Type == typeof(decimal)
        || Type == typeof(byte) || Type == typeof(sbyte) || Type == typeof(short) || Type == typeof(ushort)
        || Type == typeof(int) || Type == typeof(uint) || Type == typeof(long) || Type == typeof(ulong)
        || Type == typeof(Vector2) || Type == typeof(Vector3) || Type == typeof(Vector4)
        || Type == typeof(Int2) || Type == typeof(Int3) || Type == typeof(Int4) || Type == typeof(UInt4) || Type == typeof(Matrix) || Type == typeof(Rectangle) || Type == typeof(RectangleF)
        || Type == typeof(Quaternion) || Type == typeof(Color) || Type == typeof(Color3) || Type == typeof(Color4) || IsSceneReference || IsContentReference;
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = (Nullable.GetUnderlyingType(Field.CollectionElementType!) ?? Field.CollectionElementType!).IsEnum
        ? ComponentField.GetEnumOptionNames(Nullable.GetUnderlyingType(Field.CollectionElementType!) ?? Field.CollectionElementType!).Select(name =>
        {
            var type = Nullable.GetUnderlyingType(Field.CollectionElementType!) ?? Field.CollectionElementType!;
            var display = type.GetField(name)!.GetCustomAttributes(typeof(DisplayAttribute), false).Cast<DisplayAttribute>().FirstOrDefault()?.Name;
            return new ComponentEnumOption(Enum.Parse(type, name), name, display ?? name);
        }).ToArray() : [];

    public string IdentityKey => Id.ToString();

    internal MemberPath RuntimePath()
    {
        var path = Field.RuntimePath?.Clone() ?? new MemberPath();
        if (Field.RuntimePath == null) foreach (var member in Field.Path) path.Push(member);
        path.Push(Field.CollectionDescriptor, Index.Value!);
        return path;
    }
}
