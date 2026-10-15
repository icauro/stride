using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    internal void ChangeGeneratedAssetMembership(AssetItem item, bool included)
    {
        if (item.Asset is not IProjectFileGeneratorAsset generator || !CanSave) throw new InvalidOperationException("This project asset has no native generator.");
        var before = properties.Snapshot; var beforeRevision = revision;
        try
        {
            var include = item.GetProjectInclude(); var generated = item.GetGeneratedInclude();
            var sourceType = properties.IsProjectItemIncluded("AdditionalFiles", include) ? "AdditionalFiles" : "None";
            properties.ChangeProjectItem(sourceType, include, included);
            properties.ChangeProjectItem("Compile", generated, included);
            if (included)
            {
                properties.ChangeProjectItemMetadata(sourceType, include, new Dictionary<string, string>
                { ["Generator"] = generator.Generator, ["LastGenOutput"] = System.IO.Path.GetFileName(generated) });
                properties.ChangeProjectItemMetadata("Compile", generated, new Dictionary<string, string>
                {
                    ["DependentUpon"] = System.IO.Path.GetFileName(include), ["AutoGen"] = "True",
                    ["DesignTime"] = "True", ["DesignTimeSharedInput"] = "True",
                });
            }
            var after = properties.Snapshot;
            if (before == after) return;
            revision = ++nextRevision;
            history.Record(new BuildOperation(this, before, after, beforeRevision, revision));
        }
        catch { Apply(before, beforeRevision); throw; }
    }
}
