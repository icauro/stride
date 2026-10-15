using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

/// <summary>Loads the original installed asset descriptions without starting project-template downloads.</summary>
internal static class AssetTemplateCatalog
{
    private static readonly HashSet<string> registeredPackages = new(StringComparer.OrdinalIgnoreCase);

    internal static void EnsureRegistered()
    {
        foreach (var packageName in new[] { "Stride.Assets.Presentation.Wpf", "Stride.SpriteStudio.Assets" })
        {
            try
            {
                var path = FindDevelopmentPackage(packageName)
                    ?? PackageStore.Instance.GetPackageFileName(packageName,
                        new PackageVersionRange(new PackageVersion(StrideVersion.NuGetVersion)))?.ToOSPath();
                if (path == null || registeredPackages.Contains(path)) continue;
                var logger = new LoggerResult();
                var package = Package.Load(logger, path, new PackageLoadParameters
                {
                    // Shared template metadata only; do not load the WPF presentation assembly.
                    LoadAssemblyReferences = false,
                    AutoLoadTemporaryAssets = false,
                });
                if (logger.HasErrors || package == null)
                {
                    GlobalLogger.GetLogger("Templates").Error(logger.ToText());
                    continue;
                }
                TemplateManager.RegisterPackage(package);
                registeredPackages.Add(path);
            }
            catch (Exception error)
            {
                // Optional extension packages may be absent in an installed host.
                // Keep its supported registry factories available rather than closing the picker.
                GlobalLogger.GetLogger("Templates").Error($"Could not load asset templates from {packageName}.", error);
            }
        }
    }

    private static string? FindDevelopmentPackage(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "sources", name == "Stride.SpriteStudio.Assets" ? "engine" : "editor", name, name + ".sdpkg");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
