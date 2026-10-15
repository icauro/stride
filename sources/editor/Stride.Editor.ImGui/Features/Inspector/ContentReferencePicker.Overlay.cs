using System.Globalization;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ContentReferencePicker
{
    private void DrawReferenceOverlay(Type type, object? value, bool mixed, bool broken)
    {
        var minimum = Gui.GetItemRectMin() + Gui.GetStyle().FramePadding;
        var maximum = Gui.GetItemRectMax() - Gui.GetStyle().FramePadding;
        var width = maximum.X - minimum.X;
        if (width <= 0 || maximum.Y <= minimum.Y) return;
        var reference = mixed ? null : ContentAssetReference.Read(value);
        var asset = reference == null ? null : host.FindReferenceAsset(reference.Id);
        var url = asset == null ? mixed ? EditorLocalization.Text("(Different values)") : Label(type, value, out _)
            : host.ReferenceDisplayUrl(asset);
        var slash = url.LastIndexOf('/');
        var location = slash < 0 ? "" : url[..slash];
        var name = slash < 0 ? url : url[(slash + 1)..];
        var nameWidth = host.MeasurePickerLabel(name).X;
        var scale = Gui.GetStyle().FontScaleDpi;
        var textColor = broken ? Gui.GetColorU32(new System.Numerics.Vector4(.95f, .32f, .32f, 1)) : Gui.GetColorU32(ImGuiCol.Text);
        var draw = Gui.GetWindowDrawList();
        draw.AddRectFilled(minimum, maximum, Gui.GetColorU32(ImGuiCol.FrameBg));
        var origin = minimum;
        origin.Y += Math.Max(0, (maximum.Y - minimum.Y - host.MeasurePickerLabel(name).Y) * .5f);
        if (location.Length != 0 && width - nameWidth >= 24 * scale)
        {
            var separatorWidth = host.MeasurePickerLabel("/").X;
            var prefix = TrimReferenceLabel(location, Math.Max(0, width - nameWidth - separatorWidth - 6 * scale));
            if (prefix.Length != 0)
            {
                var dim = Gui.GetStyle().Colors[(int)ImGuiCol.Text]; dim.W *= .6f;
                host.PaintPickerLabel(prefix + "/", origin, minimum, maximum, Gui.GetColorU32(dim));
                origin.X += host.MeasurePickerLabel(prefix + "/").X;
            }
        }
        var visibleName = TrimReferenceLabel(name, Math.Max(0, maximum.X - origin.X));
        host.PaintPickerLabel(visibleName, origin, minimum, maximum, textColor);
    }

    private string TrimReferenceLabel(string text, float width)
    {
        if (host.MeasurePickerLabel(text).X <= width) return text;
        const string ellipsis = "…";
        if (host.MeasurePickerLabel(ellipsis).X > width) return "";
        var elements = StringInfo.ParseCombiningCharacters(text);
        var low = 0; var high = elements.Length;
        while (low < high)
        {
            var count = (low + high + 1) / 2;
            var end = count == elements.Length ? text.Length : elements[count];
            if (host.MeasurePickerLabel(text[..end] + ellipsis).X <= width) low = count;
            else high = count - 1;
        }
        var finalEnd = low == elements.Length ? text.Length : elements[low];
        return text[..finalEnd] + ellipsis;
    }
}
