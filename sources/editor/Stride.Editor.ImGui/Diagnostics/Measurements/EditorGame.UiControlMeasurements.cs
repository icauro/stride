using System.Diagnostics;
using System.Text.Json;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly bool[] UiMeasurementOrder = [true, false, false, true, true, false];
    private readonly List<object> uiMeasurementPhases = [];
    private readonly List<double> uiMeasurementTimes = [];
    private readonly List<long> uiMeasurementAllocations = [];
    private readonly HashSet<int> uiMeasurementVertices = [];
    private int uiMeasurementPhase, uiMeasurementFrame, uiMeasurementWarmup;
    private bool uiMeasurementStarted, uiMeasurementComplete;
    private readonly Stopwatch uiMeasurementDeadline = Stopwatch.StartNew();
    private void BeginUiControlMeasurement()
    {
        if (!measureUiControls || uiMeasurementComplete) return;
        if (uiMeasurementDeadline.Elapsed.TotalSeconds > 90) throw new TimeoutException("UI control measurement did not complete.");
        Gui.GetIO().AddMousePosEvent(-1000, -1000);
        if (document == null || engineDrawCalls == 0) return;
        if (!uiMeasurementStarted)
        {
            document.Selected = document.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
            uiMeasurementStarted = true;
        }
        if (uiMeasurementWarmup++ < 120) return;
        Controls.Enabled = UiMeasurementOrder[uiMeasurementPhase];
        if (uiMeasurementFrame == 0) { Controls.Centers.Clear(); Controls.Expanded.Clear(); }
    }
    private void EndUiControlMeasurement(long ticks, long allocations)
    {
        if (!measureUiControls || !uiMeasurementStarted || uiMeasurementComplete || uiMeasurementWarmup <= 120) return;
        if (++uiMeasurementFrame <= 30) return;
        if (Controls.Enabled != UiMeasurementOrder[uiMeasurementPhase]) throw new InvalidOperationException("Measurement registration mode differs from planned phase.");
        if (!Controls.Enabled && (Controls.Centers.Count != 0 || Controls.Expanded.Count != 0)) throw new InvalidOperationException("Production mode captured automation metadata.");
        uiMeasurementTimes.Add(ticks * 1000000.0 / Stopwatch.Frequency);
        uiMeasurementAllocations.Add(allocations); uiMeasurementVertices.Add(Gui.GetDrawData().TotalVtxCount);
        if (uiMeasurementTimes.Count < 200) return;
        var sorted = uiMeasurementTimes.Order().ToArray();
        uiMeasurementPhases.Add(new { registrationEnabled = Controls.Enabled, samples = sorted.Length,
            meanMicroseconds = sorted.Average(), p50Microseconds = sorted[99], p95Microseconds = sorted[189], p99Microseconds = sorted[197],
            allocatedBytesPerFrame = uiMeasurementAllocations.Average(value => (double)value), capturedControls = Controls.Centers.Count,
            vertices = uiMeasurementVertices.ToArray() });
        uiMeasurementTimes.Clear(); uiMeasurementAllocations.Clear(); uiMeasurementVertices.Clear(); uiMeasurementFrame = 0;
        if (++uiMeasurementPhase < UiMeasurementOrder.Length) return;
        uiMeasurementComplete = true;
        var outputIndex = Array.IndexOf(args, "--measure-ui-controls");
        if (outputIndex + 1 >= args.Length) throw new ArgumentException("Measurement output JSON path is required.");
        File.WriteAllText(args[outputIndex + 1], JsonSerializer.Serialize(new { phases = uiMeasurementPhases, dataset = MeasurementDataset,
            runtime = Environment.Version.ToString(), backend = "Direct3D11", adapter = GraphicsDevice.Adapter.DriverInfo,
            boundary = "CPU DrawPanels only and current-thread managed allocation delta; excludes Gui.Render, engine draw, GPU work/presentation. Alternating same-process warm UI, not a matched WPF comparison." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("UI CONTROL MEASUREMENT PASS: six phases, 1200 samples; production metadata remains empty."); Exit();
    }
}
