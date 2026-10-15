using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private AssetDropTemplatePanel? assetDropTemplatePanel;

    private void RequestDroppedAssetTemplates(string[] files, AssetBrowserFolder folder)
    {
        var owner = workspace ?? throw new InvalidOperationException("The template session is no longer available.");
        ValidateDroppedAssetTarget(owner, folder);
        var templates = TemplateManager.FindTemplates(TemplateScope.Asset, owner.Session).OfType<TemplateAssetDescription>()
            .Where(template => template.ImportSource).OrderBy(template => template.Order).ToArray();
        var extensions = files.Select(Path.GetExtension).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(extension => extension, extension => templates.Where(template => template.GetSupportedExtensions().Contains(extension)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var families = extensions.Values.ToArray();
        var mismatch = families.Length > 1 && families.Skip(1).Any(family => !families[0].SequenceEqual(family));
        var available = new List<AssetDropTemplatePanel.Entry>();
        try
        {
            foreach (var template in templates)
            {
                var matchingFiles = files.Where(file => extensions[Path.GetExtension(file)].Contains(template)).ToArray();
                if (matchingFiles.Length == 0) continue;
                var adapter = sessionHistory!.Extensions?.AcquireAssetTemplateAdapter(template);
                var factory = adapter == null ? (template as TemplateAssetFactoryDescription)?.GetFactory() : null;
                if (adapter == null && (factory == null || typeof(SourceCodeAsset).IsAssignableFrom(factory.AssetType)
                    || typeof(IProjectAsset).IsAssignableFrom(factory.AssetType) || AssetRegistry.GetDefaultExtension(factory.AssetType) == null)) continue;
                var choice = new AssetCreationPanel.Choice(template.Name, factory, template, adapter);
                var entry = new AssetDropTemplatePanel.Entry(choice, matchingFiles); available.Add(entry);
                var target = ResolveAssetTemplateLocation(folder, choice.AssetType);
                if (adapter != null)
                {
                    adapter.Validate();
                    choice.NativeParameters = new(new AssetTemplateGeneratorParameters(new UDirectory(target.RelativePath))
                    {
                        Package = target.Package, Description = template, Namespace = AssetTemplateNamespace(target),
                        Name = template.RequireName ? template.DefaultOutputName ?? template.AssetTypeName : "",
                        OutputDirectory = target.Package.RootDirectory, Logger = new LoggerResult(), Unattended = false,
                    }, AssetCreationBrowser.SelectedEntries.Select(selected => CanonicalTemplateSnapshot(owner.FindAsset(selected.Id))).ToArray(), CanonicalTemplateSnapshot);
                    choice.NativeParameters.Parameters.SourceFiles.AddRange(matchingFiles.Select(file => new UFile(file)));
                }
            }
            if (available.Count == 0) throw new InvalidOperationException("These files aren't supported.");
            void Run(AssetDropTemplatePanel.Entry selected) => RunDroppedAssetTemplate(owner, folder, selected);
            var transferred = available.ToArray(); available.Clear();
            if (!mismatch && transferred.Length == 1) Run(transferred[0]);
            else (assetDropTemplatePanel ??= new() { RetireProviderLease = RetireTemplateReferenceLease }).Open(transferred, mismatch ? files.Length : 0, Run);
        }
        catch
        {
            foreach (var entry in available) if (entry.Choice.Adapter is { } lease) RetireTemplateReferenceLease(lease);
            throw;
        }
    }

    private AssetBrowserFolder ResolveAssetTemplateLocation(AssetBrowserFolder initial, Type assetType)
    {
        var isCode = typeof(IProjectAsset).IsAssignableFrom(assetType);
        bool Supports(AssetBrowserFolder folder) => folder.IsCode == isCode && IsCurrentEditableAssetPackage(folder.Package)
            && (!isCode || folder.Package.Container is SolutionProject);
        if (Supports(initial)) return initial;
        // Native FindValidCreationLocation first tries the current project's Assets mount.
        if (!isCode && assetBrowser.Packages.FirstOrDefault(root => ReferenceEquals(root.Package, workspace!.Package)) is { } current
            && current.Children.FirstOrDefault(mount => !mount.IsCode && Supports(mount)) is { } currentAssets) return currentAssets;
        var packageRoot = assetBrowser.Packages.FirstOrDefault(root => ReferenceEquals(root.Package, initial.Package));
        return packageRoot?.Children.FirstOrDefault(Supports)
            ?? throw new InvalidOperationException("The template target is not available.");
    }

    private static string AssetTemplateNamespace(AssetBrowserFolder target)
    {
        var root = target.Package.RootNamespace ?? target.Package.Meta.Name ?? "";
        return target.IsCode && target.RelativePath.Length != 0 ? root + "." + target.RelativePath.Replace('/', '.').Replace('\\', '.') : root;
    }

    private AssetItem CanonicalTemplateSnapshot(AssetItem item)
    {
        var asset = authoring?.Find(item.Id)?.Document is IAssetTagDocument canonical ? canonical.AuthoredAsset : item.Asset;
        return item.Clone(true, newAsset: AssetCloner.Clone(asset));
    }

    private void ValidateDroppedAssetTarget(ProjectWorkspace owner, AssetBrowserFolder folder)
    {
        if (!ReferenceEquals(workspace, owner) || !CanAddAsset || !IsCurrentEditableAssetPackage(folder.Package)
            || assetBrowser.FindFolder(folder.Id) is not { } current || !ReferenceEquals(current.Package, folder.Package)
            || current.RelativePath != folder.RelativePath || current.IsCode != folder.IsCode)
            throw new InvalidOperationException("The template target changed.");
    }

    private void RunDroppedAssetTemplate(ProjectWorkspace owner, AssetBrowserFolder folder, AssetDropTemplatePanel.Entry entry)
    {
        var choice = entry.Choice;
        var panelOwnsLease = false;
        var retired = false;
        try
        {
        ValidateDroppedAssetTarget(owner, folder);
        if (!AssetRegistry.GetPublicTypes().Contains(choice.AssetType)) throw new InvalidOperationException("The template asset type is no longer available.");
        foreach (var file in entry.Files) if (!File.Exists(file)) throw new FileNotFoundException("The source file isn't available.", file);
        var target = ResolveAssetTemplateLocation(folder, choice.AssetType);
        if (choice.NativeParameters is { } prepared && !ReferenceEquals(prepared.Parameters.Package, target.Package))
            throw new InvalidOperationException("The template target changed.");
        var name = choice.Template!.RequireName ? choice.Template.DefaultOutputName ?? choice.Template.AssetTypeName : "";
        if (choice.Adapter != null)
        {
            var sourceNamespace = AssetTemplateNamespace(target);
            if (choice.Adapter.Adapter.DrawParameters != null)
            {
                panelOwnsLease = true;
                AssetCreation.OpenPreparedTemplate(owner.Session, choice, sourceNamespace, (selected, selectedName, _, _, selectedNamespace) =>
                {
                    ValidateDroppedAssetTarget(owner, folder);
                    var resolved = ResolveAssetTemplateLocation(folder, selected.AssetType);
                    if (resolved.Id != target.Id || !ReferenceEquals(resolved.Package, target.Package))
                        throw new InvalidOperationException("The template target changed.");
                    CreateRegisteredTemplateAsset(target.Package, target.RelativePath, selected, selectedName, selectedNamespace, false, entry.Files);
                });
                return;
            }
            try { CreateRegisteredTemplateAsset(target.Package, target.RelativePath, choice, name, sourceNamespace, false, entry.Files); }
            finally { RetireTemplateReferenceLease(choice.Adapter); retired = true; }
        }
        else CreateFactoryAssetsFromSources(target.Package, target.RelativePath, choice, name.Length == 0 ? choice.Template.DefaultOutputName ?? choice.Template.AssetTypeName : name, entry.Files);
        }
        catch
        {
            if (!panelOwnsLease && !retired && choice.Adapter != null) RetireTemplateReferenceLease(choice.Adapter);
            throw;
        }
    }
}
