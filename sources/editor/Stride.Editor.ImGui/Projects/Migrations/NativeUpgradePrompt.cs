using System.Collections.Concurrent;
using Stride.Core.Assets;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class NativeUpgradePrompt : IDisposable
{
    internal sealed record Decision(string Package, string[] Dependencies, string Answer);
    private sealed record Request(string Package, string[] Dependencies, TaskCompletionSource<PackageUpgradeRequestedAnswer> Answer);
    private readonly ConcurrentQueue<Request> requests = new();
    private readonly ConcurrentQueue<Decision> decisions = new();
    internal Decision[] Decisions => decisions.ToArray();
    private Request? current;
    private bool opened, applyToAll, backup = true, backupChosen;
    private readonly CancellationTokenSource stop = new();
    internal bool BackupOriginals => backup;
    internal PackageUpgradeRequestedAnswer Ask(Package package, IList<PackageSession.PendingPackageUpgrade> upgrades, CancellationToken cancellation)
    {
        var answer = new TaskCompletionSource<PackageUpgradeRequestedAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Enqueue(new(package.Meta.Name ?? "", upgrades.Select(upgrade => $"Dependency to {upgrade.Dependency.Name} must be upgraded from version {upgrade.Dependency.Version} to {upgrade.PackageUpgrader.Attribute.UpdatedVersionRange.MinVersion}").ToArray(), answer));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation);
        return answer.Task.WaitAsync(linked.Token).GetAwaiter().GetResult();
    }
    internal void Draw()
    {
        if (current == null && requests.TryDequeue(out var request)) { current = request; opened = false; applyToAll = false; }
        if (current == null) return;
        if (!opened) { Gui.OpenPopup("Package upgrade"); opened = true; }
        Gui.SetNextWindowSize(new System.Numerics.Vector2(650, 0), ImGuiCond.Appearing);
        if (!Gui.BeginPopupModal("Package upgrade", ImGuiWindowFlags.AlwaysAutoResize)) return;
        Gui.PushTextWrapPos(Gui.GetCursorPosX() + 610);
        Gui.TextUnformatted($"The following dependencies in the {current.Package} package need to be upgraded:");
        foreach (var dependency in current.Dependencies) Gui.BulletText(dependency);
        Gui.Spacing(); Gui.TextUnformatted(EditorLocalization.Text("Upgrading assets might break them. We also recommend committing or backing up your project first.", "Message"));
        Gui.PopTextWrapPos();
        Gui.Checkbox(EditorLocalization.Widget("Do this for every package in the solution", "Message"), ref applyToAll);
        Gui.BeginDisabled(backupChosen); Gui.Checkbox(EditorLocalization.Widget("Back up each modified file to a timestamped folder under the solution", "Message"), ref backup); Gui.EndDisabled();
        if (Gui.Button(EditorLocalization.Widget("Upgrade", "Button"))) Complete(applyToAll ? PackageUpgradeRequestedAnswer.UpgradeAll : PackageUpgradeRequestedAnswer.Upgrade);
        Gui.SameLine(); if (Gui.Button(EditorLocalization.Widget("Skip", "Button"))) Complete(applyToAll ? PackageUpgradeRequestedAnswer.DoNotUpgradeAny : PackageUpgradeRequestedAnswer.DoNotUpgrade);
        Gui.EndPopup();
    }
    private void Complete(PackageUpgradeRequestedAnswer answer)
    { backupChosen = true; decisions.Enqueue(new(current!.Package, current.Dependencies, answer.ToString())); current.Answer.TrySetResult(answer); current = null; Gui.CloseCurrentPopup(); }
    public void Dispose() { stop.Cancel(); stop.Dispose(); current?.Answer.TrySetCanceled(); current = null; while (requests.TryDequeue(out var request)) request.Answer.TrySetCanceled(); }
}
