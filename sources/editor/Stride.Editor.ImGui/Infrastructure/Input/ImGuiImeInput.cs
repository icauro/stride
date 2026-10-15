using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Input;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Positions native composition without inserting uncommitted preedit text into authored documents.</summary>
internal sealed unsafe class ImGuiImeInput : IDisposable
{
    private readonly Func<ITextInputDevice?> resolveDevice;
    private readonly PlatformSetImeDataFn setImeData;
    private ITextInputDevice? device;
    private Rectangle area;
    private bool hasArea, wantsText, focused, enabled, disposed;
    private string? reportedError;
    internal ImGuiImeInput(Func<ITextInputDevice?> resolveDevice)
    {
        this.resolveDevice = resolveDevice;
        setImeData = SetImeData;
        var platform = Gui.GetPlatformIO();
        platform.PlatformSetImeDataFn = (void*)Marshal.GetFunctionPointerForDelegate(setImeData);
    }
    internal void UpdateFocus(bool active)
    {
        focused = active;
        Synchronize();
    }
    private void SetImeData(ImGuiContext* context, ImGuiViewport* viewport, ImGuiPlatformImeData* data)
    {
        if (disposed || data == null || viewport == null) return;
        var position = data->InputPos - viewport->Pos;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(data->InputLineHeight)) return;
        var next = new Rectangle((int)Math.Clamp(position.X, -1_000_000, 1_000_000),
            (int)Math.Clamp(position.Y, -1_000_000, 1_000_000), 1,
            (int)Math.Clamp(MathF.Ceiling(data->InputLineHeight), 1, 1_000_000));
        var changed = !hasArea || area != next;
        area = next; hasArea = true;
        wantsText = data->WantTextInput != 0 || data->WantVisible != 0;
        Synchronize(changed);
    }
    private void Synchronize(bool areaChanged = false)
    {
        if (disposed) return;
        try
        {
            var next = resolveDevice();
            if (!ReferenceEquals(next, device))
            {
                if (enabled) device?.DisableTextInput();
                enabled = false; device = next; areaChanged = true;
            }
            var shouldEnable = focused && wantsText && hasArea && device != null;
            if (shouldEnable && (areaChanged || !enabled)) device!.SetTextInputRectangle(area, true);
            if (shouldEnable && !enabled) { device!.EnabledTextInput(); enabled = true; }
            else if (!shouldEnable && enabled) { device!.DisableTextInput(); enabled = false; }
            reportedError = null;
        }
        catch (Exception error)
        {
            var message = error.ToString();
            if (reportedError != message) { reportedError = message; Console.Error.WriteLine("IME input: " + message); }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        focused = false; wantsText = false; Synchronize();
        var platform = Gui.GetPlatformIO();
        platform.PlatformSetImeDataFn = null;
        disposed = true; device = null;
        GC.KeepAlive(setImeData);
    }
}
