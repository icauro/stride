using System.Runtime.CompilerServices;
using Stride.Core.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal static class NativeEnumImageControl
{
    private sealed class Selection
    {
        internal object? Observed;
        internal bool Mixed;
        internal HashSet<Enum> Values = [];
    }
    private static readonly ConditionalWeakTable<object, Dictionary<uint, Selection>> selections = new();

    internal static bool Draw(EditorIcons icons, object owner, Type type, object? value, bool mixed,
        IEnumerable<Enum> options, float scale, out object? updated)
    {
        updated = value;
        var flags = type.IsDefined(typeof(FlagsAttribute), true);
        var states = selections.GetValue(owner, _ => new());
        var id = Gui.GetID("##EnumImages");
        if (!states.TryGetValue(id, out var state)) states[id] = state = new();
        if (!Equals(state.Observed, value) || state.Mixed != mixed)
        {
            state.Observed = value; state.Mixed = mixed;
            state.Values = !mixed && value is Enum enumeration && flags ? EditorFlagEnums.GetIndividualFlags(enumeration).ToHashSet() : [];
        }
        var changed = false;
        var available = Gui.GetContentRegionAvail().X; var used = 0f;
        Gui.BeginGroup();
        foreach (var option in options)
        {
            var width = 24 * scale;
            if (used > 0 && used + Gui.GetStyle().ItemSpacing.X + width <= available)
            { Gui.SameLine(); used += Gui.GetStyle().ItemSpacing.X; }
            else used = 0;
            var selected = !mixed && (flags ? state.Values.Contains(option) : Equals(value, option));
            Gui.PushID(option.ToString());
            if (icons.EnumImageButton(option, selected, mixed))
            {
                if (!flags) updated = option;
                else
                {
                    // Original FlagEnumToObservableList starts mixed values with an empty list.
                    var values = mixed ? new HashSet<Enum>() : new HashSet<Enum>(state.Values);
                    if (selected) values.Remove(option); else values.Add(option);
                    var bits = EditorFlagEnums.GetEnum(type, values);
                    var mask = EditorFlagEnums.GetEnum(type, [option]);
                    var unsigned = Bits(bits);
                    if (selected) unsigned &= ~Bits(mask); else unsigned |= Bits(mask);
                    var result = (Enum)Enum.ToObject(type, unsigned);
                    state.Values = EditorFlagEnums.GetAllFlags(result).ToHashSet();
                    updated = EditorFlagEnums.GetEnum(type, state.Values);
                }
                changed = !Equals(value, updated) || mixed;
                // Observe the proposed selection so a rejected canonical commit
                // resets the retained native list from the real value next frame.
                if (changed) state.Observed = updated;
            }
            Gui.PopID(); used += width;
        }
        Gui.EndGroup();
        return changed;
    }
    private static ulong Bits(Enum value) => Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
    {
        TypeCode.SByte => unchecked((byte)Convert.ToSByte(value)),
        TypeCode.Int16 => unchecked((ushort)Convert.ToInt16(value)),
        TypeCode.Int32 => unchecked((uint)Convert.ToInt32(value)),
        TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value)),
        _ => Convert.ToUInt64(value),
    };
}
