using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

/// <summary>Actual canonical inbound dependencies, including transitive importer consumers.</summary>
internal static class AuthoredImportDependencyClosure
{
    public static AssetId[] Find(ProjectWorkspace workspace, EditorAuthoringRegistry? registry, AssetId root)
    {
        var items = workspace.Session.Packages.SelectMany(package => package.Assets).ToArray();
        var references = items.ToDictionary(item => item.Id, item => AssetReferenceAnalysis.Visit(
            registry?.Find(item.Id)?.Document is IAssetTagDocument owner ? owner.AuthoredAsset : item.Asset)
            .Select(link => link.Reference).OfType<IReference>().Select(reference => reference.Id).ToHashSet());
        var included = new HashSet<AssetId> { root };
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items)
                if (!included.Contains(item.Id) && references[item.Id].Overlaps(included))
                { included.Add(item.Id); changed = true; }
        } while (changed);
        return included.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    }
}
