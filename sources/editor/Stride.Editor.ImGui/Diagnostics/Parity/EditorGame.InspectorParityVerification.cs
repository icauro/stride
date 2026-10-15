namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object CaptureInspectorParityVerification() => new
    {
        schemaVersion = 1,
        workspace = workspace?.OpenPath,
        providers = sessionHistory?.Extensions?.CaptureInspectorParityExtensionInventory(),
        groups = DocumentGroupPanel.Model.Groups.Select(group => new
        {
            id = group.Id, group.Floating, active = group.Active?.Id,
            entries = group.Items.Select(entry => new
            {
                entry.Id, entry.Path, type = entry.Document.GetType().FullName,
                generation = entry.State?.Generation, published = entry.State?.IsPublished,
                disposed = entry.State?.IsDisposed,
                canonical = entry.State != null && ReferenceEquals(authoring?.Find(entry.Path), entry.State),
                sourceMutationAllowed = entry.Document is ScriptSourceDocument source && CaptureScriptPresentationAuthority(source)(),
            }).ToArray(),
        }).ToArray(),
    };

    internal object RunScriptPresentationAuthorityVerification(ScriptSourceDocument source)
    {
        if (!args.Contains("--ui-test") && !args.Contains("--parity-test"))
            throw new InvalidOperationException("Presentation-authority probes require an explicit disposable test session.");
        var entry = documents.Find(source) ?? throw new InvalidOperationException("The actual source document is not open.");
        var group = DocumentGroupPanel.Model.Find(entry) ?? throw new InvalidOperationException("The actual source document has no view group.");
        var certificate = CaptureScriptPresentationAuthority(source);
        if (!certificate()) throw new InvalidOperationException("The fixture source must initially be the active canonical document/group.");
        var original = group.Active;
        try
        {
            group.Active = null;
            if (certificate()) throw new InvalidOperationException("A retained source mutation certificate survived group deactivation.");
        }
        finally { group.Active = original; }
        if (!certificate()) throw new InvalidOperationException("Restoring the unchanged canonical group must restore source mutation eligibility.");
        return new { passed = true, document = entry.Id, group = group.Id, generation = entry.State?.Generation,
            revision = source.Version, cases = new[] { "active-canonical-owner", "group-deactivation-rejected", "same-owner-reactivation" } };
    }
}
