using System.Security.Cryptography;
using Stride.Assets.Models;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Core.Yaml;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

internal sealed record AssetPropertyField(IMemberNode Node, string Label, string? Path = null)
{
    private static Type ValueType(Type type) => Nullable.GetUnderlyingType(type) ?? type;
    public string Name => Path ?? Node.Name;
    public Type Type => ValueType(Node.Type);
    public bool AllowsNull => (Nullable.GetUnderlyingType(Node.Type) != null || !Type.IsValueType)
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<System.Diagnostics.CodeAnalysis.NotNullAttribute>(Node.MemberDescriptor.MemberInfo) == null;
    public string[] EnumNames { get; } = ValueType(Node.Type).IsEnum ? Enum.GetNames(ValueType(Node.Type)) : [];
    public bool SupportsEditing { get; } = Node.MemberDescriptor.HasSet && (AssetSettingNumericMetadata.Supports(ValueType(Node.Type)) || ValueType(Node.Type) == typeof(float)
        || ValueType(Node.Type) == typeof(double) || ValueType(Node.Type) == typeof(int) || ValueType(Node.Type) == typeof(bool) || ValueType(Node.Type) == typeof(string)
        || ValueType(Node.Type) == typeof(char) || ValueType(Node.Type) == typeof(Guid) || ValueType(Node.Type) == typeof(TimeSpan)
        || ValueType(Node.Type) == typeof(DateTime) || ValueType(Node.Type) == typeof(AngleSingle)
        || ValueType(Node.Type) == typeof(Vector2) || ValueType(Node.Type) == typeof(Vector3) || ValueType(Node.Type) == typeof(Vector4)
        || ValueType(Node.Type) == typeof(Int2) || ValueType(Node.Type) == typeof(Int3) || ValueType(Node.Type) == typeof(Int4)
        || ValueType(Node.Type) == typeof(UInt4) || ValueType(Node.Type) == typeof(Matrix) || ValueType(Node.Type) == typeof(Rectangle) || ValueType(Node.Type) == typeof(RectangleF)
        || ValueType(Node.Type) == typeof(Quaternion) || ValueType(Node.Type) == typeof(Color) || ValueType(Node.Type) == typeof(Color3) || ValueType(Node.Type) == typeof(Color4)
        || ValueType(Node.Type) == typeof(Stride.Core.IO.UFile) || ValueType(Node.Type) == typeof(Stride.Core.IO.UDirectory)
        || ContentAssetReference.Supports(ValueType(Node.Type))
        || ValueType(Node.Type) == typeof(Stride.Rendering.Material)
        || ValueType(Node.Type).IsEnum);
    public PropertyNumericRange? NumericRange { get; } = (ValueType(Node.Type) == typeof(float) || ValueType(Node.Type) == typeof(double) || ValueType(Node.Type) == typeof(int))
        && TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<Stride.Core.Annotations.DataMemberRangeAttribute>(Node.MemberDescriptor.MemberInfo) is { } range
        ? new PropertyNumericRange(range, ValueType(Node.Type)) : null;
}
