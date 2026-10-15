using System.Security.Cryptography;
using Stride.Assets.Models;
using Stride.Physics.Assets;
using Stride.Assets.Sprite;
using Stride.Assets.Skyboxes;
using Stride.Assets.Textures;
using Stride.Core.Serialization;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed record AssetTemplateProof(string Id, string Path, string Location, string Type, string DiskHash,
    string? InputHash, string? SourceHash, int SpriteCount, int Width, int Height, string? ReferenceId);
internal sealed partial class EditorGame
{
    private sealed record TemplateParitySource(ProjectWorkspace Workspace, Package Package, EditorSessionHistory History,
        AssetCreationPanel Panel, AssetCreationPanel.Choice Choice, string File, string Hash);
    private TemplateParitySource? templateParitySource;
    internal object? AssetTemplateTargetGuardReceipt { get; private set; }
    private void RecordAssetTemplateTargetGuard(ProjectWorkspace owner, Package package, AssetBrowserFolder? folder,
        AssetCreationPanel.Choice choice, string name, bool sameOwner, bool eligible, bool packageCurrent)
    {
        if (!Controls.Enabled) return;
        var browser = AssetCreationBrowser;
        var selectedFolder = browser.SelectedFolder;
        var scope = browser.CaptureNavigationScope();
        var receipt = new
        {
            utc = DateTimeOffset.UtcNow, frame = Hexa.NET.ImGui.ImGui.GetFrameCount(), name,
            templateId = choice.Template?.Id, assetType = choice.AssetType.FullName,
            sameOwner, eligible, packageCurrent,
            originalPackagePath = owner.PackagePath, currentPackagePath = workspace?.PackagePath,
            targetPackagePath = package.FullPath.ToOSPath(), targetPackageState = package.State.ToString(), package.IsReadOnly,
            targetPackageInSession = workspace?.Session.Packages.Contains(package) == true,
            targetPackageSessionCurrent = ReferenceEquals(package.Session, workspace?.Session),
            originalFolder = folder?.Id, selectedFolder = selectedFolder?.Id, scope,
            selectedFolderEditable = selectedFolder != null && IsCurrentEditableAssetPackage(selectedFolder.Package),
            ownerAvailable = authoring != null, historyAvailable = sessionHistory != null,
            projectLoading = projectLoading != null, previewBuild = assetPreviewBuild != null,
            authoringPipeline = IsAuthoringPipelineUpdating, userProjectBuilding = IsUserProjectBuilding, importUpdating = IsAssetImportUpdating,
            resourceCopyPending = resourceCopyRequest != null, recoveryTransition = RecoveryTransitionBusy,
            extensionsStopping = editorExtensionsStopping, closeRequested, closePromptOpen, closingDocument = closingDocument != null,
            canceledResourceCopies = executingResourceCopies.Count(request => request.Authority.Canceled),
            retiredPickerCertificates = retiredPickerCreationCertificates.Count, pickerCreationCurrent = PickerCreationCurrent,
            selectedNativeAssetIds = browser.CaptureSelection().Select(id => id.ToString()).ToArray(),
            draft = ownedAssetCreation?.CaptureTemplateDraft(),
        };
        AssetTemplateTargetGuardReceipt = receipt;
        try
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(root)) return;
            var directory = Path.Combine(root, "asset-templates"); Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "target-guard-receipts.jsonl"), System.Text.Json.JsonSerializer.Serialize(receipt) + Environment.NewLine);
        }
        catch (Exception error) { Console.Error.WriteLine("Native target guard receipt could not be retained: " + error); }
    }
    private long templateParitySaveAccepted;
    private bool templateParitySaveEligible;
    private int templateParitySaveUiFrame;
    private void RecordAssetTemplateSaveUiReceipt(bool pressed, bool eligible)
    {
        if (!Controls.Enabled) return;
        templateParitySaveEligible = eligible; templateParitySaveUiFrame = Hexa.NET.ImGui.ImGui.GetFrameCount();
        if (pressed) templateParitySaveAccepted++;
    }
    internal (long Accepted, bool Eligible, int Frame) AssetTemplateSaveUiReceipt => (templateParitySaveAccepted, templateParitySaveEligible, templateParitySaveUiFrame);
    internal string[] InspectAssetTemplateTargetNavigation()
    {
        var package = workspace?.Package ?? throw new InvalidOperationException("No loaded native editable project.");
        var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, package));
        var mount = root.Children.Single(folder => !folder.IsCode && folder.RelativePath.Length == 0);
        return [root.Id, mount.Id];
    }
    internal bool IsAssetTemplateTargetLocationSelected(string locationId) => assetBrowser.SelectedFolder?.Id == locationId
        && assetBrowser.SelectedFolder is { IsCode: false } folder && IsCurrentEditableAssetPackage(folder.Package)
        && assetBrowser.CaptureNavigationScope() is { EntireWorkspace: false, Folders.Length: 1 };
    internal bool AssetTemplateParityReady => CanAddAsset && !AssetCreation.HasRequest;
    internal int AssetTemplateHistoryPosition => sessionHistory?.History.Position ?? -1;
    internal bool AssetTemplatePublicationPending => assetPreviewBuild != null || IsUserProjectBuilding || IsAuthoringPipelineUpdating || IsAssetImportUpdating || resourceCopyRequest != null || factorySourceTask != null || factorySourceRequest != null || retiredFactorySources.Count != 0;
    internal string[] AssetTemplateNativeIds() => workspace!.AssetItems.Select(item => item.Id.ToString()).Order().ToArray();
    internal void OpenAssetTemplateParityChooser()
    {
        var current = workspace ?? throw new InvalidOperationException("Load the private project first.");
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(current.PackagePath).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase) || !AssetTemplateParityReady)
            throw new InvalidOperationException("Template acceptance requires a ready disposable project.");
        RequestAssetCreation();
        if (!AssetCreation.HasRequest) throw new InvalidOperationException("The actual template chooser did not open.");
    }
    internal AssetCreationPanel.Choice AssetTemplateParityChoice(string branch) => AssetCreation.InspectTemplateChoices().Single(choice => branch switch
    {
        "procedural" => choice.AssetType == typeof(ProceduralModelAsset) && choice.Name == "Cube",
        "skybox" => choice.AssetType == typeof(SkyboxAsset) && choice.Template?.Id == Guid.Parse("F16AB154-86A6-4020-B410-88FBD87D3CA5"),
        "heightmap" => choice.AssetType == typeof(HeightmapAsset) && choice.Template?.ImportSource == true,
        "spritesheet" => choice.AssetType == typeof(SpriteSheetAsset) && choice.Template?.Id == Guid.Parse("3778061A-5621-46F0-9091-B7C7E725237D"),
        _ => false,
    });
    internal int AssetTemplateNativePublicationCount(string[] before)
    {
        var ids = before.ToHashSet(StringComparer.Ordinal);
        return workspace!.AssetItems.Count(item => !ids.Contains(item.Id.ToString()));
    }
    internal object CaptureAssetTemplatePublicationState(string[] before)
    {
        var ids = before.ToHashSet(StringComparer.Ordinal);
        return new { saving = IsPackageMetadataSaving, packageDirty = workspace?.HasPackageChanges,
            assets = workspace!.AssetItems.Where(item => !ids.Contains(item.Id.ToString())).Select(item => new {
                id = item.Id.ToString(), path = item.FullPath.ToOSPath(), fileExists = File.Exists(item.FullPath.ToOSPath()),
                nativeDirty = item.IsDirty, canonicalDirty = authoring?.Find(item.Id)?.Document.IsDirty,
                published = authoring?.Find(item.Id)?.IsPublished }).ToArray() };
    }
    internal bool AssetTemplateSavedPublicationsReady(string[] before)
    {
        if (IsPackageMetadataSaving || AssetTemplatePublicationPending) return false;
        var ids = before.ToHashSet(StringComparer.Ordinal);
        return workspace!.AssetItems.Where(item => !ids.Contains(item.Id.ToString())).All(item => File.Exists(item.FullPath.ToOSPath())
            && !item.IsDirty && authoring?.Find(item.Id) is { IsPublished: true, IsDisposed: false } owner && !owner.Document.IsDirty);
    }
    internal object? CaptureAssetTemplateParityDraft() => ownedAssetCreation?.CaptureTemplateDraft();
    internal bool AssetTemplateSelected(AssetCreationPanel.Choice choice) => ReferenceEquals(AssetCreation.InspectSelectedTemplate(), choice);
    internal bool AssetTemplateSelectionMatches(AssetCreationPanel.Choice choice, string name) => ReferenceEquals(AssetCreation.InspectSelectedTemplate(), choice)
        && AssetCreation.InspectTemplateName() == name;
    internal bool AssetTemplateChooserPending => AssetCreation.HasRequest;
    internal string PrepareAssetTemplateParitySource(string branch)
    {
        var package = AssetCreationBrowser.SelectedFolder?.Package ?? throw new InvalidOperationException("No native target folder.");
        var roots = ResourceDirectories(package);
        var root = roots.FirstOrDefault(path => Path.GetFullPath(path).StartsWith(Path.GetFullPath(package.RootDirectory.ToOSPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The private fixture needs a native ResourceFolders entry inside its project.");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "TemplateParity-" + branch + "-" + Guid.NewGuid().ToString("N") + ".png");
        using var image = Image.New2D(16, 16, 1, PixelFormat.R8G8B8A8_UNorm);
        image.PixelBuffer[0].SetPixels(Enumerable.Range(0, 256).Select(i => new Color((byte)i, (byte)(255-i), (byte)64, (byte)255)).ToArray());
        using (var stream = File.Create(path)) image.Save(stream, ImageFileType.Png);
        return path;
    }
    internal void ArmAssetTemplateParitySource(AssetCreationPanel.Choice choice, string file)
    {
        if (!Controls.Enabled || templateParitySource != null || !ReferenceEquals(AssetCreation.InspectSelectedTemplate(), choice))
            throw new InvalidOperationException("A diagnostic source choice must retain the actual selected native template.");
        templateParitySource = new(workspace!, AssetCreationBrowser.SelectedFolder!.Package, sessionHistory!, AssetCreation, choice, file, TemplateFileHash(file));
    }
    private bool TryConsumeTemplateParitySources(Package package, out string[] files)
    {
        files = [];
        if (templateParitySource is not { } request) return false;
        templateParitySource = null;
        if (!Controls.Enabled || !ReferenceEquals(workspace, request.Workspace) || !ReferenceEquals(package, request.Package)
            || !ReferenceEquals(sessionHistory, request.History) || !ReferenceEquals(AssetCreation, request.Panel)
            || !ReferenceEquals(AssetCreation.InspectSelectedTemplate(), request.Choice) || TemplateFileHash(request.File) != request.Hash)
            throw new InvalidOperationException("The diagnostic source choice owner or input changed.");
        files = [request.File]; return true;
    }
    internal void ClearAssetTemplateParitySource() => templateParitySource = null;
    internal AssetTemplateProof[] InspectAssetTemplatePublications(string[] before, string? input)
    {
        var prior = before.ToHashSet(StringComparer.Ordinal);
        return workspace!.AssetItems.Where(item => !prior.Contains(item.Id.ToString())).Select(item =>
        {
            var path = item.FullPath.ToOSPath();
            var disk = AssetFileSerializer.Load<Asset>(path).Asset;
            if (disk.Id != item.Id || disk.GetType() != item.Asset.GetType()) throw new InvalidOperationException("Published asset identity/type did not survive the native serializer.");
            if (disk is ProceduralModelAsset model && model.Type is not Stride.Rendering.ProceduralModels.CubeProceduralModel)
                throw new InvalidOperationException("The selected Cube registration published a different procedural model.");
            string? source = (disk as IAssetWithSource)?.Source?.ToOSPath();
            var sprites = disk as SpriteSheetAsset;
            if (sprites != null) source = sprites.Sprites.Single().Source.ToOSPath();
            if (source != null && !Path.IsPathRooted(source)) source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, source));
            var info = sprites?.Sprites.Single();
            return new AssetTemplateProof(item.Id.ToString(), path, item.Location.ToString(), disk.GetType().FullName!, TemplateFileHash(path),
                input == null ? disk is SkyboxAsset ? AssetTemplateReferenceInputHash : null : TemplateFileHash(input), source == null ? null : TemplateFileHash(source), sprites?.Sprites.Count ?? 0,
                info?.TextureRegion.Width ?? 0, info?.TextureRegion.Height ?? 0,
                disk is SkyboxAsset skybox ? AttachedReferenceManager.GetAttachedReference(skybox.CubeMap)?.Id.ToString() : null);
        }).ToArray();
    }
    internal string AssetTemplateReferenceId => workspace!.AssetItems.Single(item => item.Asset is TextureAsset
        && item.UnqualifiedUrl.ToString() == "TemplateReferenceTexture" && ReferenceEquals(item.Package, AssetCreationBrowser.SelectedFolder!.Package)).Id.ToString();
    internal string AssetTemplateReferenceInputHash
    {
        get
        {
            var item = workspace!.AssetItems.Single(item => item.Id.ToString() == AssetTemplateReferenceId);
            var texture = (TextureAsset)item.Asset;
            var source = texture.Source.ToOSPath();
            if (!Path.IsPathRooted(source)) source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(item.FullPath.ToOSPath())!, source));
            return TemplateFileHash(source);
        }
    }
    internal bool AssetTemplateReferenceChosen => AssetCreation.InspectTemplateReference()?.ToString() == AssetTemplateReferenceId;
    internal bool AssetTemplateReferenceEmpty => AssetCreation.InspectTemplateReference() == null;
    internal void PrepareAssetTemplateReferenceFixture(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Reference fixture must belong to the disposable project.");
        var package = AssetFileSerializer.Load<Package>(full).Asset;
        var packageOwner = package.Container ?? new StandalonePackage(package);
        if (!package.ResourceFolders.Any(folder => folder.ToString().Replace('\\', '/').TrimEnd('/') == "Resources"))
            throw new InvalidOperationException("The disposable fixture requires its original native Resources mount.");
        var root = Path.GetDirectoryName(full)!;
        var resources = Path.Combine(root, "Resources"); var assets = Path.Combine(root, "Assets");
        Directory.CreateDirectory(resources); Directory.CreateDirectory(assets);
        var png = Path.Combine(resources, "TemplateReferenceTexture.png");
        var texturePath = Path.Combine(assets, "TemplateReferenceTexture.sdtex");
        if (File.Exists(png) || File.Exists(texturePath)) throw new IOException("Reference fixture destination is occupied.");
        using (var image = Image.New2D(16, 16, 1, PixelFormat.R8G8B8A8_UNorm))
        {
            image.PixelBuffer[0].SetPixels(Enumerable.Repeat(new Color((byte)96, (byte)128, (byte)160, (byte)255), 256).ToArray());
            using var stream = File.Create(png); image.Save(stream, ImageFileType.Png);
        }
        var texture = new TextureAsset { Source = new UFile("../Resources/TemplateReferenceTexture.png"), IsCompressed = false };
        SaveStockNativeFixture(texturePath, texture, "TemplateReferenceTexture");
        package.RootAssets.Add(new AssetReference(texture.Id, packageOwner.Qualify(new UFile("TemplateReferenceTexture"))));
        AssetFileSerializer.Save(full, package, null);
        if (AssetFileSerializer.Load<TextureAsset>(texturePath).Asset.Id != texture.Id) throw new InvalidOperationException("Reference fixture native identity failed serialization.");
    }
    internal static string TemplateFileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
