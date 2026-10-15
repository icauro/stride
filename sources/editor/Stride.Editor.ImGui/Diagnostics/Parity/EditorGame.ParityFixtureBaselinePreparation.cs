using Stride.Assets.Scripts;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Arrangement only: a real preloaded generator must have its actual authored
    // project membership saved before metadata-save acceptance examines it.
    internal object PrepareParityVisualScriptBaseline()
    {
        if (!args.Contains("--ui-test") || visualScriptHistoryFixturePath == null)
            throw new InvalidOperationException("Generator baseline preparation requires the exact disposable registered fixture.");
        var current = workspace ?? throw new InvalidOperationException("The native fixture workspace is absent.");
        var history = sessionHistory ?? throw new InvalidOperationException("The native fixture history is absent.");
        var item = current.AssetItems.Single(candidate => SamePath(candidate.FullPath.ToOSPath(), visualScriptHistoryFixturePath));
        if (item.Asset is not VisualScriptAsset || item.Package.Container is not SolutionProject project
            || !current.Session.Projects.Contains(project) || !ReferenceEquals(project.Package, item.Package)
            || !ReferenceEquals(item.Package.Session, current.Session) || item.Package.IsReadOnly)
            throw new InvalidOperationException("The registered fixture generator lost actual native project/package/session ownership.");
        var owner = GetProjectBuildSettingsDocument(project, history);
        owner.ValidateProjectFileBaseline();
        using (history.CreateTransaction("Prepare native generator fixture membership"))
            owner.ChangeGeneratedAssetMembership(item, true);
        owner.Save();
        // Initial native producer Save writes its real compiler output and records
        // baseline hashes after the project XML is genuinely published above.
        var path = CompleteVisualScriptHistoryFixturePreparation();
        owner.ValidateProjectAssetMembership(item, true); owner.VerifySavedProject();
        return new { fixtureArrangement = true, nativeProducerId = item.Id.ToString(), producerPath = path,
            projectPath = owner.Path, generatedPath = item.GetGeneratedAbsolutePath().ToOSPath(),
            actualNativeMembershipAuthoredAndSaved = true, actualNativeProducerSave = true,
            acceptanceBoundary = "Before save.metadata-replacement; not UI template authoring proof." };
    }
    internal string ParityFixturePackagePath => Path.GetFullPath(projectPathInput);
    internal Stride.Core.Assets.AssetId InspectExtensionParityAsset()
    {
        var current = workspace ?? throw new InvalidOperationException("The extension fixture project is absent.");
        var material = current.AssetItems.FirstOrDefault(item => item.Asset is Stride.Assets.Materials.MaterialAsset
            && item.Package.Container is SolutionProject && !item.Package.IsReadOnly)
            ?? throw new InvalidOperationException("Extension acceptance requires a genuine writable native material asset.");
        return material.Id;
    }
}
