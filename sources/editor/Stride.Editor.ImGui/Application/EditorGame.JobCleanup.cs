using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // The caller owns pending jobs/resources; this reports only the cleanup attempt outcome.
    private bool TryCleanupEditorOwnedWork(Action cleanup, ref long retryAt, ref string? failure, string context)
    {
        if (Environment.TickCount64 < retryAt) return false;
        try
        {
            cleanup(); retryAt = 0; failure = null;
            return true;
        }
        catch (Exception error)
        {
            retryAt = Environment.TickCount64 + 250;
            var current = error.ToString();
            if (failure != current)
            {
                failure = current;
                outputLog.Append("Project", LogMessageType.Error, context, error.Message, current);
            }
            return false;
        }
    }
}
