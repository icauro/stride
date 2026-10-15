using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core.Presentation.Quantum.Presenters;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class VirtualMathFields(Type type)
    {
        internal readonly MemberInfo?[] Members = NativeNumericAxes.Names(type).Select(name => (MemberInfo?)type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)).ToArray();
    }
    private static readonly ConditionalWeakTable<Type, VirtualMathFields> virtualMathFields = new();
    private static object? ReadVirtualMathAxis(object? value, int axis)
        => value == null ? null : virtualMathFields.GetValue(value.GetType(), type => new(type)).Members[axis] switch
        { FieldInfo field => field.GetValue(value), PropertyInfo property => property.GetValue(value), _ => null };
    private static object ReplaceVirtualMathAxis(object value, int axis, double number)
    {
        var result = RuntimeHelpers.GetObjectValue(value);
        switch (virtualMathFields.GetValue(value.GetType(), type => new(type)).Members[axis])
        {
            case FieldInfo field when !field.IsInitOnly:
                field.SetValue(result, Convert.ChangeType(number, field.FieldType, CultureInfo.InvariantCulture)); break;
            case PropertyInfo property when property.SetMethod != null:
                property.SetValue(result, Convert.ChangeType(number, property.PropertyType, CultureInfo.InvariantCulture)); break;
            default: throw new InvalidOperationException("The native numeric component is read-only.");
        }
        return result;
    }
}
