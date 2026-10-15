using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

// Original TextBoxPropertyValueValidationBehavior and its 0.30-second, three-cycle storyboard.
internal static class PropertyConversionFeedback
{
    private static readonly Dictionary<uint, long> failures = [];
    internal static void Fail(uint id)
    {
        foreach (var stale in failures.Where(pair => Stopwatch.GetElapsedTime(pair.Value).TotalSeconds >= 1.8).Select(pair => pair.Key).ToArray()) failures.Remove(stale);
        failures[id] = Stopwatch.GetTimestamp();
    }
    internal static void Validate(uint id) => failures.Remove(id);
    internal static void Fail(uint id, Exception error)
    {
        if (error is FormatException or OverflowException or InvalidCastException or ArgumentException) Fail(id);
    }
    internal static void Run(uint id, Action conversion)
    {
        Validate(id);
        try { conversion(); }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException or ArgumentException)
        { Fail(id); throw; }
    }
    internal static T Convert<T>(uint id, Func<T> conversion)
    {
        Validate(id);
        try { return conversion(); }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException or ArgumentException)
        { Fail(id); throw; }
    }
    internal static void Draw(uint id)
    {
        if (!failures.TryGetValue(id, out var started)) return;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (elapsed >= 1.8) { failures.Remove(id); return; }
        var phase = elapsed % .6;
        var opacity = (float)(phase < .3 ? phase / .3 : (.6 - phase) / .3);
        var dpi = Gui.GetStyle().FontScaleDpi;
        Gui.GetWindowDrawList().AddRect(Gui.GetItemRectMin(), Gui.GetItemRectMax(),
            Gui.GetColorU32(new Vector4(205 / 255f, 92 / 255f, 92 / 255f, opacity)), 3 * dpi, ImDrawFlags.None, 2 * dpi);
    }
}
