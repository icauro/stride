using System.Globalization;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageAngleValue(Package package, PackageSettingAccessor accessor, string label,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        object? Read() => presenter == null ? accessor.Get(package) : presenter.Value;
        double Degrees(object? value) => value is AngleSingle angle ? Math.Round((double)angle.Degrees, 3)
            : throw new InvalidOperationException("The registered package angle is unavailable.");
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        var feedback = Gui.GetID("##Angle");
        Gui.SetNextItemWidth(-1);
        DrawPackagePropertyString(package, accessor, "##Angle", presenter, editable, Read,
            text =>
            {
                var next = PropertyConversionFeedback.Convert(feedback, () =>
                {
                    var degrees = (double)NativeNumericText.Parse(text, typeof(double), retainedAngleRange);
                    var angle = new AngleSingle((float)degrees, AngleType.Degree);
                    PropertyValueValidation.Normalize(angle, null);
                    return angle;
                });
                if (!Equals(Read(), next)) set(next);
            }, format: value => Degrees(value).ToString(CultureInfo.InvariantCulture),
            step: text => NativeNumericInput.StepText(typeof(double), Degrees(Read()), false,
                retainedAngleRange, ref text) ? text : null);
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Angle in degrees", "ToolTip"));
        PropertyConversionFeedback.Draw(feedback);
    }
}
