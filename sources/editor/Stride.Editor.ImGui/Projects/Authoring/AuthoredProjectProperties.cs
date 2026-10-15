using Stride.Core;
using Stride.Core.Assets;
using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using MicrosoftProjectCollection = Microsoft.Build.Evaluation.ProjectCollection;
using Stride.Core.IO;

namespace Stride.Editor.ImGui;

/// <summary>A retained real MSBuild project construction graph for explicit property authoring.</summary>
internal sealed partial class AuthoredProjectProperties : IDisposable
{
    private readonly MicrosoftProjectCollection collection = new();
    private ProjectRootElement root = null!;
    private Encoding encoding = new UTF8Encoding(false);
    private readonly string? projectPath;
    public AuthoredProjectProperties(byte[] contents, string? projectPath = null)
    {
        this.projectPath = projectPath == null ? null : Path.GetFullPath(projectPath);
        Load(contents);
    }
    public IReadOnlyList<ProjectCompileItemOverride> AuthoredCompileItems => root.Items
        .Where(item => item.ItemType == "Compile" && string.IsNullOrWhiteSpace(item.Condition) && string.IsNullOrWhiteSpace(item.Parent.Condition))
        .SelectMany(item => new[] { new ProjectCompileItemOverride(item.Include, true), new ProjectCompileItemOverride(item.Remove, false) })
        .Where(item => !string.IsNullOrEmpty(item.Include) && item.Include.IndexOfAny(['*', '?', ';', '$', '%', '@']) < 0)
        .ToArray();
    private string ValidateItemPath(string include)
    {
        if (projectPath == null) throw new InvalidOperationException("The project path is required for item authoring.");
        if (string.IsNullOrWhiteSpace(include) || Path.IsPathRooted(include) || include.IndexOfAny(['*', '?', ';', '$', '%', '@']) >= 0)
            throw new InvalidOperationException("A literal relative project item path is required.");
        var folder = Path.GetDirectoryName(projectPath)!;
        var absolute = Path.GetFullPath(include.Replace('\\', Path.DirectorySeparatorChar), folder);
        if (!absolute.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The item must be inside the owning project directory.");
        return absolute;
    }
    private static Dictionary<string, string> EvaluationProperties(string? configuration, string? framework)
    {
        var properties = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(configuration)) properties.Add("Configuration", configuration);
        if (!string.IsNullOrEmpty(framework)) properties.Add("TargetFramework", framework);
        return properties;
    }
    public bool IsProjectItemIncluded(string itemType, string include, string? configuration = null, string? framework = null)
    {
        var absolute = ValidateItemPath(include);
        var evaluated = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, framework), null, collection);
        try
        {
            return evaluated.GetItems(itemType).Any(item => string.Equals(Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!), absolute, StringComparison.OrdinalIgnoreCase))
                || evaluated.GetAllGlobs(itemType).Any(glob => glob.MsBuildGlob.IsMatch(absolute));
        }
        finally { collection.UnloadProject(evaluated); }
    }
    public string? GetProjectItemMetadata(string itemType, string include, string metadataName)
    {
        var absolute = ValidateItemPath(include);
        var evaluated = new Microsoft.Build.Evaluation.Project(root, null, null, collection);
        try
        {
            var matches = evaluated.GetItems(itemType).Where(item => string.Equals(Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!), absolute, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return null;
            return matches[0].GetMetadataValue(metadataName);
        }
        finally { collection.UnloadProject(evaluated); }
    }
    public string[] GetProjectItemPaths(string itemType, string? configuration = null, string? framework = null)
    {
        if (projectPath == null) throw new InvalidOperationException("The project path is required for item evaluation.");
        var evaluated = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, framework), null, collection);
        try
        {
            return evaluated.GetItems(itemType).Select(item => Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { collection.UnloadProject(evaluated); }
    }
    public ProjectDependencyEvaluation[] EvaluateDependencyContexts(string configuration)
    {
        if (projectPath == null) throw new InvalidOperationException("The project path is required for item evaluation.");
        var outer = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, null), null, collection);
        string[] frameworks;
        try
        {
            var values = outer.GetPropertyValue("TargetFrameworks");
            if (string.IsNullOrWhiteSpace(values)) values = outer.GetPropertyValue("TargetFramework");
            frameworks = values.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (frameworks.Length == 0) frameworks = [""];
        }
        finally { collection.UnloadProject(outer); }
        return frameworks.Select(framework =>
        {
            var project = new Microsoft.Build.Evaluation.Project(root, EvaluationProperties(configuration, framework), null, collection);
            try
            {
                string[] Paths(string type) => project.GetItems(type).Select(item => Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return new ProjectDependencyEvaluation(framework, Paths("Compile"), project.GetItems("PackageReference").Select(item => item.EvaluatedInclude)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Paths("ProjectReference"),
                    project.GetItems("PackageReference").Select(item => new ProjectEvaluatedPackageReference(item.EvaluatedInclude,
                        item.GetMetadataValue("Version"), item.GetMetadataValue("VersionOverride"), item.GetMetadataValue("Aliases"),
                        item.GetMetadataValue("IncludeAssets"), item.GetMetadataValue("ExcludeAssets"))).ToArray(),
                    project.GetItems("ProjectReference").Select(item => new ProjectEvaluatedProjectReference(Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!),
                        item.GetMetadataValue("Aliases"), item.GetMetadataValue("ReferenceOutputAssembly"), item.GetMetadataValue("SetTargetFramework"), item.GetMetadataValue("AdditionalProperties"))).ToArray(),
                    new[] { "DefineConstants", "LangVersion", "Nullable", "CheckForOverflowUnderflow", "AllowUnsafeBlocks", "ImplicitUsings", "NoWarn", "WarningsAsErrors" }
                        .ToDictionary(name => name, project.GetPropertyValue));
            }
            finally { collection.UnloadProject(project); }
        }).ToArray();
    }
    public static bool SameReferenceAndCompilerContext(ProjectDependencyEvaluation first, ProjectDependencyEvaluation second)
        => first.PackageReferences.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(second.PackageReferences)
            && first.ProjectReferences.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(second.ProjectReferences)
            && first.PackageDetails.ToHashSet().SetEquals(second.PackageDetails)
            && first.ProjectDetails.ToHashSet().SetEquals(second.ProjectDetails)
            && first.CompilerProperties.Count == second.CompilerProperties.Count
            && first.CompilerProperties.All(property => second.CompilerProperties.TryGetValue(property.Key, out var value) && value == property.Value);
    public void ChangeProjectItem(string itemType, string include, bool included)
    {
        if (itemType is not ("Compile" or "None" or "AdditionalFiles" or "Folder")) throw new InvalidOperationException("This project item type can't be authored.");
        var absolute = ValidateItemPath(include);
        bool Match(string value) => !string.IsNullOrEmpty(value) && value.IndexOfAny(['*', '?', ';', '$', '%', '@']) < 0
            && string.Equals(Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!), absolute, StringComparison.OrdinalIgnoreCase);
        var local = root.Items.Where(item => item.ItemType == itemType && (Match(item.Include) || Match(item.Remove) || Match(item.Update))).ToArray();
        if (local.Any(item => !string.IsNullOrWhiteSpace(item.Condition) || !string.IsNullOrWhiteSpace(item.Parent.Condition) || item.Metadata.Any(metadata => string.Equals(metadata.Name, "Link", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Conditional or linked source items cannot be edited here.");
        var evaluated = new Microsoft.Build.Evaluation.Project(root, null, null, collection);
        try
        {
            if (evaluated.GetItems(itemType).Any(item => Match(item.EvaluatedInclude)
                && (item.HasMetadata("Link") || (!item.IsImported && (!string.IsNullOrWhiteSpace(item.Xml.Condition) || !string.IsNullOrWhiteSpace(item.Xml.Parent.Condition)))
                    || (item.IsImported && item.Xml.Include.IndexOfAny(['*', '?']) < 0))))
                throw new InvalidOperationException("Imported, conditional or linked source items cannot be edited here.");
        }
        finally { collection.UnloadProject(evaluated); }
        if (IsProjectItemIncluded(itemType, include) == included) return;
        foreach (var item in local.Where(item => included ? Match(item.Remove) : Match(item.Include))) item.Parent.RemoveChild(item);
        if (included && IsProjectItemIncluded(itemType, include)) return;
        if (included)
        {
            // A staged file may enter SDK default globs only after its first save.
            // Remove that implicit occurrence before the explicit authored item.
            var group = root.AddItemGroup();
            var removal = root.CreateItemElement(itemType); removal.Remove = include; group.AppendChild(removal);
            var addition = root.CreateItemElement(itemType); addition.Include = include; group.AppendChild(addition);
        }
        else
        {
            var item = root.CreateItemElement(itemType);
            item.Remove = include;
            root.AddItemGroup().AppendChild(item);
        }
    }
    public void RelocateProjectItem(string itemType, string beforeInclude, string afterInclude)
    {
        var beforePath = ValidateItemPath(beforeInclude); var afterPath = ValidateItemPath(afterInclude);
        bool Match(string value, string path) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['*', '?', ';', '$', '%', '@']) < 0
            && string.Equals(Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!), path, StringComparison.OrdinalIgnoreCase);
        var originals = root.Items.Where(item => item.ItemType == itemType && (Match(item.Include, beforePath) || Match(item.Update, beforePath))).ToArray();
        var copies = originals;
        ChangeProjectItem(itemType, beforeInclude, false);
        foreach (var item in originals) if (item.Parent != null) item.Parent.RemoveChild(item);
        ChangeProjectItem(itemType, afterInclude, true);
        if (copies.Length == 0) return;
        foreach (var item in root.Items.Where(item => item.ItemType == itemType && (Match(item.Include, afterPath) || Match(item.Update, afterPath))).ToArray()) item.Parent.RemoveChild(item);
        foreach (var copy in copies)
        {
            var item = root.CreateItemElement(itemType); item.DeepCopyFrom(copy);
            item.Update = ""; item.Include = afterInclude;
            root.AddItemGroup().AppendChild(item);
        }
    }
    public void TransferProjectItemFrom(AuthoredProjectProperties source, string itemType, string beforeInclude, string afterInclude)
    {
        ArgumentNullException.ThrowIfNull(source);
        var beforePath = source.ValidateItemPath(beforeInclude); var afterPath = ValidateItemPath(afterInclude);
        bool Match(string value, string path, string owner) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['*', '?', ';', '$', '%', '@']) < 0
            && string.Equals(Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(owner)!), path, StringComparison.OrdinalIgnoreCase);
        var copies = source.root.Items.Where(item => item.ItemType == itemType && (Match(item.Include, beforePath, source.projectPath!) || Match(item.Update, beforePath, source.projectPath!))).ToArray();
        source.ChangeProjectItem(itemType, beforeInclude, false);
        foreach (var item in copies) if (item.Parent != null) item.Parent.RemoveChild(item);
        ChangeProjectItem(itemType, afterInclude, true);
        if (copies.Length == 0) return;
        foreach (var item in root.Items.Where(item => item.ItemType == itemType && (Match(item.Include, afterPath, projectPath!) || Match(item.Update, afterPath, projectPath!))).ToArray()) item.Parent.RemoveChild(item);
        foreach (var copy in copies)
        {
            var item = root.CreateItemElement(itemType); item.DeepCopyFrom(copy);
            item.Update = ""; item.Include = afterInclude; root.AddItemGroup().AppendChild(item);
        }
    }
    public string Snapshot => root.RawXml;
    public void ChangeProjectItemMetadata(string itemType, string include, IReadOnlyDictionary<string, string> metadata)
    {
        var absolute = ValidateItemPath(include);
        if (itemType is not ("Compile" or "None" or "AdditionalFiles" or "Folder")) throw new InvalidOperationException("This project item type can't be authored.");
        bool Match(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['*', '?', ';', '$', '%', '@']) < 0
            && string.Equals(Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!), absolute, StringComparison.OrdinalIgnoreCase);
        var items = root.Items.Where(item => item.ItemType == itemType && (Match(item.Include) || Match(item.Update))).ToArray();
        if (items.Any(item => !string.IsNullOrWhiteSpace(item.Condition) || !string.IsNullOrWhiteSpace(item.Parent.Condition)
            || item.Metadata.Any(entry => !string.IsNullOrWhiteSpace(entry.Condition) || entry.Name.Equals("Link", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Conditional or linked project metadata cannot be edited here.");
        var item = items.LastOrDefault();
        if (item == null)
        {
            item = root.CreateItemElement(itemType); item.Update = include; root.AddItemGroup().AppendChild(item);
        }
        foreach (var pair in metadata)
        {
            XmlConvert.VerifyName(pair.Key);
            foreach (var previous in item.Metadata.Where(entry => entry.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray()) item.RemoveChild(previous);
            item.AddMetadata(pair.Key, pair.Value);
        }
    }
    public string? Get(string name) => root.Properties.LastOrDefault(property => Matches(property, name))?.Value;
    public void Set(string name, string? value)
    {
        var existing = root.Properties.Where(property => Matches(property, name)).ToArray();
        if (string.IsNullOrEmpty(value))
        {
            foreach (var property in existing)
            {
                var group = property.Parent;
                group.RemoveChild(property);
                // Existing group attributes and comments are independent
                // authored project data; resetting one property retains them.
            }
        }
        else if (existing.Length > 0) existing[^1].Value = value;
        else root.AddProperty(name, value);
    }
    public void SetDependencyReference(DependencyRange? before, DependencyRange? after, string projectPath)
    {
        if (before == null && after == null) throw new ArgumentException("A dependency change requires a reference.");
        if (before != null && after != null && (before.Type != after.Type || before.Name != after.Name))
            throw new InvalidOperationException("The dependency identity cannot change during an update.");
        var itemType = (before ?? after)!.Type == DependencyType.Project ? "ProjectReference" : "PackageReference";
        var folder = Path.GetDirectoryName(projectPath)!;
        bool Match(ProjectItemElement item, DependencyRange dependency) => string.Equals(item.ItemType, itemType, StringComparison.OrdinalIgnoreCase) && (dependency.Type == DependencyType.Package
            ? string.Equals(item.Include, dependency.Name, StringComparison.OrdinalIgnoreCase)
            : !item.Include.Contains("$(", StringComparison.Ordinal) && string.Equals(Path.GetFullPath(item.Include.Replace('\\', Path.DirectorySeparatorChar), folder),
                Path.GetFullPath(dependency.MSBuildProject, folder), StringComparison.OrdinalIgnoreCase));
        var existing = root.Items.Where(item => Match(item, before ?? after!)).ToArray();
        if (existing.Any(item => !string.IsNullOrWhiteSpace(item.Condition) || !string.IsNullOrWhiteSpace(item.Parent.Condition)))
            throw new InvalidOperationException("Conditional project references cannot be edited here.");
        if (before != null && existing.Length == 0) throw new InvalidOperationException("This reference is imported or no longer exists in the project file.");
        if (before == null && existing.Length != 0) throw new InvalidOperationException("This dependency already exists.");
        if (before?.Type == DependencyType.Package)
        {
            if (existing.Length != 1) throw new InvalidOperationException("This dependency has multiple project references.");
            var versionMetadata = existing[0].Metadata.LastOrDefault(metadata => string.Equals(metadata.Name, "Version", StringComparison.OrdinalIgnoreCase));
            if (versionMetadata != null && !string.IsNullOrWhiteSpace(versionMetadata.Condition))
                throw new InvalidOperationException("Conditional dependency versions cannot be edited here.");
            var version = versionMetadata?.Value;
            if (version == null || !PackageVersionRange.TryParse(version, out var parsed) || !Equals(parsed, before.VersionRange))
                throw new InvalidOperationException("This dependency version is imported or changed in the project file.");
        }
        if (after == null) foreach (var item in existing) item.Parent.RemoveChild(item);
        else if (existing.Length > 0)
        {
            if (after.Type != DependencyType.Package) throw new InvalidOperationException("A project dependency cannot change version.");
            var metadata = existing[0].Metadata.Last(entry => string.Equals(entry.Name, "Version", StringComparison.OrdinalIgnoreCase));
            metadata.Value = after.VersionRange.ToString();
        }
        else
        {
            var include = after.Type == DependencyType.Package ? after.Name : new UFile(after.MSBuildProject).MakeRelative(new UDirectory(folder)).ToOSPath();
            var item = root.AddItem(itemType, include);
            if (after.Type == DependencyType.Package)
            {
                var metadata = item.AddMetadata("Version", after.VersionRange.ToString()); metadata.ExpressedAsAttribute = true;
            }
        }
    }
    public void Restore(string snapshot)
    {
        using var reader = XmlReader.Create(new StringReader(snapshot), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        root = ProjectRootElement.Create(reader, collection, preserveFormatting: true);
        if (projectPath != null) root.FullPath = projectPath;
    }
    public void Load(byte[] contents)
    {
        using var stream = new MemoryStream(contents);
        using var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var xml = text.ReadToEnd(); encoding = text.CurrentEncoding;
        Restore(xml);
    }
    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, encoding, 1024, leaveOpen: true)) root.Save(writer);
        return stream.ToArray();
    }
    private static bool Matches(ProjectPropertyElement property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(property.Condition);
    public void Dispose() { collection.UnloadAllProjects(); collection.Dispose(); }
}

internal sealed record ProjectCompileItemOverride(string Include, bool Included);
internal sealed record ProjectDependencyEvaluation(string Framework, string[] CompilePaths, string[] PackageReferences, string[] ProjectReferences,
    ProjectEvaluatedPackageReference[] PackageDetails, ProjectEvaluatedProjectReference[] ProjectDetails, IReadOnlyDictionary<string, string> CompilerProperties);
internal sealed record ProjectEvaluatedPackageReference(string Name, string Version, string VersionOverride, string Aliases, string IncludeAssets, string ExcludeAssets);
internal sealed record ProjectEvaluatedProjectReference(string Path, string Aliases, string ReferenceOutputAssembly, string SetTargetFramework, string AdditionalProperties);
