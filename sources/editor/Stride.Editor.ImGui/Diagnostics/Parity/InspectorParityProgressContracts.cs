namespace Stride.Editor.ImGui;

internal static class InspectorParityProgressContracts
{
    internal static object Run()
    {
        var progress = new EditorOperationProgress();
        var cancellations = 0;
        progress.Set("save-a", "Saving...", "Saving...", null, () => cancellations++);
        progress.Set("import-b", "Add asset...", "Importing", .25f, null);
        var nativeLog = "native warning \u03a9\r\nactual diagnostic detail";
        progress.Append("save-a", nativeLog);
        Require(progress.Next()!.Log == nativeLog, "Native diagnostic Unicode/newlines must remain exact in progress.");
        progress.Append("import-b", "independent native import log");
        var generation = progress.Next()!.Generation;
        progress.RequestCancel("save-a", Guid.NewGuid());
        Require(cancellations == 0, "Stale progress generation must not cancel the actual operation.");
        progress.RequestCancel("save-a", generation);
        progress.RequestCancel("save-a", generation);
        Require(cancellations == 1 && progress.Snapshot().Count == 2, "An actual cancel is dispatched once without clearing another operation.");
        progress.Complete("save-a", new IOException("actual write failure"), false);
        Require(progress.Next() is { State: EditorOperationProgressState.Failed, Cancel: null } failure
            && failure.Log.StartsWith(nativeLog + Environment.NewLine, StringComparison.Ordinal)
            && failure.Log.Contains("actual write failure", StringComparison.Ordinal) && failure.LogEntryCount == 2, "Failed work retains its actual error until Close.");
        progress.Clear("save-a");
        Require(progress.Next() is { OwnerId: "import-b", Fraction: .25f, Log: "independent native import log" }, "Closing one operation must preserve the other operation's actual progress.");
        progress.Complete("import-b", null, false);
        Require(progress.Next() == null, "Successful work must retire without a false failure or persistent completed modal.");
        var rejectedWorker = Task.Run(() =>
        {
            try { progress.Append("import-b", "worker publication"); return false; }
            catch (InvalidOperationException) { return true; }
        }).GetAwaiter().GetResult();
        Require(rejectedWorker && progress.Next() == null, "A detached worker must not publish UI progress directly.");
        return new { passed = true, cases = new[] { "stale-cancel", "single-cancel", "error-retained", "independent-owners", "success-retired", "worker-publication-rejected", "native-log-exact", "error-appended-after-native-log" } };
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
