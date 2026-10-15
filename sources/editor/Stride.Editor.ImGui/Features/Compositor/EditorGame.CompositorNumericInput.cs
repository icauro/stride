using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Updaters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool TryCompositorNumericGesture(Type type, PropertyNumericRange? range, out double amount,
        double? smallStep = null, double? largeStep = null)
        => NativeNumericInput.TryGesture(type, range, out amount, smallStep, largeStep);

    private static object StepCompositorNumber(Type type, object? value, PropertyNumericRange? range, double amount)
        => NativeNumericInput.Step(type, value, range, amount);
    private static bool StepCompositorInput<T>(ref T value, PropertyNumericRange? range)
    {
        if (!TryCompositorNumericGesture(typeof(T), range, out var amount)) return false;
        value = (T)StepCompositorNumber(typeof(T), value, range, amount); return true;
    }

    private static unsafe bool DrawCompositorIntegerInput<T>(string label, ImGuiDataType type, T value,
        PropertyNumericRange? range, out T updated) where T : unmanaged
    {
        var current = value;
        var changed = Gui.InputScalar(label, type, &current, null, (byte*)null);
        changed |= StepCompositorInput(ref current, range);
        updated = current; return changed;
    }

}
