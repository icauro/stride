using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private StandaloneDependencyAuthority? standaloneDependencyAddition;
    private bool closePackageDependencyPicker;
    private void PrepareStandaloneDependencyAddition(StandalonePackage package)
    {
        ClearStandaloneDependencyAddition();
        var current = workspace!; var history = SessionAuthoringHistory!;
        var state = history.Registry.GetOrCreate(package.Package.FullPath.ToOSPath(), null,
            () => new PackageMetadataDocument(current, package.Package, history));
        standaloneDependencyAddition = new(current, history, package, null, state);
    }
    private void CommitStandaloneDependencyAddition(StandaloneDependencyAuthority run, PackageDependencyCandidate selected)
    {
        if (!run.Current(this) || !CanStartAssetEdit) throw new InvalidOperationException("The package changed while selecting its dependency.");
        ValidatePendingPropertyStrings();
        if (!run.Current(this)) throw new InvalidOperationException("The package changed while selecting its dependency.");
        using var capture = run.History.CaptureOperationsAtomic(() =>
            PackageDependencyService().AddStandaloneDependency(run.Package, selected.Loaded, selected.Suggested));
        capture.Publish(run.History, "Add dependency to package '" + selected.Name + "'");
        selectedDependencyReference = null; selectedDependencyPackage = null;
        ClearAssetReferenceState(); ClearPackagePropertiesState();
        closePackageDependencyPicker = true; ClearStandaloneDependencyAddition();
        selectedPackageDependency = null; packageDependencyCandidates = [];
    }
    private void ClearStandaloneDependencyAddition()
    {
        if (standaloneDependencyAddition is not { } run) return;
        standaloneDependencyAddition = null;
        try { run.Release(); }
        catch { if (!packageCommandCleanup.Contains(run.Release)) packageCommandCleanup.Add(run.Release); throw; }
    }
}
