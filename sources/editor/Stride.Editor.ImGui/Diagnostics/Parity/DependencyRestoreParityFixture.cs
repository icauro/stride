using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Packages;

namespace Stride.Editor.ImGui;

internal static class DependencyRestoreParityFixture
{
    internal static string Create(string directory, string referencer)
    {
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var referencerRoot = Path.GetFullPath(Path.GetDirectoryName(referencer)!);
        if (root.StartsWith(referencerRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, referencerRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The proof project must be outside the referencer's default source glob.");
        var path = Path.Combine(root, "NativeDependencyProof.csproj");
        if (File.Exists(path)) throw new IOException("Dependency proof needs a fresh private directory.");
        var source = XDocument.Load(referencer);
        var engine = source.Descendants().Single(element => element.Name.LocalName == "PackageReference"
            && (string?)element.Attribute("Include") == "Stride.Engine");
        var version = (string?)engine.Attribute("Version") ?? engine.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;
        if (string.IsNullOrWhiteSpace(version) || version.Contains('$'))
            throw new InvalidDataException("A pinned actual engine package is required for the native dependency fixture.");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("RootNamespace", "NativeDependencyProof"), new XElement("StrideSkipAutoPack", true)),
            new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "Stride.Engine"),
                new XAttribute("Version", version))))).Save(path);
        var package = new Package(); package.Meta.Name = "NativeDependencyProof";
        package.Meta.Version = new PackageVersion("1.0.0");
        AssetFileSerializer.Save(Path.ChangeExtension(path, ".sdpkg"), package, null);
        File.WriteAllText(Path.Combine(root, "ActualDependencyMarker.cs"), "namespace NativeDependencyProof; public sealed class ActualDependencyMarker { }\n");
        var settings = NuGet.Configuration.Settings.LoadDefaultSettings(referencerRoot);
        var sources = new NuGet.Configuration.PackageSourceProvider(settings).LoadPackageSources().Where(source => source.IsEnabled).ToArray();
        if (sources.Length == 0) throw new InvalidOperationException("The actual fixture has no enabled NuGet source bindings.");
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"), sources.Select(source => new XElement("add",
                new XAttribute("key", source.Name), new XAttribute("value", source.Source)))),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"),
                new XAttribute("value", NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(settings))))))
            .Save(Path.Combine(root, "NuGet.Config"));
        return path;
    }
    internal static Dictionary<string, byte[]> CaptureRestoreFiles(string projectPath, string assetsPath)
    {
        var root = Path.GetFullPath(Path.GetDirectoryName(projectPath)!);
        var output = Path.GetFullPath(Path.GetDirectoryName(assetsPath)!);
        if (!output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore proof may inspect only its owned private project output.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(projectPath) };
        if (Directory.Exists(output))
            foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.TopDirectoryOnly))
                if (file.EndsWith(".nuget.cache", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Contains(".nuget.", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFullPath(file), Path.GetFullPath(assetsPath), StringComparison.OrdinalIgnoreCase)) files.Add(file);
        var locked = Path.Combine(root, "packages.lock.json"); if (File.Exists(locked)) files.Add(locked);
        return files.ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    }
    internal static bool SameFiles(IReadOnlyDictionary<string, byte[]> expected, IReadOnlyDictionary<string, byte[]> actual)
        => expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes));
    internal static bool SameDependencies(IReadOnlyList<DependencyRange> expected, IReadOnlyList<DependencyRange> actual)
        => expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.First.Name == pair.Second.Name
            && pair.First.Type == pair.Second.Type && pair.First.VersionRange?.ToString() == pair.Second.VersionRange?.ToString()
            && string.Equals(pair.First.MSBuildProject, pair.Second.MSBuildProject, StringComparison.OrdinalIgnoreCase));
}
