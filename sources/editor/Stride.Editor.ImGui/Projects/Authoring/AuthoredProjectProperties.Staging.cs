using Stride.Core.Assets;
using System.Text;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Stride.Editor.ImGui;

internal sealed partial class AuthoredProjectProperties
{
    private static string EscapeStagingLiteral(string value)
    {
        // Use MSBuild's public literal-metadata contract to preserve its escaping
        // rules without depending on internal ProjectCollection helpers.
        Microsoft.Build.Framework.ITaskItem2 item = new Microsoft.Build.Utilities.TaskItem();
        item.SetMetadataValueLiteral("StrideStagingLiteral", value);
        return item.GetMetadataValueEscaped("StrideStagingLiteral");
    }

    public void RebaseProjectReferences(Func<string, string> map, string configuration = "Debug")
        => RebaseProjectReferences(map, null, null, null, configuration);

    public void RebaseProjectReferences(Func<string, string> map, Func<string, string>? stageImport,
        Action<string, byte[]>? publishImport, string configuration)
        => RebaseProjectReferences(map, stageImport, publishImport, null, configuration);

    /// <summary>Rebases a detached native construction/import graph. Imported bytes are emitted only through the caller's privately owned publication callback.</summary>
    public void RebaseProjectReferences(Func<string, string> map, Func<string, string>? stageImport,
        Action<string, byte[]>? publishImport, Func<string, string>? stageResource = null, string configuration = "Debug", Func<string, string>? stageTaskAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (projectPath == null) throw new InvalidOperationException("The native source project path is required for staging.");
        var contextProperty = stageResource == null ? "TargetFramework" : "_StridePreparedInputContext_" + Guid.NewGuid().ToString("N");
        var finalItems = new Dictionary<string, StagedProjectItem[]>(StringComparer.OrdinalIgnoreCase);
        var compilerFiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var allImports = new List<ResolvedImport>();
        var importFrameworks = new List<(ResolvedImport Import, string Framework)>();
        var references = new Dictionary<ProjectItemElement, Dictionary<string, List<string>>>();
        var resources = new Dictionary<ProjectItemElement, Dictionary<string, List<string>>>();
        var taskAssemblies = new Dictionary<ProjectUsingTaskElement, Dictionary<string, List<string>>>();
        var compilerFileDeclarations = new Dictionary<ProjectPropertyElement, Dictionary<string, List<string>>>();
        var hintPaths = new Dictionary<ProjectMetadataElement, Dictionary<string, List<string>>>();
        var imports = new Dictionary<ProjectImportElement, Dictionary<string, List<string>>>();
        var owners = new HashSet<ProjectRootElement>();
        var rewrittenReferences = new HashSet<ProjectItemElement>();
        var policyImports = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var implicitSdkImports = new Dictionary<string, ProjectImportElement>(StringComparer.Ordinal);
        var toolRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add<T>(Dictionary<T, Dictionary<string, List<string>>> entries, T element, string framework, string path) where T : notnull
        {
            if (!entries.TryGetValue(element, out var contexts)) entries.Add(element, contexts = new(StringComparer.OrdinalIgnoreCase));
            if (!contexts.TryGetValue(framework, out var values)) contexts.Add(framework, values = new());
            var full = Path.GetFullPath(path);
            if (!values.Contains(full, StringComparer.OrdinalIgnoreCase)) values.Add(full);
        }
        foreach (var framework in EvaluateDependencyContexts(configuration).Select(context => context.Framework).Append("").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var evaluated = new Project(root, EvaluationProperties(configuration, framework), null, collection);
            try
            {
                foreach (var resolved in evaluated.Imports.Where(import => import.SdkResult != null))
                {
                    if (resolved.SdkResult!.Path is { Length: > 0 } sdkPath) toolRoots.Add(Path.GetFullPath(sdkPath));
                    if (resolved.SdkResult.AdditionalPaths != null)
                        foreach (var additionalSdkPath in resolved.SdkResult.AdditionalPaths) toolRoots.Add(Path.GetFullPath(additionalSdkPath));
                }
                foreach (var property in new[] { "MSBuildSDKsPath", "MSBuildToolsPath", "MSBuildExtensionsPath", "MSBuildExtensionsPath32" })
                    if (evaluated.GetPropertyValue(property) is { Length: > 0 } path) toolRoots.Add(Path.GetFullPath(path));
                if (stageResource != null)
                {
                    var effectiveFramework = framework.Length > 0 ? framework : Environment.GetEnvironmentVariable("TargetFramework") ?? "";
                    var items = CaptureStagingItems(evaluated);
                    if (finalItems.TryGetValue(effectiveFramework, out var previous) && !SameStagingItems(previous, items))
                        throw new InvalidOperationException("The native item context changed during preparation.");
                    finalItems.TryAdd(effectiveFramework, items);
                    var files = CaptureNativeCompilerFiles(evaluated, projectPath);
                    if (compilerFiles.TryGetValue(effectiveFramework, out var previousFiles)
                        && (previousFiles.Count != files.Count || previousFiles.Any(file => !files.TryGetValue(file.Key, out var value) || value != file.Value)))
                        throw new InvalidOperationException("The native compiler resource context changed during preparation.");
                    compilerFiles.TryAdd(effectiveFramework, files);
                    foreach (var file in files)
                    {
                        var declaration = evaluated.GetProperty(file.Key)?.Xml;
                        if (declaration == null) continue;
                        Add(compilerFileDeclarations, declaration, framework, file.Value);
                        owners.Add(declaration.ContainingProject);
                    }
                }
                foreach (var item in evaluated.GetItems("ProjectReference"))
                {
                    Add(references, item.Xml, framework, Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!));
                    owners.Add(item.Xml.ContainingProject);
                }
                if (stageResource != null)
                {
                    foreach (var item in evaluated.Items.Where(item => item.ItemType is "Compile" or "Content" or "None" or "EmbeddedResource" or "AdditionalFiles" or "Analyzer" or "Resource" or "Page" or "ApplicationDefinition" or "NativeReference"))
                    {
                        // SDK implicit glob items keep their native private-project-relative semantics.
                        if (toolRoots.Any(directory => Inside(directory, item.Xml.ContainingProject.FullPath))) continue;
                        Add(resources, item.Xml, framework, Path.GetFullPath(item.EvaluatedInclude, Path.GetDirectoryName(projectPath)!));
                        owners.Add(item.Xml.ContainingProject);
                    }
                    foreach (var item in evaluated.GetItems("Reference"))
                    {
                        var metadata = item.GetMetadata("HintPath");
                        if (metadata?.Xml == null || string.IsNullOrWhiteSpace(metadata.EvaluatedValue)) continue;
                        if (toolRoots.Any(directory => Inside(directory, metadata.Xml.ContainingProject.FullPath))) continue;
                        Add(hintPaths, metadata.Xml, framework, Path.GetFullPath(metadata.EvaluatedValue, Path.GetDirectoryName(projectPath)!));
                        owners.Add(metadata.Xml.ContainingProject);
                    }
                }
                if (stageTaskAssembly != null)
                    foreach (var task in evaluated.GetLogicalProject().OfType<ProjectUsingTaskElement>())
                    {
                        if (string.IsNullOrWhiteSpace(task.AssemblyFile)) continue;
                        var value = evaluated.ExpandString(ExpandStagingFileScope(task.AssemblyFile, task.ContainingProject.FullPath));
                        if (value.Contains("%(", StringComparison.Ordinal) || value.Contains("$(", StringComparison.Ordinal))
                            throw new InvalidOperationException("The native task assembly has unresolved runtime input metadata.");
                        var assembly = Path.GetFullPath(value, Path.GetDirectoryName(task.ContainingProject.FullPath)!);
                        if (toolRoots.Any(directory => Inside(directory, assembly))) continue;
                        if (!File.Exists(assembly)) continue; // Native task registration is lazy; inactive/missing declarations retain their original conditions.
                        Add(taskAssemblies, task, framework, assembly); owners.Add(task.ContainingProject);
                    }
                foreach (var import in evaluated.Imports)
                {
                    allImports.Add(import); importFrameworks.Add((import, framework));
                    if (import.ImportingElement is { } sdkImport && ReferenceEquals(sdkImport.ContainingProject, root)
                        && sdkImport.ImplicitImportLocation != ImplicitImportLocation.None)
                    {
                        var key = string.Join("\0", sdkImport.Project, sdkImport.Sdk, sdkImport.Version, sdkImport.MinimumVersion, sdkImport.Condition, sdkImport.ImplicitImportLocation.ToString());
                        implicitSdkImports.TryAdd(key, sdkImport);
                    }
                    if (import.ImportingElement == null || import.SdkResult != null) continue;
                    var path = import.ImportedProject.FullPath;
                    if (toolRoots.Any(directory => Inside(directory, path))) continue;
                    Add(imports, import.ImportingElement, framework, path);
                    if (toolRoots.Any(directory => Inside(directory, import.ImportingElement.ContainingProject.FullPath)))
                    {
                        var policy = new[] { "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath" }
                            .FirstOrDefault(name => evaluated.GetPropertyValue(name) is { Length: > 0 } value
                                && string.Equals(Path.GetFullPath(value, Path.GetDirectoryName(projectPath)!), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                        if (policy != null) Add(policyImports, policy, framework, path);
                    }
                    owners.Add(import.ImportingElement.ContainingProject); owners.Add(import.ImportedProject);
                }
            }
            finally { collection.UnloadProject(evaluated); }
        }
        bool IsTool(ProjectRootElement owner) => toolRoots.Any(directory => Inside(directory, owner.FullPath));
        var modifiedOwners = new HashSet<ProjectRootElement>(references.Keys.Select(item => item.ContainingProject)
            .Concat(resources.Keys.Select(item => item.ContainingProject)).Concat(hintPaths.Keys.Select(item => item.ContainingProject))
            .Concat(taskAssemblies.Keys.Select(task => task.ContainingProject))
            .Concat(compilerFileDeclarations.Keys.Select(property => property.ContainingProject))
            .Concat(owners.Where(owner => !ReferenceEquals(owner, root) && !IsTool(owner)))
            .Concat(allImports.Select(import => import.ImportedProject).Where(HasDefiningScalarBindings)));
        var privateToolOwners = new HashSet<ProjectRootElement>(modifiedOwners.Where(IsTool));
        bool addedOwner;
        do
        {
            addedOwner = false;
            foreach (var import in allImports)
                if ((privateToolOwners.Contains(import.ImportedProject) || modifiedOwners.Contains(import.ImportedProject)) && import.ImportingElement is { } edge
                    && IsTool(edge.ContainingProject) && privateToolOwners.Add(edge.ContainingProject)) addedOwner = true;
        } while (addedOwner);
        bool NeedsPrivate(ProjectRootElement owner) => !IsTool(owner) || privateToolOwners.Contains(owner);
        foreach (var resolved in importFrameworks)
            if (resolved.Import.ImportingElement is { } edge
                && (privateToolOwners.Contains(resolved.Import.ImportedProject) || privateToolOwners.Contains(edge.ContainingProject))
                && edge.ImplicitImportLocation == ImplicitImportLocation.None)
                Add(imports, edge, resolved.Framework, resolved.Import.ImportedProject.FullPath);

        string ImportTarget(string path)
        {
            var imported = allImports.Select(import => import.ImportedProject).FirstOrDefault(owner => string.Equals(owner.FullPath, path, StringComparison.OrdinalIgnoreCase));
            return imported != null && IsTool(imported) && !privateToolOwners.Contains(imported) ? Path.GetFullPath(path) : stageImport!(path);
        }
        foreach (var owner in owners.Concat(privateToolOwners).Where(owner => !ReferenceEquals(owner, root) && NeedsPrivate(owner)).Distinct())
        {
            if (stageImport == null || publishImport == null) throw new InvalidOperationException("The native imported reference has no private file owner.");
            if (!File.Exists(owner.FullPath))
            {
                // Resolver-generated item roots have no physical source file. Their
                // real injected items are retained by the SDK import and projected
                // from the captured native evaluated item list after all imports.
                if (stageResource == null) throw new InvalidOperationException("A resolver-generated reference requires native item projection.");
                continue;
            }
            stageImport(owner.FullPath);
        }
        string FrameworkCondition(string framework)
        {
            if (framework.IndexOfAny(['\'', '\r', '\n']) >= 0) throw new InvalidOperationException("The native framework cannot be represented in a staging condition.");
            return "'$(" + contextProperty + ")' == '" + framework + "'";
        }
        void Rewrite(ProjectElement element, Dictionary<string, List<string>> contexts, Func<string, string> mapper, Action<ProjectElement, string> set, bool separateTargets = false)
        {
            if (element is ProjectItemElement updated && string.IsNullOrEmpty(updated.Include))
                throw new InvalidOperationException("A native resource update needs per-item staging ownership.");
            if (element is ProjectMetadataElement && contexts.Values.Any(values => values.Count != 1))
                throw new InvalidOperationException("A native reference metadata transform needs per-item staging ownership.");
            var variants = contexts.GroupBy(pair => string.Join(";", pair.Value), StringComparer.OrdinalIgnoreCase).ToArray();
            if (variants.Length == 1 && (!separateTargets || variants[0].First().Value.Count == 1))
            { set(element, string.Join(";", variants[0].First().Value.Select(mapper))); if (element is ProjectItemElement item) rewrittenReferences.Add(item); return; }
            var parent = element.Parent;
            foreach (var variant in variants)
            foreach (var targets in separateTargets ? variant.First().Value.Select(path => new[] { path }) : new[] { variant.First().Value.ToArray() })
            {
                var clone = element.Clone();
                if (element is ProjectElementContainer container)
                    ((ProjectElementContainer)clone).DeepCopyFrom(container);
                if (clone.Parent != null) parent.RemoveChild(clone);
                parent.InsertBeforeChild(clone, element);
                var condition = variants.Length == 1 ? "" : string.Join(" Or ", variant.Select(pair => FrameworkCondition(pair.Key)));
                if (condition.Length > 0) clone.Condition = string.IsNullOrWhiteSpace(element.Condition) ? "(" + condition + ")" : "(" + element.Condition + ") And (" + condition + ")";
                set(clone, string.Join(";", targets.Select(mapper)));
                if (clone is ProjectItemElement item) rewrittenReferences.Add(item);
            }
            parent.RemoveChild(element);
        }
        if (policyImports.Count > 0 || stageResource != null || privateToolOwners.Count > 0)
        {
            // Native root SDK imports are materialized with their actual parsed SDK
            // metadata so the private policy paths are visible before Sdk.props.
            // Original user bytes and SDK files remain untouched.
            foreach (var sdk in root.Children.OfType<ProjectSdkElement>().ToArray()) root.RemoveChild(sdk);
            root.Sdk = "";
            var first = root.FirstChild;
            foreach (var original in implicitSdkImports.Values)
            {
                var resolved = importFrameworks.Where(entry => entry.Import.ImportingElement is { } element
                    && ReferenceEquals(element.ContainingProject, root)
                    && element.ImplicitImportLocation == original.ImplicitImportLocation
                    && element.Project == original.Project && element.Sdk == original.Sdk
                    && element.Version == original.Version && element.MinimumVersion == original.MinimumVersion).ToArray();
                var contexts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in resolved)
                    if (!string.IsNullOrEmpty(entry.Import.ImportedProject.FullPath) && File.Exists(entry.Import.ImportedProject.FullPath))
                    {
                        if (!contexts.TryGetValue(entry.Framework, out var values)) contexts.Add(entry.Framework, values = new());
                        var target = ImportTarget(entry.Import.ImportedProject.FullPath);
                        if (!values.Contains(target, StringComparer.OrdinalIgnoreCase)) values.Add(target);
                    }
                // Resolver-generated roots still come from the true SDK resolver;
                // when there is no physical import the original native expression remains.
                var variants = contexts.Count == 0 ? new[] { new[] { original.Project } }
                    : contexts.GroupBy(entry => string.Join(";", entry.Value), StringComparer.OrdinalIgnoreCase).Select(group => group.First().Value.ToArray()).ToArray();
                foreach (var targets in variants)
                foreach (var target in targets)
                {
                    var clone = (ProjectImportElement)original.Clone();
                    clone.Sdk = original.Sdk;
                    clone.Version = original.Version;
                    clone.MinimumVersion = original.MinimumVersion;
                    clone.Project = target;
                    if (variants.Length > 1)
                    {
                        var condition = string.Join(" Or ", contexts.Where(entry => entry.Value.SequenceEqual(targets, StringComparer.OrdinalIgnoreCase)).Select(entry => FrameworkCondition(entry.Key)));
                        clone.Condition = string.IsNullOrEmpty(original.Condition) ? "(" + condition + ")" : "(" + original.Condition + ") And (" + condition + ")";
                    }
                    if (original.ImplicitImportLocation == ImplicitImportLocation.Top)
                    { if (first == null) root.AppendChild(clone); else root.InsertBeforeChild(clone, first); }
                    else root.AppendChild(clone);
                }
            }
            var group = root.CreatePropertyGroupElement(); root.PrependChild(group);
            if (stageResource != null) group.AddProperty(contextProperty, "$(TargetFramework)");
            foreach (var policy in policyImports)
            foreach (var variant in policy.Value.GroupBy(pair => string.Join(";", pair.Value), StringComparer.OrdinalIgnoreCase))
            {
                if (variant.First().Value.Count != 1) throw new InvalidOperationException("A native build policy has ambiguous import ownership.");
                var property = group.AddProperty(policy.Key, stageImport!(variant.First().Value[0]));
                if (policy.Value.Select(pair => string.Join(";", pair.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    property.Condition = "(" + string.Join(" Or ", variant.Select(pair => FrameworkCondition(pair.Key))) + ")";
            }
        }
        foreach (var entry in taskAssemblies) Rewrite(entry.Key, entry.Value, stageTaskAssembly!, (element, value) => ((ProjectUsingTaskElement)element).AssemblyFile = value, separateTargets: true);
        foreach (var entry in compilerFileDeclarations)
            Rewrite(entry.Key, entry.Value, path => toolRoots.Any(directory => Inside(directory, path)) ? path : stageResource!(path),
                (element, value) => ((ProjectPropertyElement)element).Value = EscapeStagingLiteral(value), separateTargets: true);
        foreach (var entry in resources.Where(entry => !string.IsNullOrEmpty(entry.Key.Include))) Rewrite(entry.Key, entry.Value, stageResource!, (element, value) => ((ProjectItemElement)element).Include = value);
        // Final native item projection preserves per-item transformed HintPath metadata.
        foreach (var entry in hintPaths.Where(entry => entry.Value.Values.All(values => values.Count == 1))) Rewrite(entry.Key, entry.Value, stageResource!, (element, value) => ((ProjectMetadataElement)element).Value = value, separateTargets: true);
        foreach (var entry in references.Where(entry => File.Exists(entry.Key.ContainingProject.FullPath) && !string.IsNullOrEmpty(entry.Key.Include))) Rewrite(entry.Key, entry.Value, map, (element, value) => ((ProjectItemElement)element).Include = value);
        foreach (var entry in imports)
        {
            if (!NeedsPrivate(entry.Key.ContainingProject)) continue;
            Rewrite(entry.Key, entry.Value, ImportTarget, (element, value) => ((ProjectImportElement)element).Project = value, separateTargets: true);
        }
        foreach (var item in root.Items.Where(item => item.ItemType == "ProjectReference" && !string.IsNullOrEmpty(item.Include)).ToArray())
        {
            if (rewrittenReferences.Contains(item)) continue;
            if (item.Include.IndexOfAny(['$', '*', '?', ';', '@', '%']) >= 0) throw new InvalidOperationException("An inactive native project-reference expression has no staging certificate.");
            item.Include = map(Path.GetFullPath(item.Include.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!));
        }
        foreach (var owner in owners.Concat(privateToolOwners).Where(owner => !ReferenceEquals(owner, root) && NeedsPrivate(owner) && File.Exists(owner.FullPath)).Distinct())
        {
            using var bytes = new MemoryStream();
            using (var writer = new StreamWriter(bytes, owner.Encoding ?? new UTF8Encoding(false), 1024, true)) owner.Save(writer);
            var body = IsTool(owner) ? PreserveToolchainFileScope(bytes.ToArray(), owner) : bytes.ToArray();
            if (stageResource != null) body = RebindDefiningScalarOrigins(body, owner.Encoding ?? new UTF8Encoding(false), contextProperty, projectPath);
            publishImport!(owner.FullPath, body);
        }
        if (stageResource != null)
        {
            PublishStagingItems(finalItems, contextProperty, map, stageResource);
            PublishNativeCompilerFiles(compilerFiles, contextProperty, path => toolRoots.Any(directory => Inside(directory, path)) ? path : stageResource(path));
            Load(RebindDefiningScalarOrigins(Serialize(), encoding, contextProperty, projectPath));
        }
    }
    private static bool Inside(string directory, string path)
    { var relative = Path.GetRelativePath(directory, path); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
}
