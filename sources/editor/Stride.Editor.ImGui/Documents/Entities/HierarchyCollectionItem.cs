using Stride.Core;
using Stride.Core.Assets.Quantum;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed record HierarchyCollectionItem(HierarchyCollectionContext Context, NodeIndex OriginalIndex, ItemId? Id) : IHierarchyCollectionValue
{
    public ComponentField Field => Context.Origin;
    public IAssetObjectNode Collection => Context.Node;
    public NodeIndex Index => Id is { } id ? Collection.IdToIndex(id) : OriginalIndex;
    public object? Value => Collection.Retrieve(Index);
    public Type DeclaredType => Context.ElementType!;
    public Type Type => Nullable.GetUnderlyingType(DeclaredType) ?? DeclaredType;
    public bool AllowsNull => Context.Rules?.NotNullItems != true && (!DeclaredType.IsValueType || Nullable.GetUnderlyingType(DeclaredType) != null);
    public string IdentityKey => Context.Key + "/" + (Id?.ToString() ?? OriginalIndex.ToString());
    public bool IsSceneReference => SceneObjectReference.Supports(Type);
    public bool IsContentReference => ContentAssetReference.Supports(Type);
    public bool SupportsSimpleEditing => Type == typeof(string) || Type == typeof(bool) || Type == typeof(char) || Type == typeof(AngleSingle)
        || Type == typeof(Guid) || Type == typeof(TimeSpan) || Type == typeof(DateTime) || Type.IsEnum || Type.IsPrimitive && Type != typeof(IntPtr) && Type != typeof(UIntPtr)
        || Type == typeof(Stride.Core.IO.UFile) || Type == typeof(Stride.Core.IO.UDirectory)
        || Type == typeof(decimal) || Type == typeof(Stride.Core.Mathematics.Vector2) || Type == typeof(Stride.Core.Mathematics.Vector3)
        || Type == typeof(Stride.Core.Mathematics.Vector4) || Type == typeof(Stride.Core.Mathematics.Int2) || Type == typeof(Stride.Core.Mathematics.Int3)
        || Type == typeof(Stride.Core.Mathematics.Int4) || Type == typeof(Stride.Core.Mathematics.UInt4) || Type == typeof(Stride.Core.Mathematics.Matrix) || Type == typeof(Rectangle) || Type == typeof(RectangleF)
        || Type == typeof(Stride.Core.Mathematics.Quaternion) || Type == typeof(Stride.Core.Mathematics.Color) || Type == typeof(Stride.Core.Mathematics.Color3) || Type == typeof(Stride.Core.Mathematics.Color4)
        || IsSceneReference || IsContentReference;
    private readonly IReadOnlyList<PropertyNumericRange?> axisNumericRanges = VectorPropertyNumericMetadata.Create(Nullable.GetUnderlyingType(Context.ElementType!) ?? Context.ElementType!);
    public IReadOnlyList<PropertyNumericRange?> AxisNumericRanges => Field.Presentation?.ItemAxisNumericRanges(Collection, Index, Type, axisNumericRanges) ?? axisNumericRanges;
    public IReadOnlyList<ComponentEnumOption> EnumOptions { get; } = (Nullable.GetUnderlyingType(Context.ElementType!) ?? Context.ElementType!).IsEnum
        ? ComponentField.GetEnumOptionNames(Nullable.GetUnderlyingType(Context.ElementType!) ?? Context.ElementType!).Select(name =>
        {
            var type = Nullable.GetUnderlyingType(Context.ElementType!) ?? Context.ElementType!;
            var display = type.GetField(name)!.GetCustomAttributes(typeof(DisplayAttribute), false).Cast<DisplayAttribute>().FirstOrDefault()?.Name;
            return new ComponentEnumOption(Enum.Parse(type, name), name, display ?? name);
        }).ToArray() : [];
    internal MemberPath RuntimePath()
    {
        var path = Context.Parent?.RuntimePath() ?? Field.RuntimePath?.Clone() ?? new MemberPath();
        if (Context.Parent == null && Field.RuntimePath == null) foreach (var member in Field.Path) path.Push(member);
        path.Push(Context.Descriptor, Index.Value!);
        return path;
    }
}
