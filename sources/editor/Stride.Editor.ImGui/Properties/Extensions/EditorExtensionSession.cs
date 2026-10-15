using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;
using Stride.Core.Settings;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession : IDisposable
{
    private sealed partial class Provider(Type type, IEditorExtension extension, EditorExtensionRegistration registration)
    {
        internal readonly Type Type = type;
        internal readonly IEditorExtension Extension = extension;
        internal readonly EditorExtensionRegistration Registration = registration;
        internal int Leases;
        internal bool Available = true;
        internal bool Retiring, SessionDisposed, InstanceDisposed;
    }
    private readonly EditorExtensionContext context;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<Provider> providers = [];
    private readonly List<Action> failedPresenterCleanup = [];
    private bool stopped, disposed;
    private int presenterLeases;
    private long registrationsVersion = -1;
    private int assemblyChanged = 1;
    internal long Version { get; private set; }
    internal bool HasProviders => providers.Any(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type));
    internal void SetCommandCancellation(CancellationToken token) => context.CommandCancellation = token;
    internal void DrawFrame()
    {
        Check();
        if (stopped) return;
        foreach (var provider in providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && provider.Available))
            foreach (var draw in provider.Registration.FrameActions) draw();
    }
    internal EditorExtensionSession(EditorExtensionContext context)
    {
        this.context = context; context.CloseOwnedEditor = CloseOwnedAssetEditors;
        AssemblyRegistry.AssemblyRegistered += AssembliesChanged;
        AssemblyRegistry.AssemblyUnregistered += AssembliesChanged;
    }
    private void AssembliesChanged(object? sender, AssemblyRegisteredEventArgs args) => Interlocked.Exchange(ref assemblyChanged, 1);
    internal void Refresh()
    {
        Check();
        if (stopped) return;
        ApplyUnloadingProviderGenerations();
        RetireUnused();
        if (Interlocked.Exchange(ref assemblyChanged, 0) == 0 && registrationsVersion == EditorExtensions.Version) return;
        var available = EditorExtensions.Snapshot();
        try
        {
            foreach (var assembly in AssemblyRegistry.FindAll().OrderBy(assembly => assembly.FullName, StringComparer.Ordinal))
            foreach (var type in assembly.GetTypes().Where(type => type.IsDefined(typeof(EditorExtensionAttribute), false)))
            {
                if (IsProviderGenerationUnloading(type)) continue;
                if (type.IsAbstract || !typeof(IEditorExtension).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                    throw new InvalidOperationException("Editor extension must be concrete, implement IEditorExtension and have a public parameterless constructor: " + type.FullName);
                available.TryAdd(type, () => (IEditorExtension)Activator.CreateInstance(type)!);
            }
            // AssemblyRegistry unregistration also occurs during native reload. Keep
            // these actual CLR provider generations for retained authored/history views.
            foreach (var provider in providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type)))
            {
                var active = available.ContainsKey(provider.Type);
                if (provider.Available != active) { provider.Available = active; Version++; }
            }
            foreach (var pair in available.OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal))
            {
                if (IsProviderGenerationUnloading(pair.Key) || providers.Any(provider => !provider.Retiring && provider.Type == pair.Key)) continue;
                var extension = pair.Value();
                var registration = new EditorExtensionRegistration();
                var provider = new Provider(pair.Key, extension, registration);
                ObserveProviderGeneration(provider);
                providers.Add(provider);
                try
                {
                    context.Initialize(() =>
                    {
                        extension.Initialize(context, registration); registration.Seal();
                        foreach (var type in registration.Editors.Keys.Concat(registration.OwnedEditors.Keys))
                            if (providers.Any(existing => !ReferenceEquals(existing, provider) && !existing.Retiring && existing.Available && (existing.Registration.Editors.ContainsKey(type) || existing.Registration.OwnedEditors.ContainsKey(type))))
                                throw new InvalidOperationException("An extension asset editor is already registered for " + type.FullName);
                        foreach (var type in registration.PrimitiveEditors.Keys)
                            if (providers.Any(existing => !ReferenceEquals(existing, provider) && !existing.Retiring && existing.Available && existing.Registration.PrimitiveEditors.ContainsKey(type)))
                                throw new InvalidOperationException("An extension primitive editor is already registered for " + type.FullName);
                        extension.SessionLoaded(context);
                    }, "Initialize editor extension " + pair.Key.Name);
                    Version++;
                }
                catch { provider.Retiring = true; Version++; throw; }
            }
            registrationsVersion = EditorExtensions.Version;
            RetireUnused();
        }
        catch { Interlocked.Exchange(ref assemblyChanged, 1); throw; }
    }
    internal EditorExtensionPresenterLease AcquirePresenters(Asset asset, Type? declaredType)
        => AcquirePresenters(new(context.Session.FindAsset(asset.Id)?.Package, asset, null, declaredType));
    internal EditorExtensionPresenterLease AcquirePackagePresenters(Package package, SettingsKey key, Type? declaredType = null)
        => AcquirePresenters(new(package, null, key, declaredType));
    private EditorExtensionPresenterLease AcquirePresenters(EditorPropertyProviderContext propertyContext)
    {
        Check();
        if (stopped) throw new InvalidOperationException("Editor extensions are closing.");
        var owners = providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && (provider.Available
            || provider.Type.Assembly == propertyContext.DeclaredType?.Assembly || provider.Type.Assembly == propertyContext.Asset?.GetType().Assembly)).ToArray();
        foreach (var owner in owners) owner.Leases++;
        // Even an empty provider set owns the session's release callback. Its
        // presentation must retire before this session, just like provider leases.
        presenterLeases++;
        var ownedInstances = new List<IDisposable>();
        var released = false;
        void Release()
        {
            Check();
            if (released) return;
            while (ownedInstances.Count != 0) { ownedInstances[^1].Dispose(); ownedInstances.RemoveAt(ownedInstances.Count - 1); }
            foreach (var owner in owners) owner.Leases--;
            presenterLeases--;
            released = true;
        }
        try
        {
            var updaters = new List<INodePresenterUpdater>();
            foreach (var factory in propertyContext.Asset == null ? [] : owners.SelectMany(owner => owner.Registration.Updaters))
            {
                var updater = factory(propertyContext.Asset!, propertyContext.DeclaredType) ?? throw new InvalidOperationException("The extension updater factory returned no updater.");
                updaters.Add(updater); if (updater is IDisposable resource) ownedInstances.Add(resource);
            }
            foreach (var factory in owners.SelectMany(owner => owner.Registration.ContextUpdaters))
            {
                var updater = factory(propertyContext) ?? throw new InvalidOperationException("The extension updater factory returned no updater.");
                updaters.Add(updater); if (updater is IDisposable resource) ownedInstances.Add(resource);
            }
            var commands = new List<INodePresenterCommand>();
            foreach (var factory in propertyContext.Asset == null ? [] : owners.SelectMany(owner => owner.Registration.Commands))
            {
                var command = factory(propertyContext.Asset!) ?? throw new InvalidOperationException("The extension command factory returned no command.");
                commands.Add(command); if (command is IDisposable resource) ownedInstances.Add(resource);
            }
            foreach (var factory in owners.SelectMany(owner => owner.Registration.ContextCommands))
            {
                var command = factory(propertyContext) ?? throw new InvalidOperationException("The extension command factory returned no command.");
                commands.Add(command); if (command is IDisposable resource) ownedInstances.Add(resource);
            }
            var cleanupQueued = false;
            void ReleaseLease()
            {
                try { Release(); }
                catch
                {
                    if (!cleanupQueued)
                    {
                        cleanupQueued = true;
                        failedPresenterCleanup.Add(Release);
                    }
                    throw;
                }
            }
            return new(updaters.ToArray(), commands.ToArray(), ReleaseLease);
        }
        catch (Exception failure)
        {
            try { Release(); }
            catch (Exception cleanup) { failedPresenterCleanup.Add(Release); throw new AggregateException("Extension presenter creation and cleanup failed.", failure, cleanup); }
            throw;
        }
    }
    internal bool TryOpenAssetEditor(AssetItem item)
    {
        Check();
        if (stopped) throw new InvalidOperationException("Editor extensions are closing.");
        return OpenResolvedAssetEditor(item);
    }
    internal void Stop() { Check(); stopped = true; }
    internal bool HasAssetEditor(Type assetType)
    {
        Check();
        return !stopped && ResolveAssetEditor(assetType) != null;
    }
    internal bool HasPrimitiveEditor(Type type) => providers.Any(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && provider.Registration.PrimitiveEditors.ContainsKey(type));
    internal bool TryDrawPrimitive(EditorPropertyEditorContext editor)
    {
        Check();
        if (stopped) return false;
        var matches = providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && provider.Registration.PrimitiveEditors.ContainsKey(editor.Type)).ToArray();
        var active = matches.Where(provider => provider.Available).ToArray();
        if (active.Length != 0) matches = active;
        if (matches.Length == 0) return false;
        if (matches.Length != 1) throw new InvalidOperationException("Multiple extension editors handle this property type.");
        matches[0].Registration.PrimitiveEditors[editor.Type](editor); return true;
    }
    internal IReadOnlyList<EditorPropertyCommand> PropertyCommands(Asset asset, Type? declaredType)
    {
        Check();
        return providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && (provider.Available || provider.Type.Assembly == declaredType?.Assembly
            || provider.Type.Assembly == asset.GetType().Assembly)).SelectMany(provider => provider.Registration.PropertyCommands).ToArray();
    }
    private void RetireUnused()
    {
        RetireOwnedAssetEditors();
        foreach (var cleanup in failedPresenterCleanup.ToArray()) { cleanup(); failedPresenterCleanup.Remove(cleanup); }
        foreach (var provider in providers.Where(provider => provider.Retiring && provider.Leases == 0).ToArray())
        {
            if (!provider.SessionDisposed)
            {
                var allowed = context.MutationsAllowed; context.MutationsAllowed = false;
                try { provider.Extension.SessionDisposed(context); provider.SessionDisposed = true; }
                finally { context.MutationsAllowed = allowed; }
            }
            if (!provider.InstanceDisposed) { if (provider.Extension is IDisposable resource) resource.Dispose(); provider.InstanceDisposed = true; }
            ReleaseProviderGenerationObserver(provider);
            providers.Remove(provider);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        Check(); stopped = true;
        foreach (var provider in providers) provider.Retiring = true;
        RetireUnused();
        if (providers.Count != 0 || presenterLeases != 0)
            throw new InvalidOperationException("Dispose extension property presenters before their session providers.");
        AssemblyRegistry.AssemblyRegistered -= AssembliesChanged; AssemblyRegistry.AssemblyUnregistered -= AssembliesChanged;
        context.CloseOwnedEditor = null;
        disposed = true;
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Editor extensions require their owning editor thread.");
    }
}

internal sealed class EditorExtensionPresenterLease(INodePresenterUpdater[] updaters, INodePresenterCommand[] commands, Action release) : IDisposable
{
    internal IReadOnlyList<INodePresenterUpdater> Updaters { get; } = updaters;
    internal IReadOnlyList<INodePresenterCommand> Commands { get; } = commands;
    private bool disposed;
    public void Dispose() { if (disposed) return; release(); disposed = true; }
}
