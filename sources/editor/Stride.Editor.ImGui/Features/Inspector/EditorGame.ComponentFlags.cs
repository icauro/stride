using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static ulong EnumBits(object value)
    {
        var underlying = Enum.GetUnderlyingType(value.GetType());
        var signed = underlying == typeof(sbyte) || underlying == typeof(short) || underlying == typeof(int) || underlying == typeof(long);
        return signed ? unchecked((ulong)Convert.ToInt64(value)) : Convert.ToUInt64(value);
    }

    private static object EnumFromBits(Type type, ulong bits)
    {
        var underlying = Enum.GetUnderlyingType(type);
        object number = underlying == typeof(byte) ? (object)unchecked((byte)bits)
            : underlying == typeof(sbyte) ? unchecked((sbyte)bits)
            : underlying == typeof(short) ? unchecked((short)bits)
            : underlying == typeof(ushort) ? unchecked((ushort)bits)
            : underlying == typeof(int) ? unchecked((int)bits)
            : underlying == typeof(uint) ? unchecked((uint)bits)
            : underlying == typeof(long) ? unchecked((long)bits) : bits;
        return Enum.ToObject(type, number);
    }

    private void DrawComponentFlags(ComponentField field)
    {
        Gui.PushID(field.Name);
        DrawDocumentedComponentLabel(field);
        Gui.SetNextItemWidth(-1);
        Gui.BeginDisabled(!document!.CanEditComponentValue(field) || !field.SupportsEditing);
        var value = field.Node.Retrieve() ?? Enum.ToObject(field.Type, 0);
        var capturePrefix = Controls.Enabled ? "component/" + field.Component.Id + "/" + field.Name + "/flags" : null;
        if (icons?.HasEnumImages(field.Type) == true)
        {
            if (NativeEnumImageControl.Draw(icons, field.Node, field.Type, value, false,
                field.EnumOptions.Select(option => (Enum)option.Value), UiScale(1), out var updated))
            {
                try
                {
                    if (!Equals(field.Node.Retrieve(), updated)) document.UpdateComponent(field, updated);
                    document.EndComponentEdit(); componentErrors.Remove(field);
                }
                catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
            }
        }
        else
        {
            var open = Gui.BeginCombo("##Flags", value.ToString());
            if (capturePrefix != null) Controls.Record(capturePrefix);
            if (open)
            {
                foreach (var option in field.EnumOptions)
                {
                    var bits = EnumBits(value); var mask = EnumBits(option.Value);
                    var enabled = mask == 0 ? bits == 0 : (bits & mask) == mask;
                    Gui.PushID(option.Name);
                    if (Gui.Checkbox("##Flag", ref enabled))
                    {
                        var updated = EnumFromBits(field.Type, mask == 0 ? 0 : enabled ? bits | mask : bits & ~mask);
                        try
                        {
                            if (!Equals(field.Node.Retrieve(), updated)) document.UpdateComponent(field, updated);
                            document.EndComponentEdit();
                            componentErrors.Remove(field); value = updated;
                        }
                        catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
                    }
                    if (capturePrefix != null) Controls.Record(capturePrefix + "/bit/" + option.Name);
                    Gui.SameLine(); Gui.TextUnformatted(option.Label);
                    Gui.PopID();
                }
                if ((capturePrefix == null ? FlagEnumPropertyControl.DrawBulkActions() : DrawCapturedComponentFlagActions(capturePrefix)) is { } action)
                {
                    try
                    {
                        var updated = ApplyFlagBulkAction((Enum)value, action);
                        if (!Equals(field.Node.Retrieve(), updated)) document.UpdateComponent(field, updated);
                        document.EndComponentEdit(); componentErrors.Remove(field);
                    }
                    catch (Exception error) { componentErrors[field] = error.Message; statusMessage = error.Message; }
                }
                Gui.EndCombo();
            }
        }
        if (componentErrors.TryGetValue(field, out var message)) DrawWrappedText(message);
        Gui.EndDisabled(); Gui.PopID();
    }
    private FlagBulkAction? DrawCapturedComponentFlagActions(string prefix)
        => FlagEnumPropertyControl.DrawBulkActions(command => Controls.Record(prefix + "/" + command));
}
