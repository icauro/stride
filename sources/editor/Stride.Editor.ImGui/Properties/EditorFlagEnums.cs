using Stride.Core.Extensions;

namespace Stride.Editor.ImGui;

/// <summary>Flag enumeration without signed conversion failures or top-bit shift overflow.</summary>
internal static class EditorFlagEnums
{
    internal static IEnumerable<Enum> GetIndividualFlags(Type type)
        => Enum.GetValues(type).Cast<Enum>().Where(value => Bits(value) is var bits && bits != 0 && (bits & (bits - 1)) == 0);

    internal static IEnumerable<Enum> GetIndividualFlags(Enum value)
        => GetFlags(value, GetIndividualFlags(value.GetType()));

    internal static IEnumerable<Enum> GetAllFlags(Enum value)
        => IsSigned(value.GetType()) ? GetFlags(value, Enum.GetValues(value.GetType()).Cast<Enum>()) : value.GetAllFlags();

    internal static Enum GetEnum(Type type, IEnumerable<Enum> flags)
        => IsSigned(type) ? (Enum)Enum.ToObject(type, flags.Aggregate(0UL, (bits, flag) => bits | Bits(flag))) : EnumExtensions.GetEnum(type, flags);

    private static IEnumerable<Enum> GetFlags(Enum value, IEnumerable<Enum> flags)
    {
        var bits = Bits(value);
        return bits == 0 ? [] : flags.Where(flag => Bits(flag) is var mask && mask != 0 && (bits & mask) == mask);
    }

    private static bool IsSigned(Type type) => Type.GetTypeCode(Enum.GetUnderlyingType(type))
        is TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64;

    private static ulong Bits(Enum value) => Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
    {
        TypeCode.SByte => unchecked((byte)Convert.ToSByte(value)),
        TypeCode.Int16 => unchecked((ushort)Convert.ToInt16(value)),
        TypeCode.Int32 => unchecked((uint)Convert.ToInt32(value)),
        TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value)),
        _ => Convert.ToUInt64(value),
    };
}
