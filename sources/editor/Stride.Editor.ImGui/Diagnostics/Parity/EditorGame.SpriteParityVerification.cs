using Hexa.NET.ImGui;
using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorDocumentEntry? spriteParityPreviousDocument;
    private EditorDocumentEntry? spriteParityFixtureDocument;

    internal object PrepareSpriteParityFixture(string ownedDirectory)
    {
        var current = workspace ?? throw new InvalidOperationException("Load the disposable project once before preparing grouped sprite cases.");
        var package = current.Package ?? throw new InvalidOperationException("An editable native package is required.");
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(package.FullPath.ToOSPath()).StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)
            || !IsCurrentEditableAssetPackage(package) || spriteParityFixtureDocument != null)
            throw new InvalidOperationException("Sprite fixture creation requires its private loaded package and no existing fixture owner.");
        Directory.CreateDirectory(ownedDirectory);
        var source = Path.GetFullPath(Path.Combine(ownedDirectory, "sprite-parity-fixture-source.png"));
        if (File.Exists(source)) throw new IOException("The owned sprite fixture source already exists.");
        using (var image = Image.New2D(32, 32, 1, PixelFormat.R8G8B8A8_UNorm))
        {
            var pixels = Enumerable.Repeat(new Color(255, 0, 255, 0), 32 * 32).ToArray();
            for (var y = 3; y < 12; y++) for (var x = 2; x < 10; x++) pixels[y * 32 + x] = new Color(0, 255, 0, 255);
            for (var y = 16; y < 24; y++) for (var x = 16; x < 24; x++) pixels[y * 32 + x] = new Color(255, 128, 0, 255);
            image.PixelBuffer[0].SetPixels(pixels);
            using var file = File.Create(source); image.Save(file, ImageFileType.Png);
        }
        var sheet = new SpriteSheetAsset { Type = SpriteSheetType.Sprite2D };
        sheet.Sprites.Add(new SpriteInfo { Name = "Parity green", Source = new UFile(source), TextureRegion = new Rectangle(2, 3, 8, 9), PixelsPerUnit = 100, CenterFromMiddle = true });
        sheet.Sprites.Add(new SpriteInfo { Name = "Parity orange", Source = new UFile(source), TextureRegion = new Rectangle(16, 16, 8, 8), PixelsPerUnit = 100, CenterFromMiddle = true });
        AssetCollectionItemIdHelper.GenerateMissingItemIds(sheet);
        var leaf = "SpriteParity-" + Guid.NewGuid().ToString("N");
        var relative = new UFile(leaf);
        var qualified = package.Container?.Qualify(relative) ?? relative;
        var item = new AssetItem(qualified, sheet) { SourceFolder = new UDirectory("Assets") };
        var path = Path.GetFullPath(Path.Combine(package.RootDirectory.ToOSPath(), "Assets", leaf + SpriteSheetAsset.FileExtension));
        spriteParityPreviousDocument = documents.Active;
        CreatePreparedAsset(package, item, path, "Create disposable sprite parity fixture");
        // Use the sprite document route from OpenDefaultBrowserAsset. The
        // similarly named OpenAssetEditor belongs only to procedural models.
        var createdEntry = current.Assets.Single(asset => asset.Id == sheet.Id);
        var metadata = GetAssetTagDocument(createdEntry);
        if (documents.Find(metadata) is { } opened) ActivateDocument(opened);
        spriteParityFixtureDocument = documents.Find(path)
            ?? throw new InvalidOperationException("The native sprite fixture document was not opened.");
        if (spriteParityFixtureDocument.Document is not AssetMetadataDocument { SpriteSettings: not null } document)
            throw new InvalidOperationException("The sprite fixture did not publish its native metadata owner.");
        document.SelectSpriteFrame(document.SpriteSettingsFrames[0].Id); document.Save();
        return new { assetId = sheet.Id.ToString(), assetPath = path, sourcePath = source, sourceWidth = 32, sourceHeight = 32,
            frameIds = document.SpriteSettingsFrames.Select(frame => frame.Id.ToString()).ToArray(), cases = SpriteParityCases.Cases };
    }

    internal void CloseSpriteParityFixture()
    {
        if (spriteParityFixtureDocument is { } entry && documents.Entries.Contains(entry))
        {
            if (entry.Document.IsDirty) throw new InvalidOperationException("Restore the grouped sprite baseline before closing its fixture.");
            CloseDocument(entry);
        }
        spriteParityFixtureDocument = null;
        if (spriteParityPreviousDocument is { } previous && documents.Entries.Contains(previous)) ActivateDocument(previous);
        spriteParityPreviousDocument = null;
    }

    internal SpriteParityCaseResult RunSpriteParityCase(string caseId, string artifactsDirectory)
    {
        if (documents.Active?.Document is not AssetMetadataDocument { SpriteSettings: not null } document
            || authoring?.Find(document.AuthoredAsset.Id) is not { IsPublished: true, IsDisposed: false } owner
            || !ReferenceEquals(owner.Document, document))
            throw new InvalidOperationException("The grouped sprite probe requires its active canonical published sheet.");
        // Never run authored probes against an interactive user's working asset.
        var fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(document.Path).StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sprite parity probes require a disposable generated test fixture.");
        ValidatePendingPropertyStrings();
        if (caseId == "SP01" && !SpriteParityNativeCompilerReady)
            throw new InvalidOperationException("SP01 requires the actual namespaced native sheet compiler publication, not only the source canvas.");
        var result = SpriteParityCases.Run(document, caseId, artifactsDirectory,
            () => ValidateSpriteParityCanvas(document), () => ValidateSpriteParityView(document), SpriteParityClipboardRoundTrip);
        return caseId == "SP01" ? result with { Checks = result.Checks.Append("Actual native namespaced sprite sheet and compiled texture resources installed at the unchanged authored runtime URL").ToArray() } : result;
    }

    internal bool SpriteParityNativeCompilerReady
    {
        get
        {
            if (spriteParityFixtureDocument?.Document is not AssetMetadataDocument { SpriteSettings: not null } document
                || workspace == null || authoring?.Find(document.AuthoredAsset.Id) is not { IsPublished: true, IsDisposed: false } owner
                || !ReferenceEquals(owner.Document, document)) return false;
            var item = workspace.FindAsset(document.AuthoredAsset.Id);
            var url = item.Location.ToString();
            if (spriteSheetBuild != null) return false;
            if (!refreshedSpriteSheets.TryGetValue(url, out var installed) || installed.AssetId != item.Id || !HasCurrentSpriteSheetContent(installed))
            {
                if (compiledSpriteRevisions.TryGetValue(document, out var attempted) && attempted == SpriteRevision(document))
                    throw new InvalidOperationException("The actual sprite compiler attempted the current fixture but did not publish its namespaced runtime resource: " + url + "; " + StatusMessage);
                return false;
            }
            if (installed.PendingBinding) return false;
            var sheet = installed.Content.Sheet ?? throw new InvalidOperationException("The native sprite bundle contains no runtime sheet.");
            if (sheet.Sprites.Count != document.SpriteSettings.Sprites.Count || sheet.Sprites.Count == 0
                || sheet.Sprites.Any(sprite => sprite.Texture == null || sprite.Texture.Width <= 0 || sprite.Texture.Height <= 0))
                throw new InvalidOperationException("The native sprite sheet compiler lost the fixture frames or compiled texture resources.");
            return true;
        }
    }

    internal bool SpriteParityCanvasReady
        => documents.Active?.Document is AssetMetadataDocument { SpriteSettings: not null } document
            && spriteCanvases.TryGetValue(document, out var state) && state.Pixels != null
            && state.Preview is { RenderCount: > 0, RenderError: null } && state.Error == null;

    private void ValidateSpriteParityCanvas(AssetMetadataDocument document)
    {
        if (!spriteCanvases.TryGetValue(document, out var state) || state.Pixels == null
            || state.Preview is not { RenderCount: > 0, RenderError: null } preview || state.Error != null
            || state.FrameId is not { } frameId)
            throw new InvalidOperationException("The actual source-decoded engine sprite canvas must finish rendering before this case.");
        var frame = document.SpriteSettingsFrames.Single(frame => frame.Id == frameId);
        var source = Path.GetFullPath(frame.Value.Source, Path.GetDirectoryName(document.Path)!);
        if (!StringComparer.OrdinalIgnoreCase.Equals(source, state.Source) || preview.Width <= 0 || preview.Height <= 0)
            throw new InvalidOperationException("Canvas rendering must belong to its real selected sprite source.");
        _ = renderer!.RequireVerificationTexture(preview.TextureRef);
    }

    private void ValidateSpriteParityView(AssetMetadataDocument document)
    {
        ValidateSpriteParityCanvas(document);
        var state = spriteCanvases[document]; var preview = state.Preview!;
        var scale = state.ViewScale; var scroll = state.ViewScroll; var restore = state.RestoreViewScroll;
        var highlight = state.Highlight; var color = state.BorderColor;
        try
        {
            ChangeSpriteCanvasScale(state, preview, 1);
            if (Math.Abs(preview.Scale - state.ViewDpi) > .001f) throw new InvalidOperationException("1:1 must respect the actual DPI scale.");
            var region = document.SpriteSettingsFrames.Single(frame => frame.Id == state.FrameId).Value.TextureRegion;
            var center = new Stride.Core.Mathematics.Vector2(region.X + region.Width * .5f, region.Y + region.Height * .5f);
            FocusSpriteCanvasRegion(state, preview, center);
            var expected = new System.Numerics.Vector2(center.X, center.Y) * preview.Scale - state.ScrollSize * .5f;
            if (System.Numerics.Vector2.DistanceSquared(state.ViewScroll, expected) > .001f || !state.RestoreViewScroll)
                throw new InvalidOperationException("Focus must center the real selected source-region pixels.");
            state.Highlight = !highlight; state.BorderColor = new(0, 1, 0, 1);
            if (state.Highlight == highlight) throw new InvalidOperationException("Highlight state must toggle without authored changes.");
            ZoomSpriteCanvas(state, preview, true);
            if (state.ViewScale <= 1) throw new InvalidOperationException("Zoom in must use the original native zoom sequence.");
        }
        finally
        {
            state.Highlight = highlight; state.BorderColor = color;
            ChangeSpriteCanvasScale(state, preview, scale);
            state.ViewScroll = scroll; state.RestoreViewScroll = restore;
        }
    }

    private string SpriteParityClipboardRoundTrip(string value)
    {
        if (!args.Contains("--ui-test"))
            throw new InvalidOperationException("Sprite clipboard probes require the explicitly isolated UI-test clipboard.");
        var before = Gui.GetClipboardTextS() ?? "";
        try
        {
            var document = documents.Active?.Document as AssetMetadataDocument
                ?? throw new InvalidOperationException("The sprite clipboard must retain its actual document owner.");
            Gui.SetClipboardText(value);
            if (!CanPasteSprites(document)) throw new InvalidOperationException("Compatible private sprite data must enable the actual paste command.");
            Gui.SetClipboardText("Incompatible private clipboard replacement");
            if (CanPasteSprites(document)) throw new InvalidOperationException("Replacing private clipboard contents must invalidate the actual paste cache.");
            Gui.SetClipboardText(value);
            if (!CanPasteSprites(document)) throw new InvalidOperationException("Restoring native clipboard data must re-enable the actual paste command.");
            return Gui.GetClipboardTextS() ?? "";
        }
        finally { Gui.SetClipboardText(before); }
    }
}
