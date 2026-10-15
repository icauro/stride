using System.IO.Compression;
using System.Xml.Linq;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Serializers;
using Stride.Core.Assets.Templates;
using Stride.Core.IO;
using Stride.Core.Packages;

namespace Stride.Editor.ImGui;

internal static class NugetLibraryParityFixture
{
    internal static string? Root { get; private set; }
    internal static PackageName? Name { get; private set; }
    internal static Guid TemplateId { get; private set; }
    internal static AssetId MaterialId { get; private set; }
    internal static string ConfigureP02NugetLibraryFixtureBeforeBootstrap(string[] args)
    {
        var selector = Array.IndexOf(args, "--parity-batches");
        if (!args.Contains("--ui-test") || selector < 0 || selector + 1 >= args.Length
            || !args[selector + 1].Split(',').Contains("library-dependencies")) return "";
        if (Root != null) throw new InvalidOperationException("Private library context is already configured.");
        Root = Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "NugetLibrary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "feed"));
        var defaults = NuGet.Configuration.Settings.LoadDefaultSettings(null);
        var fallback = NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(defaults);
        var sources = new NuGet.Configuration.PackageSourceProvider(defaults).LoadPackageSources().Where(source => source.IsEnabled);
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "PrivateNativeProof"), new XAttribute("value", Path.Combine(Root, "feed"))),
                sources.Select(source => new XElement("add", new XAttribute("key", source.Name), new XAttribute("value", source.Source)))),
            new XElement("fallbackPackageFolders", new XElement("clear"), new XElement("add", new XAttribute("key", "ExistingSdkCache"), new XAttribute("value", fallback))),
            new XElement("config", new XElement("add", new XAttribute("key", "globalPackagesFolder"), new XAttribute("value", Path.Combine(Root, "packages"))))))
            .Save(Path.Combine(Root, "NuGet.Config"));
        var settings = NuGet.Configuration.Settings.LoadSpecificSettings(Root, "NuGet.Config");
        PackageStore.ConfigureDefaultSettings(settings, fallback);
        Name = new PackageName("Stride.NativeLibraryProof." + Guid.NewGuid().ToString("N"), new PackageVersion("1.0.0-dev"));
        return Root;
    }

    // After built-in asset registration, before the real private project load.
    internal static void Prepare(string projectPath)
    {
        if (Root == null || Name == null) throw new InvalidOperationException("Private store must be configured before bootstrap.");
        var projectRoot = Path.GetFullPath(Path.GetDirectoryName(projectPath)!);
        var owned = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!projectRoot.StartsWith(owned, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only the private project config may be written.");
        File.Copy(Path.Combine(Root, "NuGet.Config"), Path.Combine(projectRoot, "NuGet.Config"), overwrite: true);
        var payload = Path.Combine(Root, "payload", "stride");
        Directory.CreateDirectory(Path.Combine(payload, "Assets"));
        Directory.CreateDirectory(Path.Combine(payload, "Templates"));
        var material = new Stride.Assets.Materials.MaterialAsset(); MaterialId = material.Id;
        AssetFileSerializer.Save(Path.Combine(payload, "Assets", "NativeMaterial.sdmat"), material, null);
        TemplateId = Guid.NewGuid();
        var template = new TemplateAssetFactoryDescription { Id = TemplateId, Name = "Native library material",
            AssetTypeName = nameof(Stride.Assets.Materials.MaterialAsset), Scope = TemplateScope.Asset, DefaultOutputName = "NativeMaterial" };
        var templatePath = Path.Combine(payload, "Templates", "NativeMaterial.sdtpl");
        using (var stream = File.Create(templatePath)) Stride.Core.Yaml.AssetYamlSerializer.Default.Serialize(stream, template);
        var reloadedTemplate = Stride.Core.Yaml.YamlSerializer.Load<TemplateDescription>(templatePath);
        if (reloadedTemplate is not TemplateAssetFactoryDescription || reloadedTemplate.Id != TemplateId)
            throw new InvalidDataException("The genuine native template did not round-trip through Package.LoadTemplates' serializer.");
        var package = new Package(); package.Meta.Name = Name.Id; package.Meta.Version = Name.Version;
        package.AssetFolders.Add(new AssetFolder(new UDirectory("Assets")));
        var folder = new TemplateFolder(new UDirectory("Templates")); folder.Files.Add(new UFile("Templates/NativeMaterial.sdtpl"));
        package.TemplateFolders.Add(folder);
        AssetFileSerializer.Save(Path.Combine(payload, Name.Id + ".sdpkg"), package, null);
        var nuspec = new XDocument(new XElement("package", new XElement("metadata", new XElement("id", Name.Id),
            new XElement("version", Name.Version.ToString()), new XElement("authors", "Stride native fixture"),
            new XElement("description", "Disposable genuine native library restore fixture"))));
        using var archive = ZipFile.Open(Path.Combine(Root, "feed", Name.Id + "." + Name.Version + ".nupkg"), ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry(Name.Id + ".nuspec").Open())) writer.Write(nuspec);
        using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
            writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"nuspec\" ContentType=\"application/octet-stream\"/><Default Extension=\"sdpkg\" ContentType=\"application/octet-stream\"/><Default Extension=\"sdmat\" ContentType=\"application/octet-stream\"/><Default Extension=\"sdtpl\" ContentType=\"application/octet-stream\"/></Types>");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "payload"), "*", SearchOption.AllDirectories))
            archive.CreateEntryFromFile(file, Path.GetRelativePath(Path.Combine(Root, "payload"), file).Replace('\\', '/'));
    }
}
