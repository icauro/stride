using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Stride.Core.Reflection;
using Stride.Editor.Preview;

namespace Stride.Editor.ImGui;

internal sealed class NativeAssetPreviewRegistry : IDisposable
{
    internal sealed class Registration(Type asset, Type provider, Type? adapter, bool image, bool compiled)
    {
        internal Type? AssetType = asset, ProviderType = provider, AdapterType = adapter, SurfaceType;
        internal readonly bool Image = image;
        internal readonly bool RequiresCompiledContent = compiled;
        internal bool Retiring;
        internal int Leases;
        internal bool Available => !Retiring && ProviderType != null
            && !EditorExtensionGenerations.IsUnloading(ProviderType.Assembly)
            && (AdapterType == null || !EditorExtensionGenerations.IsUnloading(AdapterType.Assembly))
            && (SurfaceType == null || !EditorExtensionGenerations.IsUnloading(SurfaceType.Assembly))
            && AssetType != null && !EditorExtensionGenerations.IsUnloading(AssetType.Assembly)
            && (Image || SurfaceType != null);
        internal bool UsesCore(AssemblyLoadContext context)
            => Uses(AssetType, context) || Uses(ProviderType, context) || Uses(AdapterType, context);
        private static bool Uses(Type? type, AssemblyLoadContext context)
            => type != null && AssemblyLoadContext.GetLoadContext(type.Assembly) == context;
        internal void Clear() { if (Retiring && Leases == 0) AssetType = ProviderType = AdapterType = SurfaceType = null; }
    }
    internal sealed class Lease : IDisposable
    {
        private Registration? registration;
        internal readonly Type ProviderType, AssetType;
        internal readonly Type? AdapterType, SurfaceType;
        internal readonly bool Image;
        internal readonly bool RequiresCompiledContent;
        internal bool IsCoreUnloading => EditorExtensionGenerations.IsUnloading(ProviderType.Assembly)
            || EditorExtensionGenerations.IsUnloading(AssetType.Assembly)
            || AdapterType != null && EditorExtensionGenerations.IsUnloading(AdapterType.Assembly);
        internal bool IsRetiring => registration?.Available != true || EditorExtensionGenerations.IsUnloading(ProviderType.Assembly)
            || EditorExtensionGenerations.IsUnloading(AssetType.Assembly)
            || AdapterType != null && EditorExtensionGenerations.IsUnloading(AdapterType.Assembly)
            || SurfaceType != null && EditorExtensionGenerations.IsUnloading(SurfaceType.Assembly);
        internal Lease(Registration value)
        {
            registration = value; value.Leases++;
            ProviderType = value.ProviderType!; AssetType = value.AssetType!;
            AdapterType = value.AdapterType; SurfaceType = value.SurfaceType; Image = value.Image;
            RequiresCompiledContent = value.RequiresCompiledContent;
        }
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref registration, null);
            if (value != null) { value.Leases--; value.Clear(); }
        }
    }
    private readonly List<Registration> registrations = [];
    private readonly Dictionary<Type, Type> surfaces = [];
    private readonly Dictionary<AssemblyLoadContext, Action<AssemblyLoadContext>> observers = new();
    private readonly ConcurrentQueue<AssemblyLoadContext> unloading = new();
    private readonly ConcurrentQueue<Assembly> changed = new();
    private readonly ConditionalWeakTable<Assembly, object> scannedAssemblies = new();
    private bool disposed;
    internal long Version { get; private set; }
    private static readonly HashSet<Type> BuiltInFamilies = [typeof(Stride.Assets.Textures.TextureAsset), typeof(Stride.Assets.Models.ModelAsset),
        typeof(Stride.Assets.Models.ProceduralModelAsset), typeof(Stride.Assets.Materials.MaterialAsset), typeof(Stride.Assets.Skyboxes.SkyboxAsset),
        typeof(Stride.Assets.Entities.PrefabAsset), typeof(Stride.Assets.Models.PrefabModelAsset), typeof(Stride.SpriteStudio.Assets.SpriteStudioModelAsset),
        typeof(Stride.Assets.Models.AnimationAsset), typeof(Stride.Assets.Sprite.SpriteSheetAsset), typeof(Stride.Assets.SpriteFont.SpriteFontAsset),
        typeof(Stride.Assets.SpriteFont.PrecompiledSpriteFontAsset), typeof(Stride.Audio.Assets.SoundAsset), typeof(Stride.Physics.Assets.HeightmapAsset),
        typeof(Stride.Assets.Entities.SceneAsset)];

    internal NativeAssetPreviewRegistry()
    {
        foreach (var assembly in AssemblyRegistry.FindAll()) Scan(assembly);
        AssemblyRegistry.AssemblyRegistered += Registered;
        // Temporary asset/editor unregister is not collectible generation unload.
    }
    private void Registered(object? sender, AssemblyRegisteredEventArgs args)
    { changed.Enqueue(args.Assembly); }
    internal void Pump()
    {
        while (changed.TryDequeue(out var assembly)) Scan(assembly);
        while (unloading.TryDequeue(out var context))
        {
            foreach (var registration in registrations.Where(item => item.UsesCore(context)).ToArray())
            { registration.Retiring = true; registration.Clear(); registrations.Remove(registration); Version++; }
            foreach (var surface in surfaces.Where(item => AssemblyLoadContext.GetLoadContext(item.Key.Assembly) == context
                || AssemblyLoadContext.GetLoadContext(item.Value.Assembly) == context).ToArray()) surfaces.Remove(surface.Key);
            BindSurfaceGenerations();
            if (observers.Remove(context, out var handler)) context.Unloading -= handler;
        }
    }
    internal Lease? Acquire(Type assetType)
    {
        Pump();
        for (var current = assetType; current != null; current = current.BaseType)
        {
            var registration = registrations.LastOrDefault(item => item.AssetType == current && item.Available);
            if (registration != null) return new(registration);
            if (BuiltInFamilies.Contains(current)) return null;
        }
        return null;
    }
    internal bool Has(Type type)
    {
        using var lease = Acquire(type); return lease != null;
    }
    private void Scan(Assembly assembly)
    {
        if (disposed || EditorExtensionGenerations.IsUnloading(assembly) || scannedAssemblies.TryGetValue(assembly, out _)) return;
        Type[] types;
        var complete = true;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = error.Types.OfType<Type>().ToArray(); complete = false; }
        foreach (var type in types.Where(type => !type.IsAbstract && !type.ContainsGenericParameters))
            foreach (var attribute in type.GetCustomAttributesData())
            {
                if (!attribute.AttributeType.IsGenericType) continue;
                var definition = attribute.AttributeType.GetGenericTypeDefinition(); var arguments = attribute.AttributeType.GetGenericArguments();
                if (definition == typeof(ImGuiAssetPreviewSurfaceAttribute<>))
                {
                    if (!typeof(IImGuiAssetPreviewSurface).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null) continue;
                    surfaces[arguments[0]] = type; Observe(assembly); Observe(arguments[0].Assembly);
                }
                else if (definition == typeof(AssetPreviewRuntimeAttribute<>) || definition == typeof(AssetPreviewRuntimeAdapterAttribute<,>))
                {
                    var adapter = definition == typeof(AssetPreviewRuntimeAdapterAttribute<,>) ? type : null;
                    var provider = adapter == null ? type : arguments[0]; var asset = arguments[^1];
                    if (adapter == null && !typeof(IAssetPreviewRuntime).IsAssignableFrom(provider)
                        || adapter != null && !typeof(IAssetPreviewRuntimeAdapter).IsAssignableFrom(adapter)
                        || provider.GetConstructor(Type.EmptyTypes) == null || adapter != null && adapter.GetConstructor(Type.EmptyTypes) == null) continue;
                    if (registrations.Any(item => item.ProviderType == provider && item.AdapterType == adapter && item.AssetType == asset)) continue;
                    var image = attribute.NamedArguments.Any(value => value.MemberName == "UseImageSurface" && value.TypedValue.Value is true);
                    var compiled = !attribute.NamedArguments.Any(value => value.MemberName == "RequiresCompiledContent" && value.TypedValue.Value is false);
                    registrations.Add(new(asset, provider, adapter, image, compiled)); Version++; Observe(assembly); Observe(provider.Assembly); Observe(asset.Assembly);
                }
            }
        BindSurfaceGenerations();
        // Repeated category registrations do not need another metadata walk.
        // Partially loaded or dynamic assemblies can acquire real types later;
        // do not permanently certify those scans as complete.
        if (complete && !assembly.IsDynamic) scannedAssemblies.Add(assembly, new object());
    }
    private void BindSurfaceGenerations()
    {
        for (var index = 0; index < registrations.Count; ++index)
        {
            var previous = registrations[index];
            if (previous.ProviderType == null) continue;
            surfaces.TryGetValue(previous.ProviderType, out var surface);
            if (surface != null && EditorExtensionGenerations.IsUnloading(surface.Assembly)) surface = null;
            if (previous.SurfaceType == surface) continue;
            // A lease captures one exact provider/view generation. Never mutate
            // its registration to point at a different collectible surface.
            var next = new Registration(previous.AssetType!, previous.ProviderType, previous.AdapterType,
                previous.Image, previous.RequiresCompiledContent) { SurfaceType = surface };
            registrations[index] = next;
            Version++;
            previous.Retiring = true; previous.Clear();
        }
    }
    private void Observe(Assembly assembly)
    {
        EditorExtensionGenerations.Observe(assembly);
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        if (context is not { IsCollectible: true } || observers.ContainsKey(context)) return;
        Action<AssemblyLoadContext> handler = unloading.Enqueue;
        observers.Add(context, handler); context.Unloading += handler;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; AssemblyRegistry.AssemblyRegistered -= Registered;
        foreach (var observer in observers) observer.Key.Unloading -= observer.Value;
        observers.Clear(); surfaces.Clear();
        scannedAssemblies.Clear();
        foreach (var registration in registrations) { registration.Retiring = true; registration.Clear(); }
        registrations.Clear();
    }
}
