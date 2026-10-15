using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Transactions;

namespace Stride.Editor.ImGui;

/// <summary>Undoable authored package folder metadata; no filesystem deletion.</summary>
internal sealed partial class PackageFolderHistory
{
    private readonly ITransactionStack transactions;
    private readonly EditorSessionHistory? sessionHistory;
    private readonly ProjectWorkspace? authoringWorkspace;
    public PackageFolderHistory(EditorSessionHistory? history = null, ProjectWorkspace? workspace = null)
    {
        if (history != null && workspace == null) throw new ArgumentException("Shared package edits require their actual workspace.", nameof(workspace));
        sessionHistory = history; authoringWorkspace = workspace; transactions = history?.Stack ?? TransactionStackFactory.Create(256);
    }
    public bool CanUndo => transactions.CanRollback;
    public bool CanRedo => transactions.CanRollforward;
    public long Version { get; private set; }
    private static void RequireCurrentPackage(Package package, ProjectWorkspace? workspace)
    {
        if (package.IsReadOnly || workspace != null && (!workspace.Session.Packages.Contains(package)
            || !ReferenceEquals(package.Session, workspace.Session) || package.State < PackageState.AssetsReady))
            throw new InvalidOperationException("The folder package is no longer available for editing.");
    }
    public string CreateFolder(Package package, string parentPath, string name)
    {
        if (sessionHistory == null) _ = HistoryPresentation;
        RequireCurrentPackage(package, authoringWorkspace);
        if (package.IsReadOnly) throw new InvalidOperationException("This package that contains this folder can't be edited.");
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ')) throw new ArgumentException("Invalid folder name.");
        var device = name.Split('.')[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase) || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && device[3] is >= '1' and <= '9')
            throw new ArgumentException("Invalid folder name.");
        var relative = parentPath.Length == 0 ? name : parentPath.TrimEnd('/') + "/" + name;
        if (package.ExplicitFolders.Any(path => string.Equals(path.ToString().TrimEnd('/'), relative, StringComparison.OrdinalIgnoreCase))
            || package.Assets.Any(item => item.UnqualifiedUrl.ToString().StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A folder with this name already exists.");
        var before = package.ExplicitFolders.ToArray(); var after = before.Append(new UDirectory(relative)).ToArray();
        var participant = sessionHistory?.Registry.GetOrCreate(package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(authoringWorkspace!, package, sessionHistory));
        var beforeRevision = participant?.Document.Revision ?? 0;
        var afterRevision = participant?.Document.AllocateRevision() ?? 0;
        using (sessionHistory?.CreateTransaction("Create folder: " + relative) ?? transactions.CreateTransaction())
        {
            var operation = new FolderOperation(package, before, after, participant?.Document, beforeRevision, afterRevision, authoringWorkspace);
            var wasDirty = package.IsDirty;
            FolderOperation.Apply(package, after);
            participant?.Document.RestoreRevision(afterRevision);
            try
            {
                if (sessionHistory != null) sessionHistory.PushOperation(operation, participant!);
                else transactions.PushOperation(operation);
            }
            catch { FolderOperation.Apply(package, before); participant?.Document.RestoreRevision(beforeRevision); package.IsDirty = wasDirty; throw; }
        }
        if (sessionHistory == null) HistoryPresentation.Name("Create folder: " + relative);
        Version++; return relative;
    }
    public void Undo() { if (CanUndo) { transactions.Rollback(); Version++; } }
    public void Redo() { if (CanRedo) { transactions.Rollforward(); Version++; } }
    public void Clear() { if (sessionHistory == null) transactions.Clear(); Version++; }
    private sealed class FolderOperation(Package package, UDirectory[] before, UDirectory[] after,
        PackageMetadataDocument? metadata, long beforeRevision, long afterRevision, ProjectWorkspace? workspace) : Operation
    {
        internal static void Apply(Package package, UDirectory[] folders)
        {
            package.ExplicitFolders.Clear(); package.ExplicitFolders.AddRange(folders); package.IsDirty = true;
        }
        protected override void Rollback() { RequireCurrentPackage(package, workspace); Apply(package, before); metadata?.RestoreRevision(beforeRevision); }
        protected override void Rollforward() { RequireCurrentPackage(package, workspace); Apply(package, after); metadata?.RestoreRevision(afterRevision); }
    }
}
