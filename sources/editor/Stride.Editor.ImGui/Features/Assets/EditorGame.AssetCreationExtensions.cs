using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void CreateRegisteredTemplateAsset(Package package, string parent, AssetCreationPanel.Choice choice,
        string name, string sourceNamespace, bool selectSource, string[]? sourceFiles = null, bool resourceSourcesPrepared = false)
    {
        var lease = choice.Adapter ?? throw new InvalidOperationException("This template has no registered preparation adapter.");
        lease.Validate();
        var parameters = choice.NativeParameters?.Parameters ?? throw new InvalidOperationException("This template has no native parameters.");
        var owner = workspace!;
        if (!CanAddAsset || !IsCurrentEditableAssetPackage(package) || !ReferenceEquals(package.Session, owner.Session)
            || !ReferenceEquals(parameters.Package, package) || !lease.Adapter.Generator.IsSupportingTemplate(parameters.Description))
            throw new InvalidOperationException("The template target changed.");
        if (choice.Template?.RequireName != false && !IsValidAssetLeafName(name)) throw new ArgumentException("Invalid asset name.");
        parameters.Name = name; parameters.Namespace = sourceNamespace;
        if (sourceFiles != null)
        { parameters.SourceFiles.Clear(); parameters.SourceFiles.AddRange(sourceFiles.Select(file => new UFile(Path.GetFullPath(file)))); }
        if (selectSource)
        {
            var files = PickAssetTemplateSources(package, choice.Template!.GetSupportedExtensions());
            if (files.Length == 0)
            {
                AssetCreation.RequestWithoutSourcePrompt(DisplayAttribute.GetDisplayName(choice.AssetType) ?? choice.AssetType.Name, retry =>
                {
                    if (!ReferenceEquals(workspace, owner)) throw new InvalidOperationException("The template session changed.");
                    if (!retry) parameters.SourceFiles.Clear();
                    CreateRegisteredTemplateAsset(package, parent, choice, name, sourceNamespace, retry);
                });
                return;
            }
            parameters.SourceFiles.Clear(); parameters.SourceFiles.AddRange(files.Select(file => new UFile(Path.GetFullPath(file))));
        }
        if (!ReferenceEquals(workspace, owner) || !CanAddAsset || !IsCurrentEditableAssetPackage(package) || !ReferenceEquals(package.Session, owner.Session))
            throw new InvalidOperationException("The template target changed.");
        if (!resourceSourcesPrepared && parameters.SourceFiles.Count > 0)
        {
            var retained = lease.Retain();
            var retainedChoice = choice with { Adapter = retained };
            if (RequestResourceSourceCopies(parameters.SourceFiles.Select(file => file.ToOSPath()).ToArray(), package, parent,
                copied => CreateRegisteredTemplateAsset(package, parent, retainedChoice, name, sourceNamespace, false, copied, true), retained)) return;
        }
        documents.CommitAll(); parameters.Validate();
        AssetItem[] outputs = [];
        using (var preparation = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            lease.Validate();
            var values = lease.Adapter.PrepareAssets(parameters)?.ToArray() ?? throw new InvalidOperationException("The template returned no assets.");
            if (parameters.Logger.HasErrors) throw new InvalidOperationException(parameters.Logger.ToText());
            if (values.Any(item => item == null)) throw new InvalidOperationException("The template returned an empty asset item.");
            if (values.Select(item => item.Id).Distinct().Count() != values.Length) throw new InvalidOperationException("The template returned duplicate native asset identities.");
            if (values.Any(item => owner.Session.Packages.Any(candidate => candidate.Assets.Contains(item)
                || candidate.Assets.Any(existing => ReferenceEquals(existing.Asset, item.Asset)))
                || authoring!.RetainedStates.Any(state => state.Document is IAssetTagDocument document && ReferenceEquals(document.AuthoredAsset, item.Asset))))
                throw new InvalidOperationException("A template must return detached authored assets.");
            outputs = values.Select(item => item.Clone(true, newAsset: AssetCloner.Clone(item.Asset))).ToArray();
        }))
        {
            if (preparation.Entries.Count != 0)
            {
                sessionHistory.RestoreFailedCapture(preparation);
                throw new InvalidOperationException("A template must prepare detached assets without changing canonical owners.");
            }
        }
        if (outputs.Length == 0)
        {
            CancelPendingResourceCopies();
            parameters.Logger.Info("Nothing to import.");
            ReportRegisteredTemplateMessages(parameters);
            return;
        }
        var prepared = outputs.Select(item => new PreparedFactoryAsset(item, RegisteredTemplateDestination(package, item))).ToArray();
        RequestFactorySourcePublication(package, prepared, "Create asset '" + name + "'", () => PublishRegisteredTemplateAssets(package, outputs), lease, () =>
        {
            ReportRegisteredTemplateMessages(parameters);
            if (parameters.RequestSessionSave) RequestPackageMetadataSave(saveDocuments: true);
        });
    }
    private void ReportRegisteredTemplateMessages(Stride.Core.Assets.Templates.AssetTemplateGeneratorParameters parameters)
    {
        foreach (var message in parameters.Logger.Messages)
            outputLog.Append("Template", message.Type, message.Module, message.Text, message.ExceptionInfo?.ToString());
    }
    private string RegisteredTemplateDestination(Package package, AssetItem item)
    {
        var relative = NativeAssetUrls.Unqualify(package, item.Location).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new InvalidOperationException("The template returned an invalid asset location.");
        var source = item.Asset is IProjectAsset ? package.RootDirectory : package.GetDefaultAssetFolder();
        var root = Path.GetFullPath((source.IsAbsolute ? source : UPath.Combine(package.RootDirectory, source)).ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extension = AssetRegistry.GetDefaultExtension(item.Asset.GetType()) ?? throw new InvalidOperationException("The generated asset has no native serializer extension.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar) + extension));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The generated asset is outside its package.");
        return path;
    }

    private void PublishRegisteredTemplateAssets(Package package, AssetItem[] outputs)
    {
        string Relative(UFile location)
        {
            var value = NativeAssetUrls.Unqualify(package, location).Replace('\\', '/');
            if (Path.IsPathRooted(value) || value.Split('/').Any(part => !IsValidAssetLeafName(part))) throw new InvalidOperationException("The template returned an invalid asset location.");
            return value;
        }
        string Destination(UFile location, Type type)
        {
            var source = typeof(IProjectAsset).IsAssignableFrom(type) ? package.RootDirectory : package.GetDefaultAssetFolder();
            var root = Path.GetFullPath((source.IsAbsolute ? source : UPath.Combine(package.RootDirectory, source)).ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var extension = AssetRegistry.GetDefaultExtension(type) ?? throw new InvalidOperationException("The generated asset has no native serializer extension.");
            var path = Path.GetFullPath(Path.Combine(root, Relative(location).Replace('/', Path.DirectorySeparatorChar) + extension));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The generated asset is outside its package.");
            return path;
        }
        var items = outputs.Select(item => item.CloneForPackage(package, new UFile(Relative(item.Location)), item.Asset)).ToList();
        if (items.Any(item => item.Asset is IProjectAsset && package.Container is not SolutionProject
            || item.Asset is IProjectAsset && item.Asset is not SourceCodeAsset && item.Asset is not Stride.Assets.Scripts.VisualScriptAsset))
            throw new InvalidOperationException("This generated project asset requires its native project topology adapter.");
        var types = items.Select(item => item.Asset.GetType()).Distinct().ToArray();
        var resolver = new AssetResolver(location => package.Assets.Find(location) != null || types.Any(type =>
        {
            var path = Destination(location, type); return File.Exists(path) || Directory.Exists(path) || authoring!.Find(path) != null;
        }), id => workspace!.Session.FindAsset(id) != null || authoring!.Find(id) != null);
        var prepared = new List<AssetItem>(); AssetCollision.Clean(null!, items, prepared, resolver, false, false);
        var sources = prepared.Where(item => item.Asset is SourceCodeAsset).ToArray();
        var serialized = prepared.Where(item => item.Asset is not SourceCodeAsset).ToArray();
        var reserved = serialized.Select(item => item.Location.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceMap = new Dictionary<AssetId, AssetItem>();
        var packageRoot = assetBrowser.Packages.First(root => ReferenceEquals(root.Package, package));
        foreach (var group in sources.GroupBy(item => item.Asset is IProjectAsset))
        {
            var originals = group.ToArray(); var ids = originals.Select(item => item.Id).ToArray();
            var unqualified = originals.Select(item => item.Clone(false, new UFile(Relative(item.Location)), item.Asset)).ToArray();
            var published = PasteSourceAssets(unqualified, packageRoot.Children.First(mount => mount.IsCode == group.Key), reserved);
            for (var index = 0; index < ids.Length; index++) sourceMap.Add(ids[index], published[index]);
        }
        var locations = serialized.ToDictionary(item => item.Id, item => item.Location.ToString());
        foreach (var item in serialized)
        {
            foreach (var link in AssetReferenceAnalysis.Visit(item.Asset))
                if (link.Reference is IReference reference)
                {
                    if (sourceMap.TryGetValue(reference.Id, out var source)) link.UpdateReference(source.Id, source.Location.ToString());
                    else if (locations.TryGetValue(reference.Id, out var next)) link.UpdateReference(reference.Id, next);
                    else if (workspace!.Session.FindAsset(reference.Id) is { } existing) link.UpdateReference(existing.Id, existing.Location.ToString());
                    else link.UpdateReference(AssetId.Empty, null);
                }
            CreatePreparedAsset(package, item, Destination(item.Location, item.Asset.GetType()), "Create assets");
        }
        var selected = serialized.Select(item => item.Id).Concat(sourceMap.Values.Select(item => item.Id)).ToArray();
        SelectCreatedAssetBatch(selected);
    }
}
