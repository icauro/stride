using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel
{
    internal void ConfigureNativeText(WindowsGlyphImages images, CommandList commands)
    {
        foreach (var state in outputChannels.Values)
        {
            state.TextView.UseNativeShaping = true;
            state.TextView.NativeFontFamily = "Consolas";
            state.TextView.NativeGlyphImages = images;
            state.TextView.NativeGlyphCommands = commands;
        }
    }
    internal void SuspendNativeText()
    {
        foreach (var state in outputChannels.Values) state.TextView.SuspendNativePresentation();
    }
    internal void DisposeNativeText()
    {
        foreach (var state in outputChannels.Values) state.TextView.Dispose();
    }
}
