using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Tga;
using Stride.Assets.Presentation.Templates;
using System.Text.Json;
using Stride.Assets.Models;
using Stride.Assets.Materials;
using Stride.Assets.Textures;
using Stride.Core.Assets;
using Stride.Core.Assets.Analysis;
using Stride.Core.Assets.Tracking;
using Stride.Core.IO;
using Stride.Core.Serialization.Contents;

namespace Stride.Editor.ImGui;

internal sealed record FbxNativeOutput(string Id, string Path, string Url, string Type, string? DiskSha256,
    string[] References, string[] Sources, string[] NativeSourceHashes, int Nodes, int Materials);
internal sealed partial class EditorGame
{
    private string? fbxTemplateFixtureRoot;
    private readonly List<object> fbxDerivedCompanions = [];
    private readonly Dictionary<string, string> fbxTemplateInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> fbxTemplateOriginalInputs = new(StringComparer.OrdinalIgnoreCase);
    internal void PrepareFbxTemplateFixtureBeforeLoad(string packagePath)
    {
        var packageFile = Path.GetFullPath(packagePath);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!packageFile.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase) || fbxTemplateFixtureRoot != null)
            throw new InvalidOperationException("FBX preparation requires one fresh disposable native project.");
        var package = AssetFileSerializer.Load<Package>(packageFile).Asset;
        if (!package.ResourceFolders.Any(folder => folder.ToString().Replace('\\', '/').TrimEnd('/') == "Resources"))
            throw new InvalidOperationException("FBX fixture needs its real Resources mount.");
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "tests", "editor-imgui", "fixtures", "PreviewFamilies", "manifest.json"))) repository = repository.Parent;
        if (repository == null) throw new DirectoryNotFoundException("The pinned importer source manifest is missing.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository.FullName, "tests", "editor-imgui", "fixtures", "PreviewFamilies", "manifest.json")));
        const string prefix = "samples/Templates/Packs/mannequinModel/Resources/";
        string[] required = ["Models/mannequinModel.fbx", "Animation/Run.fbx", "Textures/normalmapUpper.png", "Textures/normalmapLower.png"];
        var verified = required.Select(relative =>
        {
            var entry = manifest.RootElement.GetProperty("resources").EnumerateArray().Single(value => value.GetProperty("source").GetString() == prefix + relative);
            var source = Path.GetFullPath(Path.Combine(repository.FullName, (prefix + relative).Replace('/', Path.DirectorySeparatorChar)));
            var hash = entry.GetProperty("sha256").GetString()!;
            if (new FileInfo(source).Length != entry.GetProperty("bytes").GetInt64() || !string.Equals(TemplateFileHash(source), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pinned FBX source or companion identity changed: " + relative);
            return (Relative: relative, Source: source, Hash: hash.ToUpperInvariant());
        }).ToArray();
        var root = Path.Combine(Path.GetDirectoryName(packageFile)!, "Resources", "FbxTemplateAcceptance-" + Guid.NewGuid().ToString("N"));
        foreach (var entry in verified)
        {
            var destination = Path.Combine(root, entry.Relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(entry.Source, destination, false);
            fbxTemplateInputs.Add(destination, entry.Hash); fbxTemplateOriginalInputs.Add(entry.Source, entry.Hash);
        }
        // The original FBX names Maya TGA sources; native importer resolves their
        // basenames beside the model. Generate genuine private TGA companions.
        foreach (var pair in new[] { (Png: "normalmapUpper.png", Tga: "Upper [Normal].tga"), (Png: "normalmapLower.png", Tga: "Lower [Normal].tga") })
        {
            var png = Path.Combine(root, "Textures", pair.Png); var tga = Path.Combine(root, "Models", pair.Tga);
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
            image.Save(tga, new TgaEncoder { BitsPerPixel = TgaBitsPerPixel.Pixel32 });
            using var decoded = SixLabors.ImageSharp.Image.Load<Rgba32>(tga);
            var pixels = new byte[checked(image.Width * image.Height * 4)]; var roundtrip = new byte[pixels.Length];
            image.CopyPixelDataTo(pixels); decoded.CopyPixelDataTo(roundtrip);
            if (decoded.Width != image.Width || decoded.Height != image.Height || !pixels.SequenceEqual(roundtrip))
                throw new InvalidDataException("The private TGA companion changed its pinned PNG pixels.");
            var sha = TemplateFileHash(tga); fbxTemplateInputs.Add(tga, sha);
            fbxDerivedCompanions.Add(new { original = png, originalSha = TemplateFileHash(png), derived = tga, derivedSha = sha,
                image.Width, image.Height, pixelsSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)) });
        }
        File.Copy(Path.Combine(repository.FullName, "LICENSE.md"), Path.Combine(root, "Stride-MIT-LICENSE.md"), false);
        fbxTemplateFixtureRoot = root; VerifyFbxTemplateInputs();
    }
    internal string FbxTemplateSource(string variant) => Path.Combine(fbxTemplateFixtureRoot ?? throw new InvalidOperationException("Prepare the FBX fixture before native loading."),
        variant == "animation" ? "Animation" : "Models", variant == "animation" ? "Run.fbx" : "mannequinModel.fbx");
    internal AssetCreationPanel.Choice FbxTemplateChoice(string variant)
    {
        var id = Guid.Parse(variant switch { "model" => "3B778954-54C4-4FF3-97EF-4CD7AEA0B97D", "skeleton" => "7DA3597C-7AC4-450E-B60B-21DDB6296208", "animation" => "24E65D4C-0207-4B62-B8F2-BAB33F44B4FA", _ => throw new ArgumentException("Unknown importer variant.") });
        return AssetCreation.InspectTemplateChoices().Single(choice => choice.Template?.Id == id);
    }
    internal bool IsFbxTemplateImporterRegistered(Guid? importerId, string source, Type root) => importerId != null
        && GetAssetImportChoices(source, root).Any(choice => choice.Id == importerId);
    internal object CaptureFbxTemplateImportDiagnostics()
    {
        long version = -1; outputLog.Read(ref version, out var entries);
        var errors = entries.Where(entry => entry.Module == "Asset import" && entry.Level >= Stride.Core.Diagnostics.LogMessageType.Error)
            .TakeLast(8).Select(entry => new { entry.Sequence, entry.Text, entry.Exception }).ToArray();
        var bindings = workspace?.Session.Packages.SelectMany(package => package.LoadedAssemblies)
            .Where(owner => owner.Assembly is { IsDynamic: false } assembly && assembly.GetName().Name == "Stride.Engine")
            .Select(owner => new { declaredPath = owner.Path, actualPath = owner.Assembly!.Location,
                actualIdentity = owner.Assembly.FullName, actualModule = owner.Assembly.ManifestModule.ModuleVersionId }).ToArray();
        return new { worker = assetImportTask?.Status.ToString(), errorMessages = errors, engineBindings = bindings,
            actualModelAssetModule = typeof(ModelAsset).Assembly.ManifestModule.ModuleVersionId };
    }
    internal (int Frame, string? Path) FbxHistoryToolbarAcceptance(string action)
        => stockHistoryToolbarAccepted.GetValueOrDefault(action);
    internal bool FbxHistoryToolbarEligible(string action) => action == "undo" ? CanUndoCurrent || HasPendingRenameChanges : CanRedoCurrent;
    internal string? FbxHistoryCommandPath => CommandDocument?.Path;
    internal object FbxHistoryToolbarEvidence => new { current = stockHistoryToolbarCurrent, pressed = stockHistoryToolbarPressed,
        undo = new { FbxHistoryToolbarAcceptance("undo").Frame, FbxHistoryToolbarAcceptance("undo").Path },
        redo = new { FbxHistoryToolbarAcceptance("redo").Frame, FbxHistoryToolbarAcceptance("redo").Path } };
    internal EditorHistorySnapshot FbxTemplateHistory => sessionHistory!.History;
    internal EditorHistorySnapshot? FbxTemplateAcceptedOptionsHistory { get; private set; }
    private void RecordFbxTemplateOptionsAcceptance()
    {
        if (Controls.Enabled) FbxTemplateAcceptedOptionsHistory = sessionHistory!.History;
    }
    internal void CertifyFbxOptionsHistory(int baseline, int importBaseline)
    {
        var snapshot = FbxTemplateAcceptedOptionsHistory ?? throw new InvalidOperationException("No native options acceptance receipt.");
        var transactions = sessionHistory.Stack.RetrieveAllTransactions().ToArray();
        if (importBaseline != baseline + 1 || snapshot.Position != importBaseline
            || snapshot.Entries[baseline].Name != "Model import options" || snapshot.Entries[baseline].Id != transactions[baseline].Id
            || sessionHistory.GetParticipants(transactions[baseline]).Length == 0
            || sessionHistory.GetParticipants(transactions[baseline]).Any(state => state.Document is not PackageSettingsDocument))
            throw new InvalidOperationException("Model options must own their separate native package-settings transaction.");
    }
    internal string CaptureFbxModelOptions()
    {
        var settings = (AssetCreationBrowser.SelectedFolder?.Package ?? throw new InvalidOperationException("The native template target is missing.")).UserSettings;
        return JsonSerializer.Serialize(new { materials = settings.GetValue(ModelFromFileTemplateSettings.ImportMaterials),
            textures = settings.GetValue(ModelFromFileTemplateSettings.ImportTextures), animations = settings.GetValue(ModelFromFileTemplateSettings.ImportAnimations),
            skeleton = settings.GetValue(ModelFromFileTemplateSettings.ImportSkeleton), deduplicate = settings.GetValue(ModelFromFileTemplateSettings.DeduplicateMaterials),
            defaultSkeleton = settings.GetValue(ModelFromFileTemplateSettings.DefaultSkeleton).ToString() });
    }
    internal bool FbxTemplateOriginalWorkspaceClean => !IsPackageMetadataSaving && !AssetTemplatePublicationPending
        && !documents.HasDirtyDocuments && workspace?.HasPackageChanges == false;
    internal AssetImportOptionsInspection? FbxTemplateOptions => assetImportOptionsPanel?.Inspect();
    internal bool FbxTemplateOptionsPending => assetImportOptionsPanel?.HasRequest == true;
    internal object FbxTemplateFixtureEvidence => new { root = fbxTemplateFixtureRoot, inputs = fbxTemplateInputs.ToDictionary(pair => pair.Key, pair => pair.Value), derivedCompanions = fbxDerivedCompanions };
    internal void VerifyFbxTemplateInputs()
    {
        foreach (var entry in fbxTemplateInputs.Concat(fbxTemplateOriginalInputs))
            if (TemplateFileHash(entry.Key) != entry.Value) throw new IOException("FBX importer changed a pinned source or companion: " + entry.Key);
    }
    internal Dictionary<string, string> CaptureFbxBaselineAssetFiles()
        => workspace!.AssetItems.Select(item => item.FullPath.ToOSPath()).Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, TemplateFileHash, StringComparer.OrdinalIgnoreCase);
    internal void VerifyFbxBaselineAssetFiles(IReadOnlyDictionary<string, string> files)
    {
        foreach (var file in files)
            if (!File.Exists(file.Key) || TemplateFileHash(file.Key) != file.Value)
                throw new InvalidDataException("FBX workflow changed an original native asset file: " + file.Key);
    }
    internal FbxNativeOutput[] InspectFbxTemplateOutputs(string[] baseline, bool persisted)
    {
        var before = baseline.ToHashSet(StringComparer.Ordinal);
        var items = workspace!.AssetItems.Where(item => !before.Contains(item.Id.ToString())).ToArray();
        return items.Select(item =>
        {
            if (authoring?.Find(item.Id) is not { IsPublished: true, IsDisposed: false } owner || owner.Document is not IAssetTagDocument canonical)
                throw new InvalidOperationException("Native importer output has no canonical published owner.");
            var path = item.FullPath.ToOSPath();
            var authored = canonical.AuthoredAsset;
            var asset = persisted ? AssetFileSerializer.Load<Asset>(path, assetNamespace: item.Package.Container?.AssetNamespace ?? item.Package.AssetNamespace).Asset : authored;
            if (asset.Id != item.Id || asset.GetType() != authored.GetType()) throw new InvalidDataException("FBX output lost its native ID/type during independent persistence.");
            string[] References(Asset value) => AssetReferenceAnalysis.Visit(value).Where(link => link.Reference is IReference)
                .Select(link => (IReference)link.Reference).Where(reference => reference.Id != AssetId.Empty)
                .Select(reference =>
                {
                    var target = workspace.Session.FindAsset(reference.Id) ?? throw new InvalidDataException("Importer published a dangling native reference.");
                    if (!string.Equals(target.Location.ToString(), reference.Location, StringComparison.Ordinal))
                        throw new InvalidDataException("Importer reference URL does not match its actual native target.");
                    return reference.Id + "|" + reference.Location;
                }).Order(StringComparer.Ordinal).ToArray();
            var references = References(asset);
            if (persisted && !references.SequenceEqual(References(authored))) throw new InvalidDataException("Native serializer changed importer reference IDs or qualified URLs.");
            string[] Sources(Asset value)
            {
                var collector = new SourceFilesCollector();
                return collector.GetSourceFiles(value).Keys.Concat(collector.GetSourceMembers(value)
                    .Where(member => member.Value != null).Select(member => member.Value!)).Distinct().Select(file =>
                {
                    var source = Path.GetFullPath(file.ToOSPath(), Path.GetDirectoryName(path)!);
                    if (!fbxTemplateInputs.TryGetValue(source, out var hash) || TemplateFileHash(source) != hash)
                        throw new InvalidDataException("An importer source is not one of the unchanged private pinned inputs.");
                    return source + "|" + hash;
                }).Order(StringComparer.Ordinal).ToArray();
            }
            string[] Hashes(Asset value)
            {
                var hashAsset = AssetCloner.Clone(value);
                SourceHashesHelper.ConvertPaths(hashAsset, new UDirectory(Path.GetDirectoryName(path)!), UPathType.Absolute);
                var nativeHashes = SourceHashesHelper.GetAllHashes(hashAsset);
                foreach (var pair in nativeHashes)
                {
                    var source = Path.GetFullPath(pair.Key.ToOSPath());
                    using var stream = File.OpenRead(source);
                    var actual = AssetSourceImport.Fingerprint(stream, CancellationToken.None);
                    if (!fbxTemplateInputs.TryGetValue(source, out var expected) || actual.Sha != expected || actual.Object != pair.Value)
                        throw new InvalidDataException("Imported native source ObjectId metadata does not match its unchanged pinned bytes.");
                }
                foreach (var source in Sources(value))
                {
                    var fullPath = source[..source.LastIndexOf('|')];
                    if (!nativeHashes.Keys.Any(key => string.Equals(Path.GetFullPath(key.ToOSPath()), fullPath, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("An actual importer source has no native ObjectId hash metadata.");
                }
                return nativeHashes.Select(pair => pair.Key + "|" + pair.Value).Order(StringComparer.Ordinal).ToArray();
            }
            var sources = Sources(asset); var hashes = Hashes(asset);
            if (persisted && (!sources.SequenceEqual(Sources(authored)) || !hashes.SequenceEqual(Hashes(authored))))
                throw new InvalidDataException("Native serialization changed importer source members or ObjectId metadata.");
            return new FbxNativeOutput(item.Id.ToString(), path, item.Location.ToString(), asset.GetType().FullName!, persisted ? TemplateFileHash(path) : null,
                references, sources, hashes, asset is SkeletonAsset skeleton ? skeleton.Nodes.Count : 0, asset is ModelAsset model ? model.Materials.Count : 0);
        }).OrderBy(output => output.Id, StringComparer.Ordinal).ToArray();
    }
}
