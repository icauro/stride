using Stride.Core.Assets;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Graph;
using MicrosoftProjectCollection = Microsoft.Build.Evaluation.ProjectCollection;

namespace Stride.Editor.ImGui;

internal static partial class AuthoredProjectCompilationResolver
{
    private static ProjectRootElement CreateRuntimeRoot(ProjectRootElement authoredRoot, ProjectGraphNode node,
        IReadOnlyDictionary<(string Path, string Framework), string> compiledOutputs, MicrosoftProjectCollection collection)
    {
        using var reader = XmlReader.Create(new StringReader(authoredRoot.RawXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var root = ProjectRootElement.Create(reader, collection, preserveFormatting: true);
        root.FullPath = authoredRoot.FullPath;
        // ResolveProjectReferences still asks saved child projects for target
        // paths when BuildProjectReferences=false. Replace those SDK results
        // with the outputs of the exact native graph nodes already built.
        var target = root.AddTarget("_StrideImGuiAuthoredProjectOutputs_" + Guid.NewGuid().ToString("N"));
        target.AfterTargets = "ResolveProjectReferences";
        foreach (var reference in node.ProjectReferences)
        {
            var projectPath = Path.GetFullPath(reference.ProjectInstance.FullPath);
            var authoredItem = node.ProjectInstance.GetItems("ProjectReference").FirstOrDefault(item => SamePath(item.GetMetadataValue("FullPath"), projectPath));
            if (authoredItem == null) continue;
            var outputItem = authoredItem.GetMetadataValue("OutputItemType");
            var assemblyReference = !string.Equals(authoredItem.GetMetadataValue("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase);
            if (!assemblyReference && outputItem.Length == 0) continue;
            if (!compiledOutputs.TryGetValue((projectPath.ToUpperInvariant(), reference.ProjectInstance.GetPropertyValue("TargetFramework")), out var assemblyPath))
                throw new InvalidOperationException("A referenced native project target has not produced its private assembly.");
            foreach (var itemType in (assemblyReference ? new[] { "_ResolvedProjectReferencePaths" } : Array.Empty<string>()).Concat(outputItem.Length == 0 ? Array.Empty<string>() : new[] { outputItem }).Distinct(StringComparer.Ordinal))
            {
                var group = target.AddItemGroup();
                var remove = root.CreateItemElement(itemType);
                remove.Remove = "@(" + itemType + ")";
                remove.Condition = "'%(" + itemType + ".MSBuildSourceProjectFile)' == '" + MicrosoftProjectCollection.Escape(projectPath) + "'";
                group.AppendChild(remove);
                var item = group.AddItem(itemType, MicrosoftProjectCollection.Escape(assemblyPath));
                foreach (var metadata in authoredItem.Metadata.Where(metadata => metadata.Name is not "MSBuildSourceProjectFile" and not "OriginalItemSpec" and not "OriginalProjectReferenceItemSpec" and not "ReferenceSourceTarget"))
                    item.AddMetadata(metadata.Name, MicrosoftProjectCollection.Escape(metadata.EvaluatedValue));
                item.AddMetadata("MSBuildSourceProjectFile", MicrosoftProjectCollection.Escape(projectPath));
                item.AddMetadata("OriginalItemSpec", MicrosoftProjectCollection.Escape(authoredItem.EvaluatedInclude));
                item.AddMetadata("OriginalProjectReferenceItemSpec", MicrosoftProjectCollection.Escape(authoredItem.EvaluatedInclude));
                item.AddMetadata("ReferenceSourceTarget", "ProjectReference");
            }
        }
        return root;
    }
}
