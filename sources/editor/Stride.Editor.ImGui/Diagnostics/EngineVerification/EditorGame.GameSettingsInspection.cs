using Stride.Assets;
using Stride.Core.Assets;
using Stride.Core.Serialization;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal object? CaptureGameSettingsVerificationIdentity()
    {
        if (workspace == null) return null;
        var projectPackage = workspace.Session.CurrentProject?.Package;
        var active = workspace.Package;
        var effective = projectPackage ?? (active != null && workspace.Session.Packages.Contains(active) ? active : null);
        var settings = effective?.GetGameSettingsAsset();
        var reference = settings?.GraphicsCompositor == null ? null : AttachedReferenceManager.GetAttachedReference(settings.GraphicsCompositor);
        object? PackageIdentity(Package? package) => package == null ? null : new
        {
            name = package.Meta.Name, path = package.FullPath.ToString(), container = package.Container?.GetType().FullName,
            registered = workspace.Session.Packages.Contains(package), package.IsReadOnly, state = package.State.ToString(),
        };
        return new
        {
            workspace.OpenPath, workspace.PackagePath,
            registry = CaptureContentReferenceVerificationRegistry(includeSerializerSnapshot: true),
            currentProject = workspace.Session.CurrentProject?.FullPath.ToString(),
            currentProjectPackage = PackageIdentity(projectPackage), activePackage = PackageIdentity(active), effectivePackage = PackageIdentity(effective),
            settings = settings == null ? null : new
            {
                id = settings.Id.ToString(), type = settings.GetType().FullName,
                compositorType = settings.GraphicsCompositor?.GetType().FullName,
                compositor = reference == null ? null : new { id = reference.Id.ToString(), url = reference.Url },
                unloadableSettings = CaptureUnloadableVerificationIdentity(settings),
                unloadableCompositor = CaptureUnloadableVerificationIdentity(settings.GraphicsCompositor),
            },
            authoredSettingsAssets = workspace.AssetItems.Where(item => item.Asset is GameSettingsAsset)
                .Select(item => new { id = item.Id.ToString(), location = item.Location.ToString(), type = item.Asset.GetType().FullName,
                    package = item.Package.FullPath.ToString() }).ToArray(),
        };
    }

    private static object? CaptureUnloadableVerificationIdentity(object? value)
    {
        if (value is not IUnloadable unknown) return null;
        return new
        {
            unknown.Error, unknown.TypeName, unknown.AssemblyName,
            actualProxyType = value.GetType().AssemblyQualifiedName,
            parsingEventCount = unknown.ParsingEvents.Count,
            parsingEvents = unknown.ParsingEvents.Take(32).Select(item => new
            {
                kind = item.GetType().Name,
                tag = (item as NodeEvent)?.Tag,
                scalar = (item as Scalar)?.Value,
                start = item.Start.ToString(), end = item.End.ToString(),
            }).ToArray(),
            truncated = unknown.ParsingEvents.Count > 32,
        };
    }
}
