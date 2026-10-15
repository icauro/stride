using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Reflection;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ScriptComponentDiscovery(ScriptSourceDocument source, ScriptSemanticWorkspace compiler)
    {
        internal readonly ScriptSourceDocument Source = source;
        internal readonly string ProjectPath = source.ProjectPath!;
        internal readonly ScriptSemanticWorkspace Compiler = compiler;
        internal readonly CancellationTokenSource Cancellation = new();
        internal ScriptSourceSnapshot[] Sources = [];
        internal Task<ScriptComponentSourceResult>? Task;
        internal long Epoch = -1;
        private bool cancelled, compilerStopped, cancellationRetired;
        internal void Stop()
        {
            if (!cancelled) { Cancellation.Cancel(); cancelled = true; }
            if (!compilerStopped) { Compiler.Dispose(); compilerStopped = true; }
            if (cancellationRetired) return;
            if (Task is { } task) _ = task.ContinueWith(completed => { _ = completed.Exception; Cancellation.Dispose(); }, TaskScheduler.Default);
            else Cancellation.Dispose();
            cancellationRetired = true;
        }
    }
    private readonly Dictionary<AssetId, ScriptComponentDiscovery> scriptComponentDiscoveries = [];
    private readonly Dictionary<AssetId, string> scriptComponentDiscoveryErrors = [];

    private bool TryResolveScriptComponent(AssetItem item, out Type? type, out LoadedAssembly? generation)
    {
        type = null; generation = null;
        if (workspace == null || item.Asset is not ScriptSourceFileAsset || IsAuthoringPipelineUpdating) return false;
        var source = GetCanonicalDocument(item.FullPath.ToOSPath(), () => CreateScriptDocument(item.FullPath.ToOSPath())) as ScriptSourceDocument;
        if (source?.ProjectPath == null || !source.IsCSharpSource) return false;
        if (scriptComponentDiscoveries.TryGetValue(item.Id, out var discovery)
            && (!ReferenceEquals(discovery.Source, source) || !SamePath(discovery.ProjectPath, source.ProjectPath)))
        { discovery.Stop(); scriptComponentDiscoveries.Remove(item.Id); discovery = null; }
        if (discovery == null)
        {
            discovery = new(source, new ScriptSemanticWorkspace(source.ProjectPath, "Debug", ""));
            scriptComponentDiscoveries.Add(item.Id, discovery);
        }
        discovery.Compiler.SetProjectSnapshots(CaptureScriptProjectSnapshots());
        discovery.Compiler.SetProjectMembership(CaptureScriptProjectMembership());
        discovery.Compiler.SetReferenceContexts(documents.AuthoredDocuments.OfType<ScriptSourceDocument>()
            .SelectMany(value => value.CaptureReferenceContexts()).OrderBy(context => context.Order).ToArray());
        var sources = CaptureScriptSourceSnapshots();
        if (!sources.Any(value => SamePath(value.Path, source.Path)))
            sources = sources.Append(new ScriptSourceSnapshot(source.Path, source.Version, source.EditorText, source.ProjectPath)).ToArray();
        var changed = sources.Length != discovery.Sources.Length || sources.Where((value, index) => index >= discovery.Sources.Length
            || value.Path != discovery.Sources[index].Path || value.Version != discovery.Sources[index].Version || value.Text != discovery.Sources[index].Text).Any();
        if (discovery.Task?.IsCompleted == false) return false;
        if (changed || discovery.Task == null || discovery.Epoch != discovery.Compiler.Epoch)
        {
            discovery.Sources = sources; discovery.Epoch = discovery.Compiler.Epoch;
            var compiler = discovery.Compiler; var path = source.Path; var cancellation = discovery.Cancellation.Token;
            discovery.Task = System.Threading.Tasks.Task.Run(() => compiler.DiscoverSourceComponents(sources, path, cancellation), cancellation);
            return false;
        }
        if (discovery.Task.IsCanceled) return false;
        if (discovery.Task.IsFaulted) discovery.Task.GetAwaiter().GetResult();
        var result = discovery.Task.Result;
        if (result.Epoch != discovery.Compiler.Epoch || !SamePath(result.ProjectPath, source.ProjectPath)) return false;
        var assemblies = item.Package.LoadedAssemblies.Where(value => value.Assembly != null
            && SamePath(value.ProjectReference.Location.ToOSPath(), result.ProjectPath)
            && string.Equals(value.Assembly.GetName().FullName, result.AssemblyIdentity, StringComparison.OrdinalIgnoreCase)
            && AssemblyRegistry.FindAll().Contains(value.Assembly) && !EditorExtensionGenerations.IsUnloading(value.Assembly)).ToArray();
        if (assemblies.Length != 1) return false;
        generation = workspace.Session.AssemblyContainer.LoadedAssemblies.SingleOrDefault(value => ReferenceEquals(value.Assembly, assemblies[0].Assembly));
        if (generation == null) return false;
        foreach (var name in result.MetadataNames)
        {
            var candidate = generation.Assembly.GetType(name, false, false);
            if (candidate != null && EntityHierarchyDocument.RegisteredComponentTypes.Contains(candidate)
                && !candidate.IsAbstract && !candidate.ContainsGenericParameters && candidate.GetConstructor(Type.EmptyTypes) != null)
            { type = candidate; return true; }
        }
        generation = null; return false;
    }

    private void StopScriptComponentSources()
    {
        List<Exception>? failures = null;
        foreach (var discovery in scriptComponentDiscoveries.Values)
            try { discovery.Stop(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures != null) throw new AggregateException("Script component discovery cleanup failed.", failures);
        scriptComponentDiscoveries.Clear();
        scriptComponentDiscoveryErrors.Clear();
    }

    private bool CanCreateDroppedComponent(AssetItem item, out Type? type, out string reason)
    {
        if (item.Asset is ScriptSourceFileAsset)
        {
            reason = $"No class inheriting from {nameof(EntityComponent)} found in file {item.UnqualifiedUrl}";
            try
            {
                if (!TryResolveScriptComponent(item, out type, out _)) return false;
                scriptComponentDiscoveryErrors.Remove(item.Id);
            }
            catch (Exception error)
            {
                type = null;
                if (!scriptComponentDiscoveryErrors.TryGetValue(item.Id, out var previous) || previous != error.ToString())
                {
                    scriptComponentDiscoveryErrors[item.Id] = error.ToString();
                    outputLog.Append("Scripts", Stride.Core.Diagnostics.LogMessageType.Error, "Script component discovery", error.ToString());
                }
                return false;
            }
            reason = ""; return true;
        }
        var asset = (authoring?.Find(item.Id)?.Document as IAssetTagDocument)?.AuthoredAsset ?? item.Asset;
        type = AssetEntityFactories.ComponentType(asset);
        return AssetEntityFactories.CanCreate(asset, out reason) && type != null;
    }

    private EntityComponent CreateDroppedComponent(AssetItem item, List<IDisposable> resources)
    {
        if (item.Asset is not ScriptSourceFileAsset) return AssetEntityFactories.CreateComponent(item);
        if (!TryResolveScriptComponent(item, out var type, out var generation))
            throw new InvalidOperationException($"No class inheriting from {nameof(EntityComponent)} found in file {item.UnqualifiedUrl}");
        if (generation!.AcquireArtifactLifetime() is { } artifact) resources.Add(artifact);
        var sourceOwner = authoring!.Find(item.Id) ?? throw new InvalidOperationException("The script source has no canonical owner.");
        resources.Add(sourceOwner.AcquireHistory());
        return Activator.CreateInstance(type!) as EntityComponent
            ?? throw new InvalidOperationException("The component could not be created.");
    }
}
