namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    internal object CaptureInspectorParityExtensionInventory()
    {
        Check();
        return new
        {
            Version, stopped, disposed, presenterLeases,
            providers = providers.Select(provider => new
            {
                type = provider.Type.AssemblyQualifiedName, provider.Available, provider.Retiring, provider.Leases,
                unloading = IsProviderGenerationUnloading(provider.Type),
                assetUpdaters = provider.Registration.Updaters.Count, contextUpdaters = provider.Registration.ContextUpdaters.Count,
                assetCommands = provider.Registration.Commands.Count, contextCommands = provider.Registration.ContextCommands.Count,
                primitiveTypes = provider.Registration.PrimitiveEditors.Keys.Select(type => type.AssemblyQualifiedName).OrderBy(name => name).ToArray(),
                propertyCommands = provider.Registration.PropertyCommands.Count,
                assetEditors = provider.Registration.Editors.Keys.Select(type => type.AssemblyQualifiedName).OrderBy(name => name).ToArray(),
            }).ToArray(),
        };
    }
}
