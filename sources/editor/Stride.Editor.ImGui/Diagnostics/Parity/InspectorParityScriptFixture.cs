namespace Stride.Editor.ImGui;

internal sealed record InspectorParityScriptFixture(ScriptSourceDocument Source, Guid EntryId, Guid GroupId,
    EditorDocumentEntry? PreviousActive, EditorDocumentEntry? PreviousGroupActive, bool OpenedByProbe, long Version, string Text);

internal sealed partial class EditorGame
{
    internal InspectorParityScriptFixture PrepareScriptPresentationAuthorityVerification()
    {
        if (!args.Contains("--ui-test") && !args.Contains("--parity-test"))
            throw new InvalidOperationException("Source-group probes require an explicit disposable test session.");
        if (workspace == null || sessionHistory == null || projectLoading != null)
            throw new InvalidOperationException("The real fixture package must be loaded first.");
        var item = workspace.AssetItems.FirstOrDefault(item => item.Asset is Stride.Core.Assets.SourceCodeAsset
            && item.FullPath.ToOSPath().EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && !item.Package.IsReadOnly && File.Exists(item.FullPath.ToOSPath()));
        if (item == null) throw new InvalidOperationException("The disposable fixture has no editable native source asset.");
        var previous = documents.Active;
        var originalEntry = documents.Find(item.FullPath.ToOSPath());
        var originalGroupActive = originalEntry == null ? null : DocumentGroupPanel.Model.Find(originalEntry)?.Active;
        OpenScriptDocument(item.FullPath.ToOSPath());
        var entry = documents.Find(item.FullPath.ToOSPath()) ?? throw new InvalidOperationException("The actual source editor did not open.");
        var source = entry.Document as ScriptSourceDocument ?? throw new InvalidOperationException("The canonical asset owner is not a source document.");
        DocumentGroupPanel.Model.Synchronize(documents.Entries);
        DocumentGroupPanel.Model.Select(entry);
        var group = DocumentGroupPanel.Model.Find(entry)!;
        return new(source, entry.Id, group.Id, previous, originalGroupActive, originalEntry == null, source.Version, source.EditorText);
    }

    internal bool IsScriptPresentationAuthorityVerificationReady(InspectorParityScriptFixture fixture)
        => fixture.Source.Version == fixture.Version && CaptureScriptPresentationAuthority(fixture.Source)();

    internal void CloseScriptPresentationAuthorityVerification(InspectorParityScriptFixture fixture)
    {
        if (!args.Contains("--ui-test") && !args.Contains("--parity-test"))
            throw new InvalidOperationException("Source-group probes require an explicit disposable test session.");
        var entry = documents.Find(fixture.Source);
        if (entry == null || entry.Id != fixture.EntryId) throw new InvalidOperationException("The probed source owner changed before cleanup.");
        if (fixture.Source.Version != fixture.Version || fixture.Source.EditorText != fixture.Text)
            throw new InvalidOperationException("The source-group authority probe modified its authored source.");
        if (fixture.OpenedByProbe) CloseDocument(entry);
        DocumentGroupPanel.Model.Synchronize(documents.Entries);
        if (fixture.PreviousGroupActive is { } grouped && documents.Entries.Contains(grouped)) DocumentGroupPanel.Model.Select(grouped);
        if (fixture.PreviousActive is { } previous && documents.Entries.Contains(previous))
        {
            ActivateDocument(previous);
            DocumentGroupPanel.Model.Select(previous);
        }
    }
}
