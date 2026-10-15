using System.Xml.Linq;
using Stride.Assets;
using Stride.Core;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Save hooks also retry a previously saved settings generation after a
    // guarded platform-file save failed; the baseline advances only on success.
    private void SynchronizeSavedGameSettingsOrientations()
    {
        if (authoring == null || workspace?.Session.CurrentProject == null) return;
        foreach (var state in authoring.PublishedStates.Where(state => state.Document is AssetMetadataDocument).ToArray())
        {
            using var ownerLease = state.AcquireView();
            SynchronizeGameSettingsOrientation((AssetMetadataDocument)state.Document);
        }
    }
    private void SynchronizeGameSettingsOrientation(AssetMetadataDocument settings)
    {
        if (!settings.TryGetSavedGameOrientationChange(out var orientation, out var version)
            || workspace?.Session.CurrentProject is not { } project || authoring == null || sessionHistory == null) return;
        var target = GameSettingsAssetCompiler.GetPlatformOrientationFile(project);
        if (target == null || !File.Exists(target.ToOSPath()))
        {
            // The production save hook is a successful no-op for other platforms
            // and missing conventional platform files.
            settings.CompleteSavedGameOrientationChange(orientation, version); return;
        }
        var path = Path.GetFullPath(target.ToOSPath());
        if (project.Package.IsReadOnly) throw new InvalidOperationException("The current platform project is read-only.");
        var sourceItem = workspace.AssetItems.FirstOrDefault(item => SamePath(item.FullPath.ToOSPath(), path));
        if (GetCanonicalDocument(path, () => sourceItem?.Asset is SourceCodeAsset
            ? new AssetScriptSourceDocument(sourceItem, sessionHistory) : new ScriptSourceDocument(path, IsReadOnlyAssetPath(path), sessionHistory)) is not ScriptSourceDocument source)
            throw new InvalidOperationException("The platform orientation file has a different canonical document owner.");
        var state = authoring.Find(path) ?? throw new InvalidOperationException("The platform source has no canonical owner.");
        using var sourceLease = state.AcquireView();
        if (!source.CanSave || !ReferenceEquals(state.Document, source))
            throw new InvalidOperationException("The platform orientation file cannot be saved.");
        var text = source.EditorText;
        var updated = project.Platform == PlatformType.Android
            ? GameSettingsAssetCompiler.UpdateAndroidActivityOrientation(text, orientation)
            : UpdateIosOrientationText(text, orientation);
        using (sessionHistory.CreateTransaction("Synchronize platform orientation")) source.UpdateEditorText(updated);
        source.Save();
        settings.CompleteSavedGameOrientationChange(orientation, version);
    }
    private static string UpdateIosOrientationText(string text, Stride.Graphics.RequiredDisplayOrientation orientation)
    {
        var xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var before = xml.ToString(SaveOptions.DisableFormatting);
        var orientationKey = xml.Descendants("key").FirstOrDefault(element => element.Value == "UISupportedInterfaceOrientations");
        var spacing = new List<XText>();
        while (orientationKey?.NextNode is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value))
        { spacing.Add(whitespace); whitespace.Remove(); }
        try { GameSettingsAssetCompiler.UpdateIosOrientation(xml, orientation); }
        finally { if (orientationKey != null && spacing.Count != 0) orientationKey.AddAfterSelf(spacing); }
        var after = xml.ToString(SaveOptions.DisableFormatting);
        if (before == after) return text;
        // XDocument.ToString excludes the XML declaration. Preserve its exact
        // text while the canonical source document preserves file encoding.
        var declarationEnd = text.StartsWith("<?xml", StringComparison.Ordinal) ? text.IndexOf("?>", StringComparison.Ordinal) : -1;
        return declarationEnd < 0 ? after : text[..(declarationEnd + 2)] + after;
    }
}
