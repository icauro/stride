using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class StockFolderNavigationAttempt
{
    private string? target;
    private double deadline;
    private int attempts, issuedFrame = -1;
    private double expansionDeadline;
    private int expansionAttempts, expansionIssuedFrame = -1;
    internal object Evidence => new { target, deadline, attempts, issuedFrame, expansionDeadline, expansionAttempts, expansionIssuedFrame };
    internal bool Expand(EditorGame game, string id, Func<string, bool> click, double seconds)
    {
        if (game.Controls.Expanded.GetValueOrDefault("location/" + id) && !Gui.GetIO().MouseDown[0]) return true;
        if (expansionDeadline == 0) expansionDeadline = seconds + 20;
        if (seconds >= expansionDeadline || expansionAttempts >= 8)
            throw new TimeoutException("Actual native folder chevron expansion failed its bounded acknowledgement: " + id);
        if (Gui.GetIO().MouseDown[0] || Gui.GetFrameCount() <= expansionIssuedFrame + 3) return false;
        if (click("location-expand/" + id)) { expansionIssuedFrame = Gui.GetFrameCount(); expansionAttempts++; }
        return false;
    }
    internal bool Select(EditorGame game, string id, Func<string, bool> click, double seconds)
    {
        if (target != id) { target = id; deadline = seconds + 20; attempts = 0; issuedFrame = -1; expansionDeadline = 0; expansionAttempts = 0; expansionIssuedFrame = -1; }
        if (seconds >= deadline) throw new TimeoutException("Actual native folder selection exceeded its shared deadline: " + id);
        var frame = Gui.GetFrameCount(); var receipt = game.InspectStockFolderPointer();
        if (issuedFrame >= 0 && frame > issuedFrame + 2 && !Gui.GetIO().MouseDown[0])
        {
            if (receipt.AcceptedId == id && receipt.AcceptedFrame >= issuedFrame)
            {
                if (game.StockScriptDispatchLocationSelected(id)) return true;
                throw new InvalidOperationException("The native folder accepted its click but failed to retain the requested selection: " + id);
            }
        }
        if (issuedFrame >= 0 && (frame <= issuedFrame + 3 || Gui.GetIO().MouseDown[0])) return false;
        if (attempts >= 8) throw new InvalidOperationException("Eight native folder clicks were not accepted: " + id);
        if (click("location/" + id)) { issuedFrame = frame; attempts++; }
        return false;
    }
}
