using System.Globalization;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly PropertyNumericRange retainedUnicodeRange = new(new PropertyNumericMetadata(0, double.MaxValue, 1, 10, 0), typeof(double));
    private void DrawRetainedCharacter(object identity, Func<object?[]> read, Func<bool> canEdit, Action<char> assign,
        Func<IDisposable> acquire, int valueCount = 1, Action<string>? capture = null, Action<char>? assignGlyph = null)
    {
        var values = read(); var mixed = values.Take(valueCount).Any(value => !Equals(value, values[0]));
        var value = values[0] is char character ? character : default;
        bool Editable() => !IsAuthoringPipelineUpdating && canEdit();
        var available = Gui.GetContentRegionAvail().X;
        var width = Math.Min(UiScale(40), Math.Max(1, available - Gui.GetStyle().ItemSpacing.X - 1));
        Gui.BeginGroup();
        try
        {
            Gui.SetNextItemWidth(width);
            if (NativeCharacterInput.DrawGlyph(identity, value, mixed, Editable(), width, out var updated))
            {
                var feedback = Gui.GetID("##Character");
                try
                {
                    ValidatePendingPropertyStrings();
                    var lease = acquire();
                    try
                    {
                        if (!Editable()) throw new InvalidOperationException("The character property owner changed.");
                        (assignGlyph ?? assign)(updated);
                    }
                    finally
                    {
                        try { lease.Dispose(); }
                        catch { packageCommandCleanup.Add(lease.Dispose); throw; }
                    }
                }
                catch (Exception error)
                { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; NativeCharacterInput.ResetGlyph(identity); }
                PropertyConversionFeedback.Draw(feedback);
            }
            capture?.Invoke("glyph");
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Character", "ToolTIp"));
            Gui.SameLine(); Gui.SetNextItemWidth(Math.Max(1, available - width - Gui.GetStyle().ItemSpacing.X));
            var id = Gui.GetID("##Unicode");
            var current = read(); mixed = current.Take(valueCount).Any(source => !Equals(source, current[0]));
            double Unicode(object? source) => source is char code ? code : 0;
            try
            {
                PropertyGrid.Strings.Draw(identity, mixed, Editable, read, text =>
                {
                    var next = PropertyConversionFeedback.Convert(id, () => Convert.ToChar(Convert.ToUInt32(
                        NativeNumericText.Parse(text, typeof(double), retainedUnicodeRange)), CultureInfo.CurrentCulture));
                    assign(next);
                }, acquire, text => NativeNumericInput.StepText(typeof(double), Unicode(read()[0]), mixed, retainedUnicodeRange, ref text) ? text : null,
                    source => Unicode(source).ToString(CultureInfo.CurrentCulture), controlLabel: "##Unicode");
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(id, error); statusMessage = error.Message; }
            capture?.Invoke("unicode");
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Unicode value", "ToolTip"));
            PropertyConversionFeedback.Draw(id);
        }
        finally { Gui.EndGroup(); }
    }
    private void DrawComponentRetainedCharacter(ComponentField field)
    {
        var owner = document!; Gui.PushID(field.Name);
        try
        {
            if (!BeginDocumentedComponentRow(field)) return;
            try
            {
                DrawRetainedCharacter(field.Node, () => [field.Node.Retrieve()],
                    () => ReferenceEquals(document, owner) && owner.CanEditComponentValue(field),
                    character => { owner.UpdateComponent(field, character); owner.EndComponentEdit(); },
                    () => AcquirePropertyStringOwners([owner]), capture: Controls.Enabled ? part => Controls.Record("component/" + field.Component.Id + "/" + field.Name + "/" + part) : null);
                if (Controls.Enabled) Controls.Record("component/" + field.Component.Id + "/" + field.Name);
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
    private void DrawRetainedCombinedVirtualCharacter(IReadOnlyList<ComponentVirtualOwner> source)
    {
        var entries = source.ToArray(); var first = entries[0].Node;
        DrawRetainedCharacter(first, () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
            () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
            character => ApplyCombinedComponentVirtualCore(entries, (_, _) => character),
            () => AcquireRetainedCombinedVirtualTargets(entries), entries.Length);
    }
}
