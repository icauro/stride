using System.Security.Cryptography;
using System.Text.Json;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Serialization.Contents;
using Stride.Core.Storage;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;
using Stride.Rendering.Compositing;

if (args.Length == 2 && args[0] == "--fixture")
{
    var root = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(root);
    using var mount = new FileSystemProvider("/browser-fixture", root);
    using var database = new ObjectDatabase("/browser-fixture", "index", loadDefaultBundle: false);
    using var provider = new DatabaseFileProvider(database);
    var content = new ContentManager(new DatabaseFileProviderService(provider));
    using var scene = new Scene();
    scene.Entities.Add(new Entity("Browser asset marker", new Vector3(1, 2, 3)));
    using var compositor = new GraphicsCompositor();
    var settings = new GameSettings
    {
        PackageName = "BrowserFixture",
        DefaultSceneUrl = "/BrowserFixture/MainScene",
        DefaultGraphicsCompositorUrl = "/Stride.Engine/GraphicsCompositor",
    };
    content.Save(settings.DefaultSceneUrl, scene);
    content.Save(settings.DefaultGraphicsCompositorUrl, compositor);
    content.Save(GameSettings.AssetUrl, settings);
    var index = database.ContentIndexMap.GetMergedIdMap().ToDictionary();
    database.CreateBundle(index.Values.Distinct().ToArray(), "default", database.BundleBackend,
        new HashSet<ObjectId>(), index, [], useIncrementalBundle: false);
    File.WriteAllText(Path.Combine(root, "aliases"), "MainScene|/BrowserFixture/MainScene\n");
    Console.WriteLine($"Created {index.Count} actual serialized fixture assets.");
    return;
}
if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: ContentPackageTool <packaged data/db> <web output> [default bundle], or --fixture <db output>");
var source = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
if (source == destination || Path.GetRelativePath(source, destination).Split(Path.DirectorySeparatorChar)[0] != "..")
    throw new ArgumentException("Web output must be outside the source database.");
var bundle = args.Length == 3 ? args[2] : "default";
if (bundle.Length == 0 || bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
    throw new ArgumentException("Default bundle must be a portable name.");
var files = Directory.GetFiles(Path.Combine(source, "bundles"), "*.bundle").ToList();
if (!File.Exists(Path.Combine(source, "index"))) throw new FileNotFoundException("Database index is absent.");
files.Add(Path.Combine(source, "index"));
if (!files.Contains(Path.Combine(source, "bundles", bundle + ".bundle")))
    throw new FileNotFoundException("Default bundle is absent.");
files.AddRange(Directory.GetFiles(source, "*.aliases"));
if (File.Exists(Path.Combine(source, "aliases"))) files.Add(Path.Combine(source, "aliases"));
Directory.CreateDirectory(Path.Combine(destination, "bundles"));
var manifest = new BrowserContentManifest { Version = 1, DefaultBundle = bundle };
foreach (var file in files.Order(StringComparer.Ordinal))
{
    var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
    var bytes = File.ReadAllBytes(file);
    File.WriteAllBytes(Path.Combine(destination, relative), bytes);
    manifest.Files.Add(new BrowserContentFile { Path = relative, Size = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
}
File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, BrowserContentJson.Default.BrowserContentManifest));
Console.WriteLine($"Packaged {manifest.Files.Count} files, {manifest.Files.Sum(file => file.Size)} bytes.");
