using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

internal sealed class OwnedSourceDirectoriesOperation(string root) : Operation
{
    private readonly OwnedDirectoryPhaseReceipts directories = new(root);
    internal bool HasDirectories => directories.HasDirectories;
    internal bool HasPresentDirectories => directories.HasPresentDirectories;
    internal bool HasAbsentDirectories => directories.HasAbsentDirectories;
    public override bool HasEffect => HasDirectories;
    internal bool[] CaptureDirectoryPhases() => directories.CaptureDirectoryPhases();
    internal void RestoreDirectoryPhases(bool[] presence) => directories.RestoreDirectoryPhases(presence);
    internal void ValidateOwnedDirectories() => directories.ValidateOwnedDirectories();
    internal void Ensure(string directory) => directories.Ensure(directory);
    internal void CancelInitial() => directories.CancelInitial();
    internal void UndoOwnedDirectories() => directories.UndoOwnedDirectories();
    internal void RedoOwnedDirectories() => directories.RedoOwnedDirectories();
    protected override void Rollback() => directories.UndoOwnedDirectories();
    protected override void Rollforward() => directories.RedoOwnedDirectories();
}
