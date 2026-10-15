using Stride.Core;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? SuggestAssetTemplateName(ProjectWorkspace owner, AssetBrowserFolder initial, AssetCreationPanel.Choice choice)
    {
        if (choice.Template is not { RequireName: true } template) return choice.Template == null ? choice.Name : null;
        ValidateDroppedAssetTarget(owner, initial);
        choice.Adapter?.Validate();
        var target = ResolveAssetTemplateLocation(initial, choice.AssetType);
        var assets = target.Package.Assets.Where(item =>
        {
            var code = item.Asset is IProjectAsset && item.Package.Container is SolutionProject;
            if (code != target.IsCode) return false;
            var url = item.UnqualifiedUrl.ToString().Replace('\\', '/');
            var separator = url.LastIndexOf('/');
            var directory = separator < 0 ? "" : url[..separator];
            return string.Equals(directory, target.RelativePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        });
        return NamingHelper.ComputeNewName(template.DefaultOutputName ?? template.AssetTypeName,
            assets, item => Path.GetFileName(item.Location.ToString()), "{0}{1}");
    }
}
