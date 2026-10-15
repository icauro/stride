using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageRotationValue(Package package, PackageSettingAccessor accessor, string label, string path,
        bool editable, Action<object?> set, INodePresenter? presenter)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        // Keep equivalent Euler choices across the key's native presentation rebuild.
        var rotation = RotationEditor(owner, Gui.GetID("##PackageRotation"), path);
        Quaternion Read() => (Quaternion)(presenter == null ? accessor.Get(package) : presenter.Value)!;
        if (!string.IsNullOrEmpty(label)) Gui.TextUnformatted(label);
        if (!Gui.BeginTable("##PackageRotationAxes", 3, ImGuiTableFlags.SizingStretchSame)) return;
        var tree = packageSettingTrees.GetValueOrDefault((package, accessor.Key));
        try
        {
            for (var axis = 0; axis < 3; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var coordinate = axis;
                    DrawInspectorAxisBadge(axis); Gui.SetNextItemWidth(-1);
                    var feedback = Gui.GetID("##Value");
                    DrawPackagePropertyString(package, accessor, "##Value", presenter, editable,
                        () => rotation.Read(Read())[coordinate], text =>
                        {
                            var degrees = PropertyConversionFeedback.Convert(feedback, () =>
                            {
                                var parsed = (float)NativeNumericText.Parse(text, typeof(float));
                                if (!float.IsFinite(parsed)) throw new FormatException("The rotation value must be finite.");
                                return MathF.Round(parsed, 3);
                            });
                            var retained = rotation.Capture();
                            try
                            {
                                set(rotation.ReplaceAxis(Read(), coordinate, degrees));
                                CommitPackageSettingEdits(package, accessor, retainPresentation: true);
                            }
                            catch { rotation.Restore(retained); throw; }
                        }, step: text => NativeNumericInput.StepText(typeof(float), rotation.Read(Read())[coordinate], false, null, ref text) ? text : null,
                        readValues: () => [rotation.Read(Read())[coordinate], Read()]);
                    PropertyConversionFeedback.Draw(feedback);
                    // A frozen draft callback can belong to an earlier frame. Check
                    // the actual key after completion, rather than a frame-local flag,
                    // before another axis reads the old presentation snapshot.
                    if (tree != null && !SameSettingValue(accessor, accessor.Get(package), tree.Source)) break;
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
