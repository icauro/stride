using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Diagnostics;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Settings;

namespace Stride.Editor.ImGui;

/// <summary>Opt-in editor extensions discovered in real registered project/plugin assemblies.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EditorExtensionAttribute : Attribute { }

/// <summary>Session-independent counterpart of the editor plugin registration/lifetime contract.</summary>
public interface IEditorExtension
{
    void Initialize(EditorExtensionContext context, EditorExtensionRegistration registration);
    void SessionLoaded(EditorExtensionContext context) { }
    void SessionDisposed(EditorExtensionContext context) { }
}

public sealed class EditorExtensionContext
{
    private readonly Func<AssetId, AssetPropertyGraph> graph;
    private readonly Action<AssetId, Action<AssetPropertyGraph>, string> mutate;
    private readonly Action<Action, string> initialize;
    private readonly Func<AssetId, EditorAuthoringState>? editorOwner;
    internal Action<AssetId>? CloseOwnedEditor;
    internal bool MutationsAllowed { get; set; } = true;
    public CancellationToken CommandCancellation { get; internal set; }
    internal EditorExtensionContext(PackageSession session, ILogger logger, Func<AssetId, AssetPropertyGraph> graph,
        Action<AssetId, Action<AssetPropertyGraph>, string> mutate, Action<Action, string> initialize, Func<AssetId, EditorAuthoringState>? editorOwner = null)
    { Session = session; Logger = logger; this.graph = graph; this.mutate = mutate; this.initialize = initialize; this.editorOwner = editorOwner; }
    public PackageSession Session { get; }
    public ILogger Logger { get; }
    public AssetPropertyGraph GetAuthoringGraph(AssetId id) => graph(id);
    public void MutateAsset(AssetId id, Action<AssetPropertyGraph> mutation, string title)
    {
        ArgumentNullException.ThrowIfNull(mutation); ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!MutationsAllowed) throw new InvalidOperationException("Editor extension disposal cannot author assets.");
        mutate(id, mutation, title);
    }
    public void CloseAssetEditor(AssetId id)
        => (CloseOwnedEditor ?? throw new InvalidOperationException("The editor session cannot close owned asset editors."))(id);
    internal EditorAuthoringState GetAssetEditorOwner(AssetItem item)
    {
        var owner = editorOwner?.Invoke(item.Id) ?? throw new InvalidOperationException("The editor host has no canonical asset owner service.");
        if (!owner.IsPublished || owner.IsDisposed || !ReferenceEquals(owner.Registry.Find(item.Id), owner)
            || owner.Document is not IAssetTagDocument document || !ReferenceEquals(document.AuthoredAsset, item.Asset))
            throw new InvalidOperationException("The actual asset editor owner changed.");
        return owner;
    }
    internal void Initialize(Action action, string title) => initialize(action, title);
}

public sealed partial class EditorExtensionRegistration
{
    internal readonly List<Func<Asset, Type?, INodePresenterUpdater>> Updaters = [];
    internal readonly List<Func<Asset, INodePresenterCommand>> Commands = [];
    internal readonly List<Func<EditorPropertyProviderContext, INodePresenterUpdater>> ContextUpdaters = [];
    internal readonly List<Func<EditorPropertyProviderContext, INodePresenterCommand>> ContextCommands = [];
    internal readonly Dictionary<Type, Action<AssetItem>> Editors = [];
    internal readonly Dictionary<Type, Action<EditorPropertyEditorContext>> PrimitiveEditors = [];
    internal readonly List<EditorPropertyCommand> PropertyCommands = [];
    internal readonly List<Action> FrameActions = [];
    private bool sealedRegistration;
    public void RegisterNodePresenterUpdater(Func<Asset, Type?, INodePresenterUpdater> factory)
    { Check(); ArgumentNullException.ThrowIfNull(factory); Updaters.Add(factory); }
    public void RegisterNodePresenterCommand(Func<Asset, INodePresenterCommand> factory)
    { Check(); ArgumentNullException.ThrowIfNull(factory); Commands.Add(factory); }
    public void RegisterPropertyPresenterUpdater(Func<EditorPropertyProviderContext, INodePresenterUpdater> factory)
    { Check(); ArgumentNullException.ThrowIfNull(factory); ContextUpdaters.Add(factory); }
    public void RegisterPropertyPresenterCommand(Func<EditorPropertyProviderContext, INodePresenterCommand> factory)
    { Check(); ArgumentNullException.ThrowIfNull(factory); ContextCommands.Add(factory); }
    public void RegisterAssetEditor(Type assetType, Action<AssetItem> open)
    {
        Check(); ArgumentNullException.ThrowIfNull(assetType); ArgumentNullException.ThrowIfNull(open);
        if (!typeof(Asset).IsAssignableFrom(assetType)) throw new ArgumentException("Editor registration requires an asset type.", nameof(assetType));
        if (OwnedEditors.ContainsKey(assetType)) throw new InvalidOperationException("An asset editor is already registered for this type.");
        Editors.Add(assetType, open);
    }
    public void RegisterPrimitiveEditor(Type type, Action<EditorPropertyEditorContext> draw)
    { Check(); ArgumentNullException.ThrowIfNull(type); ArgumentNullException.ThrowIfNull(draw); PrimitiveEditors.Add(type, draw); }
    public void RegisterPropertyCommand(EditorPropertyCommand command)
    { Check(); ArgumentNullException.ThrowIfNull(command); PropertyCommands.Add(command); }
    public void RegisterFrameAction(Action draw)
    { Check(); ArgumentNullException.ThrowIfNull(draw); FrameActions.Add(draw); }
    internal void Seal() => sealedRegistration = true;
    private void Check() { if (sealedRegistration) throw new InvalidOperationException("Register editor services during extension initialization."); }
}

public sealed record EditorPropertyProviderContext(Package? Package, Asset? Asset, SettingsKey? SettingsKey, Type? DeclaredType);

/// <summary>A native editor control writes through its canonical document's validation/edit boundary.</summary>
public sealed class EditorPropertyEditorContext
{
    private readonly Action<object?> set;
    private readonly Action commit;
    private readonly Action<string, object?>? invokeCommand;
    internal EditorPropertyEditorContext(Type type, object? value, string label, bool canEdit, Action<object?> set, Action commit,
        Action<string, object?>? invokeCommand = null)
    { Type = type; Value = value; Label = label; CanEdit = canEdit; this.set = set; this.commit = commit; this.invokeCommand = invokeCommand; }
    public Type Type { get; }
    public object? Value { get; }
    public string Label { get; }
    public bool CanEdit { get; }
    public void SetValue(object? value)
    {
        if (!CanEdit) throw new InvalidOperationException("This property is read-only.");
        if (value != null && !(Nullable.GetUnderlyingType(Type) ?? Type).IsInstanceOfType(value)) throw new ArgumentException("The editor value is incompatible with the authored property.");
        set(value);
    }
    public void Commit() => commit();
    public void InvokeCommand(string name, object? parameter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (invokeCommand == null) throw new InvalidOperationException("This property editor has no native command target.");
        invokeCommand(name, parameter);
    }
}

/// <summary>A synchronous native graph command; the host owns its atomic journal and context-menu invocation.</summary>
public sealed record EditorPropertyCommand(string Name, Func<Asset, IGraphNode, bool> CanExecute, Action<Asset, IGraphNode> Execute);

/// <summary>Explicit registration for extensions outside registered asset assemblies.</summary>
public static class EditorExtensions
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, Func<IEditorExtension>> Registered = [];
    private static long version;
    internal static long Version => Interlocked.Read(ref version);
    public static IDisposable Register<T>(Func<T> factory) where T : class, IEditorExtension
    {
        ArgumentNullException.ThrowIfNull(factory);
        Func<IEditorExtension> create = () => factory() ?? throw new InvalidOperationException("The extension factory returned no instance.");
        var lease = new RegistrationLease(typeof(T), create);
        try { lease.Publish(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    internal static Dictionary<Type, Func<IEditorExtension>> Snapshot()
    { lock (Gate) return new(Registered); }
    private sealed class RegistrationLease : IDisposable
    {
        private Type? type;
        private Func<IEditorExtension>? factory;
        private System.Runtime.Loader.AssemblyLoadContext? loadContext;
        private EditorExtensionGenerations.State? generationState;
        private bool disposed;
        internal RegistrationLease(Type type, Func<IEditorExtension> factory)
        {
            this.type = type; this.factory = factory;
            generationState = EditorExtensionGenerations.Observe(type.Assembly);
            var generation = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly);
            if (generation?.IsCollectible == true)
            {
                loadContext = generation;
                generation.Unloading += GenerationUnloading;
            }
        }
        internal void Publish()
        {
            lock (Gate)
            {
                if (disposed || type == null || factory == null || generationState != null && Volatile.Read(ref generationState.Unloading) != 0) throw new InvalidOperationException("The extension generation is already unloading.");
                Registered.Add(type, factory); Interlocked.Increment(ref version);
            }
        }
        private void GenerationUnloading(System.Runtime.Loader.AssemblyLoadContext _) => Dispose();
        public void Dispose()
        {
            lock (Gate)
            {
                if (disposed) return;
                if (type != null && Registered.TryGetValue(type, out var current) && ReferenceEquals(current, factory))
                { Registered.Remove(type); Interlocked.Increment(ref version); }
                if (loadContext != null) loadContext.Unloading -= GenerationUnloading;
                type = null; factory = null; loadContext = null; generationState = null; disposed = true;
            }
        }
    }
}
