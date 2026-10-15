using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class PickerCreationScope(ProjectWorkspace workspace, EditorSessionHistory history, AssetBrowserState browser,
        AssetBrowserFolder folder, ContentReferenceEditCertificate retention, Func<bool> current, Action<bool> completed)
    {
        internal ProjectWorkspace Workspace { get; } = workspace;
        internal EditorSessionHistory History { get; } = history;
        internal AssetBrowserState Browser { get; } = browser;
        internal AssetBrowserFolder Folder { get; } = folder;
        internal string PackagePath { get; } = folder.Package.FullPath.ToOSPath();
        internal string? Namespace { get; } = folder.Package.Container?.AssetNamespace ?? folder.Package.AssetNamespace;
        internal ContentReferenceEditCertificate Retention { get; } = retention;
        internal Func<bool> Current { get; } = current;
        internal Action<bool> Completed { get; } = completed;
        internal string Identity { get; } = Guid.NewGuid().ToString("N");
        internal PickerCreationScope? Parent { get; init; }
        internal AssetCreationPanel? Panel;
        internal bool Published;
    }
    private PickerCreationScope? pickerCreationScope;
    private readonly List<ContentReferenceEditCertificate> retiredPickerCreationCertificates = [];
    private AssetBrowserState AssetCreationBrowser => pickerCreationScope?.Browser ?? assetBrowser;
    private bool PickerCreationCurrent => pickerCreationScope is not { } scope || ReferenceEquals(workspace, scope.Workspace)
        && ReferenceEquals(sessionHistory, scope.History) && scope.Current() && scope.Retention.IsCurrent()
        && IsCurrentEditableAssetPackage(scope.Folder.Package)
        && SamePath(scope.Folder.Package.FullPath.ToOSPath(), scope.PackagePath)
        && (scope.Folder.Package.Container?.AssetNamespace ?? scope.Folder.Package.AssetNamespace) == scope.Namespace
        && assetBrowser.FindFolder(scope.Folder.Id) is { } folder && ReferenceEquals(folder.Package, scope.Folder.Package)
        && folder.RelativePath == scope.Folder.RelativePath && folder.IsCode == scope.Folder.IsCode;

    private void RequestPickerSourceImport(string[] files, AssetBrowserFolder folder, AssetBrowserState browser,
        ContentReferenceEditCertificate retention, Func<bool> current, Action<bool> completed)
    {
        if (IsAssetImportUpdating || !assetImportProgressRetired || resourceCopyRequest != null || pendingResourceCopies != null
            || assetDropTemplatePanel?.HasRequest == true || assetImportOptionsPanel?.HasRequest == true
            || pickerCreationScope is { } active && ReferenceEquals(active.Browser, browser))
        { RetirePickerCreationCertificate(retention); completed(false); return; }
        var owner = workspace;
        if (owner == null || sessionHistory == null) { RetirePickerCreationCertificate(retention); completed(false); return; }
        pickerCreationScope = new(owner, sessionHistory, browser, folder, retention, current, completed) { Parent = pickerCreationScope };
        try
        {
            ValidatePickerCreationScope();
            if (browser.CaptureNavigationScope() is not { EntireWorkspace: false, Folders.Length: 1 }
                || browser.SelectedFolder?.Id != folder.Id) throw new InvalidOperationException("The asset import target changed.");
            RequestDroppedAssetSources(files, folder);
        }
        catch { CancelPickerAssetCreation(); throw; }
    }
    private void ValidatePickerCreationScope()
    {
        if (!PickerCreationCurrent) throw new InvalidOperationException("The asset picker import target changed.");
    }
    private void MarkPickerCreationPublished() { if (pickerCreationScope is { } scope) scope.Published = true; }
    private void PumpPickerAssetCreation()
    {
        foreach (var certificate in retiredPickerCreationCertificates.ToArray())
        {
            certificate.Dispose(); retiredPickerCreationCertificates.Remove(certificate);
        }
        if (pickerCreationScope is not { } scope) return;
        if (!scope.Published && !PickerCreationCurrent) { CancelPickerAssetCreation(); return; }
        if (IsAssetImportUpdating || !assetImportProgressRetired || resourceCopyRequest != null || pendingResourceCopies != null
            || assetDropTemplatePanel?.HasRequest == true || assetImportOptionsPanel?.HasRequest == true
            || scope.Panel?.HasRequest == true) return;
        scope.Panel?.Clear();
        pickerCreationScope = scope.Parent;
        try { scope.Completed(scope.Published); }
        finally { RetirePickerCreationCertificate(scope.Retention); }
    }
    private void RetirePickerCreationCertificate(ContentReferenceEditCertificate certificate)
    {
        try { certificate.Dispose(); }
        catch { retiredPickerCreationCertificates.Add(certificate); throw; }
    }
    private void CancelPickerAssetCreation()
    {
        if (pickerCreationScope is not { } scope) return;
        // Stop clears every actor publication callback before owner leases retire.
        // Detached workers retain only their own immutable inputs/file leases.
        StopAssetImportUpdate(); assetImportOptionsPanel?.Clear(); scope.Panel?.Clear();
        pickerCreationScope = scope.Parent;
        try { scope.Completed(false); }
        finally { RetirePickerCreationCertificate(scope.Retention); }
    }
}
