using System.Collections;
using System.Reflection;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Reflection;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Serialization;
using Stride.Rendering.Compositing;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // Call without the optional serializer snapshot before package reading. It never
    // creates a serializer cache or attempts reference parsing to make the test pass.
    internal static object CaptureContentReferenceVerificationRegistry(bool includeSerializerSnapshot = false, bool runFactoryDiscoveryProbe = false)
    {
        var type = typeof(GraphicsCompositor);
        var factoryAssembly = typeof(ContentReferenceSerializer).Assembly;
        var scans = AssemblyRegistry.GetScanTypes(factoryAssembly);
        var scannedFactories = scans != null && scans.Types.TryGetValue(typeof(IYamlSerializableFactory), out var scannedTypes)
            ? scannedTypes.Select(found => found.AssemblyQualifiedName).ToArray() : [];
        return new
        {
            exactCompositorContentType = AssetRegistry.IsExactContentType(type),
            contentReferenceCanVisitCompositor = ContentReferenceSerializer.Default.CanVisit(type),
            compositorType = type.AssemblyQualifiedName,
            compositorAssemblyLocation = type.Assembly.Location,
            compositorAssemblyCategories = AssemblyRegistry.FindCategories(type.Assembly).OrderBy(name => name).ToArray(),
            contentReferenceAssemblyLocation = factoryAssembly.Location,
            contentReferenceAssemblyCategories = AssemblyRegistry.FindCategories(factoryAssembly).OrderBy(name => name).ToArray(),
            contentReferenceScannedFactoryTypes = scannedFactories,
            detachedFactoryDiscovery = runFactoryDiscoveryProbe ? CaptureDetachedFactoryDiscovery(factoryAssembly) : null,
            engineAssemblies = AssemblyRegistry.Find(AssemblyCommonCategories.Engine).Select(assembly => assembly.FullName).OrderBy(name => name).ToArray(),
            assetAssemblies = AssemblyRegistry.Find(AssemblyCommonCategories.Assets).Select(assembly => assembly.FullName).OrderBy(name => name).ToArray(),
            serializer = includeSerializerSnapshot ? CaptureExistingAssetSerializerFactorySnapshot() : null,
        };
    }

    private static object CaptureDetachedFactoryDiscovery(Assembly assembly)
    {
        // Explicit one-off diagnostic requested by the audit. This fresh settings
        // instance never replaces or refreshes the application's actual serializer.
        try
        {
            var settings = new SerializerSettings();
            settings.RegisterAssembly(assembly);
            var registry = settings.TagTypeRegistry;
            var factories = ((IEnumerable<IYamlSerializableFactory>)registry.GetType().GetProperty("SerializableFactories")!.GetValue(registry)!).ToArray();
            return new
            {
                factories = factories.Select(factory => factory.GetType().AssemblyQualifiedName).ToArray(),
                contentReferenceFactoryPresent = factories.Any(factory => factory is ContentReferenceSerializer),
            };
        }
        catch (Exception error) { return new { inspectionError = error.ToString() }; }
    }

    private static object CaptureExistingAssetSerializerFactorySnapshot()
    {
        // Private fields are inspected only as diagnostics against this exact source
        // revision. Calling GetSerializerSettings would build/refresh the cache and
        // conceal the stale-factory state that this diagnostic is meant to inspect.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var owner = AssetYamlSerializer.Default;
            var gate = typeof(YamlSerializerBase).GetField("Lock", flags)!.GetValue(owner)!;
            lock (gate)
            {
                var cached = (Serializer?)typeof(AssetYamlSerializer).GetField("serializer", flags)!.GetValue(owner);
                var assemblies = ((IEnumerable<Assembly>)typeof(YamlSerializerBase).GetField("RegisteredAssemblies", flags)!.GetValue(owner)!)
                    .Select(assembly => assembly.FullName).ToArray();
                var selector = cached?.Settings.SerializerFactorySelector;
                var factories = selector == null ? [] : ((IEnumerable)typeof(SerializerFactorySelector).GetField("factories", flags)!.GetValue(selector)!)
                    .Cast<object>().Select(factory => factory.GetType().AssemblyQualifiedName).ToArray();
                return new
                {
                    initialized = cached != null, registeredAssemblies = assemblies,
                    selectorType = selector?.GetType().FullName, factories,
                    contentReferenceFactoryPresent = factories.Contains(typeof(ContentReferenceSerializer).AssemblyQualifiedName),
                };
            }
        }
        catch (Exception error) { return new { inspectionError = error.ToString() }; }
    }
}
