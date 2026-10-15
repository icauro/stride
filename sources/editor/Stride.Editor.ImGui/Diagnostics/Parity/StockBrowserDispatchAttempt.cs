using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class StockBrowserDispatchAttempt
{
    private int sinceFrame = -1, buttonClicks, selectionClicks;
    private double started;
    internal object Evidence => new { sinceFrame, buttonClicks, selectionClicks, started,
        route = "Actual selected asset tile and dedicated-editor browser button; double-click timing is not certified." };
    internal void Reset() { sinceFrame = -1; buttonClicks = selectionClicks = 0; }
    internal void Begin(double seconds)
    { if (sinceFrame < 0) { sinceFrame = Gui.GetFrameCount(); started = seconds; } }
    internal bool Open(EditorGame game, string path, string tile, Func<string, bool> click, double seconds)
    {
        Begin(seconds);
        if (seconds - started > 20) throw new TimeoutException("Browser dedicated-editor action did not acknowledge native opening within twenty seconds.");
        if (game.StockBrowserOpenAccepted(path, sinceFrame))
        {
            if (!game.NativeDocumentRestorationSettled || Gui.GetFrameCount() <= game.StockBrowserOpenFrame(path) + 2) return false;
            if (!game.VerifyRecoveryAcceptanceNativeTab(path, out _))
                throw new InvalidOperationException("Native browser accepted opening the stock asset but did not activate its actual native document tab.");
            return true;
        }
        if (!game.StockBrowserSelectionMatches(path))
        {
            if (selectionClicks >= 8) throw new InvalidOperationException("Eight actual tile clicks did not establish the exact native selected stock asset.");
            if (click(tile)) selectionClicks++;
            return false;
        }
        if (buttonClicks >= 8) throw new InvalidOperationException("Eight native browser dedicated-editor button clicks were not acknowledged.");
        if (click("asset/edit")) buttonClicks++;
        return false;
    }
}
