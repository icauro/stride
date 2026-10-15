using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectBuildSettingsDocument
{
    public void ValidateProjectFileBaseline()
    {
        if (!CanSave || (File.GetAttributes(Path) & FileAttributes.ReadOnly) != 0)
            throw new InvalidOperationException("This project file can't be edited.");
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!System.Security.Cryptography.SHA256.HashData(stream).AsSpan().SequenceEqual(savedDiskHash))
            throw new InvalidOperationException("The project file changed outside the editor.");
    }
    public IReadOnlyList<ProjectCompileItemOverride> AuthoredCompileItems => properties.AuthoredCompileItems;
    public string[] GetProjectItemPaths(string itemType) => properties.GetProjectItemPaths(itemType);
    public ProjectDependencyEvaluation[] EvaluateDependencyContexts(string configuration) => properties.EvaluateDependencyContexts(configuration);
    public ProjectEvaluatedBuildInputs EvaluateBuildInputs(string configuration, string framework) => properties.EvaluateBuildInputs(configuration, framework);
    public bool IsProjectItemIncluded(string itemType, string include, string configuration, string framework) => properties.IsProjectItemIncluded(itemType, include, configuration, framework);
    public bool IsProjectItemIncluded(string itemType, string include) => properties.IsProjectItemIncluded(itemType, include);
    public void ValidateProjectAssetMembership(AssetItem asset, bool adding)
    {
        // Package.Clone preserves FullPath but intentionally detaches Container.
        // Core removal supplies that saved snapshot, not the live package object.
        if (asset.Package == null || asset.Package.FullPath == null || asset.Package.FullPath != project.Package.FullPath || asset.Asset is not IProjectAsset)
            throw new InvalidOperationException("The source asset does not belong to this project.");
        var include = System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(Path)!, asset.FullPath.ToOSPath());
        if (asset.Asset is not IProjectFileGeneratorAsset generator)
        {
            if (IsProjectItemIncluded(ProjectSourceItemType(asset), include) != adding)
                throw new InvalidOperationException("The project source membership has not been authored.");
            return;
        }
        var sourceType = IsProjectItemIncluded("None", include) ? "None" : "AdditionalFiles";
        var sourceIncluded = IsProjectItemIncluded(sourceType, include);
        var generated = include + ".cs";
        var generatedIncluded = IsProjectItemIncluded("Compile", generated);
        if (!adding)
        {
            if (sourceIncluded || generatedIncluded)
                throw new InvalidOperationException("The generated source membership has not been authored.");
            return;
        }
        if (!sourceIncluded || !generatedIncluded
            || properties.GetProjectItemMetadata(sourceType, include, "Generator") != generator.Generator
            || properties.GetProjectItemMetadata(sourceType, include, "LastGenOutput") != System.IO.Path.GetFileName(generated)
            || properties.GetProjectItemMetadata("Compile", generated, "DependentUpon") != System.IO.Path.GetFileName(include)
            || !bool.TryParse(properties.GetProjectItemMetadata("Compile", generated, "AutoGen"), out var autoGen) || !autoGen
            || !bool.TryParse(properties.GetProjectItemMetadata("Compile", generated, "DesignTime"), out var designTime) || !designTime
            || !bool.TryParse(properties.GetProjectItemMetadata("Compile", generated, "DesignTimeSharedInput"), out var shared) || !shared)
            throw new InvalidOperationException("The generated source project metadata has not been authored.");
    }
    internal string ProjectSourceItemType(AssetItem item)
    {
        var include = item.GetProjectInclude();
        if (item.Asset is Stride.Assets.Scripts.ScriptSourceFileAsset) return "Compile";
        if (IsProjectItemIncluded("AdditionalFiles", include)) return "AdditionalFiles";
        if (IsProjectItemIncluded("None", include)) return "None";
        if (IsProjectItemIncluded("Compile", include)) return "Compile";
        return "AdditionalFiles";
    }
    internal void ChangeProjectSourceMembership(AssetItem item, bool included)
        => ChangeProjectItem(ProjectSourceItemType(item), item.GetProjectInclude(), included);
    internal void RelocateProjectItem(string itemType, string beforeInclude, string afterInclude)
    {
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        var before = properties.Snapshot; var beforeRevision = revision;
        try
        {
            properties.RelocateProjectItem(itemType, beforeInclude, afterInclude);
            var after = properties.Snapshot;
            if (before == after) return;
            revision = ++nextRevision;
            using var transaction = history.CreateTransaction("Move project item");
            history.Record(new BuildOperation(this, before, after, beforeRevision, revision));
        }
        catch { Apply(before, beforeRevision); throw; }
    }
    public void ChangeProjectItem(string itemType, string include, bool included)
    {
        if (!CanSave) throw new InvalidOperationException("This package can't be edited.");
        var before = properties.Snapshot;
        var beforeRevision = revision;
        try
        {
            properties.ChangeProjectItem(itemType, include, included);
            var after = properties.Snapshot;
            if (before == after) return;
            var afterRevision = ++nextRevision;
            revision = afterRevision;
            using var transaction = history.CreateTransaction(included ? "Add source file" : "Remove source file");
            history.Record(new BuildOperation(this, before, after, beforeRevision, afterRevision));
        }
        catch { Apply(before, beforeRevision); throw; }
    }
}
