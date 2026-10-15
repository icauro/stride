using Stride.Core.Assets;
namespace Stride.Editor.ImGui;

internal sealed record ProjectCompilerReferenceChange(string Kind, string Path, bool Included, string AssemblyIdentity,
    string[] Aliases, bool EmbedInteropTypes);

internal sealed partial class AuthoredProjectProperties
{
    public void ChangeCompilerReference(ProjectCompilerReferenceChange change, string configuration, string framework)
    {
        if (projectPath == null || change.Kind is not ("Reference" or "Analyzer"))
            throw new InvalidOperationException("A native project/compiler reference is required.");
        if (EvaluateDependencyContexts(configuration).Length > 1)
            throw new InvalidOperationException("The compiler reference requires an exact authored target-framework scope.");
        var directory = Path.GetDirectoryName(projectPath)!;
        var path = Path.GetFullPath(change.Path);
        var evaluated = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, framework), null, collection);
        try
        {
            var matches = evaluated.GetItems(change.Kind).Where(item =>
            {
                var value = change.Kind == "Reference" ? item.GetMetadataValue("HintPath") : item.EvaluatedInclude;
                return value.Length > 0 && string.Equals(Path.GetFullPath(value, directory), path, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (change.Included && matches.Length > 0) throw new InvalidOperationException("The compiler reference is already authored or imported.");
            if (!change.Included)
            {
                if (matches.Length != 1) throw new InvalidOperationException("The compiler reference has no unique authored project item.");
                var item = matches[0];
                if (!ReferenceEquals(item.Xml.ContainingProject, root) || !string.IsNullOrWhiteSpace(item.Xml.Condition)
                    || !string.IsNullOrWhiteSpace(item.Xml.Parent.Condition)
                    || item.Xml.Include.IndexOfAny(['*', '?', ';', '$', '%', '@']) >= 0)
                    throw new InvalidOperationException("The compiler reference is conditional or owned by an import/package.");
                var aliases = item.GetMetadataValue("Aliases").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (change.Kind == "Reference" && (!(aliases.Length == 0 ? new[] { "global" } : aliases).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(change.Aliases.Length == 0 ? new[] { "global" } : change.Aliases)
                    || (bool.TryParse(item.GetMetadataValue("EmbedInteropTypes"), out var embed) && embed) != change.EmbedInteropTypes))
                    throw new InvalidOperationException("The compiler reference properties changed.");
                item.Xml.Parent.RemoveChild(item.Xml);
                return;
            }
            if (!File.Exists(path)) throw new IOException("The real compiler reference file is unavailable.");
            var group = root.CreateItemGroupElement(); root.AppendChild(group);
            var added = root.CreateItemElement(change.Kind); group.AppendChild(added);
            var relative = Path.GetRelativePath(directory, path);
            if (change.Kind == "Analyzer") added.Include = relative;
            else
            {
                if (string.IsNullOrWhiteSpace(change.AssemblyIdentity)) throw new InvalidOperationException("The assembly reference has no native identity.");
                added.Include = change.AssemblyIdentity;
                added.AddMetadata("HintPath", relative);
                if (change.Aliases.Length > 0) added.AddMetadata("Aliases", string.Join(',', change.Aliases));
                if (change.EmbedInteropTypes) added.AddMetadata("EmbedInteropTypes", "true");
            }
        }
        finally { collection.UnloadProject(evaluated); }
    }
}
