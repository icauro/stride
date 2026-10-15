using System.Security.Cryptography;
using Stride.Assets.Sprite;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal sealed record SpriteParityCaseResult(string CaseId, string[] FeatureIds, string Coverage,
    string AssetId, int NativeFrameCount, string[] ItemIds, bool BaselineRestored,
    bool NativeMutationSavedAndReloaded, string BaselineSha256, string[] Checks);

/// <summary>Actor-side native sprite authoring/persistence probes; UI gestures remain separate coverage.</summary>
internal static class SpriteParityCases
{
    internal static IReadOnlyList<string> Cases { get; } =
        ["SP01", "SP02", "SP03", "SP04", "SP05", "SP06", "SP07", "SP08", "SP09", "SP10", "SP11", "SP12"];
    internal static string[] Features(string id) => id switch
    {
        "SP01" => ["S01", "S02"], "SP02" => ["S03", "S04", "S08", "S19"],
        "SP03" => ["S05", "S06"], "SP04" => ["S07"], "SP05" => ["S09"],
        "SP06" => ["S11", "S12"], "SP07" => ["S13"], "SP08" => ["S14"],
        "SP09" => ["S17", "S18"], "SP10" => ["S20"], "SP11" => ["S03", "S10"],
        "SP12" => ["S15", "S16"], _ => throw new ArgumentException("Unknown sprite parity case", nameof(id)),
    };

    internal static SpriteParityCaseResult Run(AssetMetadataDocument document, string id, string artifactsDirectory,
        Action validateCanvas, Action validateView, Func<string, string> clipboardRoundTrip)
    {
        var features = Features(id);
        var native = document.SpriteSettings ?? throw new InvalidOperationException("An actual sprite-sheet document must be active.");
        Require(document.SessionHistory != null && document.CanEditSpriteCollection && !document.IsDirty,
            "Sprite cases require a clean, published, editable disposable fixture document.");
        Directory.CreateDirectory(artifactsDirectory);
        document.CommitEdits();
        var original = Snapshot(native);
        var selected = document.SelectedSpriteIds.ToArray();
        var locked = document.SpriteSettingsFrames.Where(frame => document.IsSpriteBordersLocked(frame.Id)).Select(frame => frame.Id).ToArray();
        Exception? failure = null;
        var nativeMutationReloaded = false;
        try
        {
            switch (id)
            {
                case "SP01": validateCanvas(); break;
                case "SP02": Collection(document); break;
                case "SP03": Selection(document); break;
                case "SP04": Name(document); break;
                case "SP05": Region(document); break;
                case "SP06": Pixels(document, artifactsDirectory); break;
                case "SP07": Pivot(document); break;
                case "SP08": Borders(document); break;
                case "SP09": Clipboard(document, clipboardRoundTrip); break;
                case "SP10": Sheet(document); break;
                case "SP11": Import(document, artifactsDirectory); break;
                case "SP12": validateView(); break;
            }
            document.CommitEdits();
            var edited = Snapshot(native);
            if (!edited.SequenceEqual(original))
            {
                Require(document.CanUndo && document.IsDirty, "Real sprite changes must be undoable and dirty.");
                document.Save();
                var reloaded = AssetFileSerializer.Load<SpriteSheetAsset>(document.Path).Asset;
                Require(Snapshot(reloaded).SequenceEqual(edited), "Independent native reload must retain all sprite values, order and ItemIds.");
                nativeMutationReloaded = true;
            }
            else Require(!document.IsDirty, "Selection/view/copy-only operations must leave authored content clean.");
        }
        catch (Exception error) { failure = error; }
        try
        {
            document.CommitEdits();
            for (var count = 0; !Snapshot(native).SequenceEqual(original) && count < 64; count++)
            {
                Require(document.CanUndo, "The case cannot restore its actual authored baseline through undo.");
                document.Undo();
            }
            Require(Snapshot(native).SequenceEqual(original), "A sprite case must restore every baseline field and collection identity.");
            document.Save();
            Require(Snapshot(AssetFileSerializer.Load<SpriteSheetAsset>(document.Path).Asset).SequenceEqual(original),
                "The restored baseline must independently reload before the next grouped case.");
            foreach (var frame in document.SpriteSettingsFrames) document.SetSpriteBordersLocked(frame.Id, locked.Contains(frame.Id));
            document.SelectSpriteFrame(null);
            foreach (var frame in selected) document.SelectSpriteFrame(frame, toggle: true);
        }
        catch (Exception restore)
        {
            throw failure == null ? restore : new AggregateException("Sprite case and baseline restoration failed.", failure, restore);
        }
        if (failure != null) throw new InvalidOperationException("Sprite parity case " + id + " failed; baseline restored.", failure);
        return new(id, features, "Native actor-side graph/history/source-decoder/persistence; synthetic UI and physical platform coverage are separate.",
            native.Id.ToString(), native.Sprites.Count, document.SpriteSettingsFrames.Select(frame => frame.Id.ToString()).ToArray(), true,
            nativeMutationReloaded, Convert.ToHexString(original), Checks(id));
    }

    private static string[] Checks(string id) => id switch
    {
        "SP01" => ["Canonical native sheet ownership", "Actual selected source decoder and completed engine texture rendering"],
        "SP02" => ["Add/duplicate allocate independent ItemIds", "Move/insertion preserve identities and order", "Delete retains untouched siblings", "Native persistence and baseline restoration"],
        "SP03" => ["Range/Ctrl/SelectAll native selection", "Next/previous wrap", "Selection leaves authored content unchanged"],
        "SP04" => ["Unicode authored name retains ItemId", "Undo/redo name", "Native persistence and baseline restoration"],
        "SP05" => ["Region clamps to source bounds", "Canceled canvas mutation rolls back", "Native persistence and baseline restoration"],
        "SP06" => ["Native PNG decoding/pixel color", "Transparency/color-key wand island bounds", "Native region union persistence"],
        "SP07" => ["Half-pixel pivot and origin mode", "Native persistence and baseline restoration"],
        "SP08" => ["Locked border/region coupling", "Cancel restores both fields atomically", "Native persistence and baseline restoration"],
        "SP09" => ["Copy preserves native IDs", "Isolated clipboard invalidates actual paste cache", "Paste allocates fresh ItemId", "Native persistence and baseline restoration"],
        "SP10" => ["Actual sheet parameter fields", "Aggregate selected-frame edit", "Native persistence and baseline restoration"],
        "SP11" => ["Batch import distinct native ItemIds/source paths", "Whole regions use native decoded dimensions", "Native persistence and baseline restoration"],
        "SP12" => ["Actual engine source ownership", "DPI-aware 1:1 and native zoom sequence", "Selected source-region focus", "Highlight state checkpoint; GPU overlay pixels not asserted"],
        _ => [],
    };

    private static void Collection(AssetMetadataDocument document)
    {
        var before = document.SpriteSettingsFrames.Select(frame => frame.Id).ToArray();
        document.AddSpriteFrame();
        var added = document.SelectedSpriteFrame ?? throw new InvalidOperationException("Add must select the new native frame.");
        Require(!before.Contains(added.Id), "Add must allocate an independent native ItemId.");
        document.DuplicateSelectedSpriteFrames();
        var duplicate = document.SelectedSpriteFrame ?? throw new InvalidOperationException("Duplicate must select the new native frame.");
        Require(duplicate.Id != added.Id && !ReferenceEquals(duplicate.Value, added.Value), "Duplicate must preserve content with fresh object and ItemId.");
        document.MoveSelectedSpriteFrames(-1);
        Require(document.SpriteSettingsFrames[^2].Id == duplicate.Id && document.SpriteSettingsFrames[^1].Id == added.Id,
            "Move up must preserve identities while reordering selected frames.");
        document.InsertSpriteFramesBefore([duplicate.Id], before.FirstOrDefault(), after: false);
        if (before.Length > 0) Require(document.SpriteSettingsFrames[0].Id == duplicate.Id, "Insertion must place the actual identified frame before its target.");
        document.DeleteSelectedSpriteFrames();
        Require(document.SpriteSettingsFrames.Count == before.Length + 1 && !document.SpriteSettingsFrames.Any(frame => frame.Id == duplicate.Id),
            "Delete must remove only the selected duplicate, preserving siblings.");
    }
    private static void Selection(AssetMetadataDocument document)
    {
        Require(document.SpriteSettingsFrames.Count >= 2, "Selection fixture needs at least two identified frames.");
        var ids = document.SpriteSettingsFrames.Select(frame => frame.Id).ToArray();
        document.SelectSpriteFrame(ids[0]); document.SelectSpriteFrame(ids[^1], range: true);
        Require(document.SelectedSpriteIds.Count == ids.Length, "Range selection must include every frame between anchor and target.");
        document.SelectSpriteFrame(ids[0], toggle: true);
        Require(!document.SelectedSpriteIds.Contains(ids[0]) && document.SelectedSpriteIds.Count == ids.Length - 1, "Ctrl toggle must preserve other selected frames.");
        document.SelectAllSpriteFrames(); Require(document.SelectedSpriteIds.Count == ids.Length, "Select all must contain native frame identities.");
        document.SelectSpriteFrame(ids[^1]); document.SelectNextSpriteFrame(1);
        Require(document.SelectedSpriteId == ids[0], "Next must wrap to the first sprite.");
        document.SelectNextSpriteFrame(-1); Require(document.SelectedSpriteId == ids[^1], "Previous must wrap to the last sprite.");
    }
    private static void Name(AssetMetadataDocument document)
    {
        var frame = First(document); var identity = frame.Id;
        Set(document, frame, nameof(SpriteInfo.Name), "Sprite parity Ω frame");
        Require(First(document).Id == identity && First(document).Value.Name == "Sprite parity Ω frame", "Rename must preserve ItemId and Unicode authored text.");
        document.Undo(); Require(First(document).Value.Name != "Sprite parity Ω frame" && document.CanRedo, "Rename undo must restore its authored predecessor.");
        document.Redo(); Require(First(document).Value.Name == "Sprite parity Ω frame", "Rename redo must replay the same native field.");
    }
    private static void Region(AssetMetadataDocument document)
    {
        var frame = First(document);
        document.RefreshSpriteImageSizes([(frame.Id, frame.Value.Source?.ToString() ?? "", 16, 16)]);
        document.SetSpriteFrameRegion(frame, new Rectangle(14, 13, 8, 9));
        Require(First(document).Value.TextureRegion == new Rectangle(14, 13, 2, 3), "Numeric region must clamp to actual source bounds.");
        frame = First(document); var region = frame.Value.TextureRegion;
        var field = Field(frame, nameof(SpriteInfo.TextureRegion));
        document.UpdateSpriteCanvasSetting(field, new Rectangle(1, 2, 5, 6)); document.CancelSpriteSettingEdit();
        Require(First(document).Value.TextureRegion == region, "Canceled canvas gesture must restore the previous authored region.");
    }
    private static void Pixels(AssetMetadataDocument document, string artifacts)
    {
        var path = CreatePixels(artifacts); using var source = SpriteSourceImage.Load(path);
        Require(source.Upload.Description.Width == 16 && source.Upload.Description.Height == 16, "Native source decoder must retain source dimensions.");
        var color = source.PickColor(new Int2(5, 6)); Require(color == new Color(0, 255, 0, 255), "Color picker must read the actual source pixel.");
        var transparent = source.FindRegion(new Int2(5, 6), true, Color.Magenta);
        var keyed = source.FindRegion(new Int2(5, 6), false, Color.Magenta);
        Require(transparent == new Rectangle(4, 5, 4, 5) && keyed == transparent, "Native wand must find the opaque island in transparency and color-key modes.");
        var frame = First(document); document.SetSpriteFrameRegion(frame, transparent, resetBorders: true);
        var union = Rectangle.Union(transparent, new Rectangle(1, 1, 2, 2));
        document.SetSpriteFrameRegion(First(document), union);
        Require(First(document).Value.TextureRegion == union, "Wand union must persist its combined region in the actual native graph.");
    }
    private static void Pivot(AssetMetadataDocument document)
    {
        SetSheet(document, nameof(SpriteSheetAsset.Type), SpriteSheetType.Sprite2D);
        var frame = First(document); Set(document, frame, nameof(SpriteInfo.CenterFromMiddle), false);
        Set(document, First(document), nameof(SpriteInfo.Center), new Vector2(3.5f, 4.5f));
        Require(First(document).Value.Center == new Vector2(3.5f, 4.5f) && !First(document).Value.CenterFromMiddle,
            "Sprite pivot must retain half-pixel coordinates and its authored origin mode.");
        Set(document, First(document), nameof(SpriteInfo.CenterFromMiddle), true);
        Require(First(document).Value.CenterFromMiddle, "Pivot origin toggle must use its real native field.");
    }
    private static void Borders(AssetMetadataDocument document)
    {
        SetSheet(document, nameof(SpriteSheetAsset.Type), SpriteSheetType.UI);
        var frame = First(document); document.SetSpriteFrameRegion(frame, new Rectangle(0, 0, 12, 12));
        Set(document, First(document), nameof(SpriteInfo.Borders), new Vector4(2, 3, 4, 5));
        frame = First(document); document.SetSpriteBordersLocked(frame.Id, true);
        document.SetSpriteFrameRegion(frame, new Rectangle(0, 0, 14, 15));
        Require(First(document).Value.Borders == new Vector4(2, 3, 6, 8), "Locked borders must preserve interior extent when region dimensions grow.");
        var region = First(document).Value.TextureRegion; var borders = First(document).Value.Borders;
        document.UpdateSpriteCanvasSetting(Field(First(document), nameof(SpriteInfo.TextureRegion)), new Rectangle(0, 0, 10, 10));
        document.CancelSpriteSettingEdit();
        Require(First(document).Value.TextureRegion == region && First(document).Value.Borders == borders,
            "Cancel must restore both region and coupled locked borders atomically.");
    }
    private static void Clipboard(AssetMetadataDocument document, Func<string, string> roundTrip)
    {
        var frame = First(document); document.SelectSpriteFrame(frame.Id);
        var before = Snapshot(document.SpriteSettings!); var text = document.CopySelectedSpriteFrames();
        Require(text.Length > 0 && Snapshot(document.SpriteSettings!).SequenceEqual(before), "Copy must not mutate canonical native IDs or content.");
        var copied = roundTrip(text); Require(copied == text && document.CanPasteSpriteFrames(copied), "Private clipboard must retain compatible native sprite payload.");
        var count = document.SpriteSettingsFrames.Count; var name = frame.Value.Name;
        document.PasteSpriteFrames(copied);
        var pasted = document.SelectedSpriteFrame;
        Require(document.SpriteSettingsFrames.Count == count + 1 && pasted != null && pasted.Id != frame.Id && pasted.Value.Name == name,
            "Paste must recreate sprite content with a distinct native collection ItemId.");
        Require(!document.CanPasteSpriteFrames("This is not asset clipboard data"), "Incompatible private clipboard data must not be accepted.");
    }
    private static void Sheet(AssetMetadataDocument document)
    {
        SetSheet(document, nameof(SpriteSheetAsset.ColorKeyEnabled), !document.SpriteSettings!.ColorKeyEnabled);
        SetSheet(document, nameof(SpriteSheetAsset.ColorKeyColor), new Color(17, 33, 65, 255));
        SetSheet(document, nameof(SpriteSheetAsset.IsCompressed), !document.SpriteSettings.IsCompressed);
        SetSheet(document, nameof(SpriteSheetAsset.UseSRgbSampling), !document.SpriteSettings.UseSRgbSampling);
        SetSheet(document, nameof(SpriteSheetAsset.GenerateMipmaps), !document.SpriteSettings.GenerateMipmaps);
        SetSheet(document, nameof(SpriteSheetAsset.IsStreamable), !document.SpriteSettings.IsStreamable);
        Require(document.SpriteSettings.ColorKeyColor == new Color(17, 33, 65, 255), "Sheet edits must affect the actual authored color-key field.");
        document.SelectAllSpriteFrames();
        if (document.SelectedSpriteIds.Count > 1)
        {
            var common = document.SpriteCommonSettings.First(setting => setting.First.Node.Name == nameof(SpriteInfo.Name));
            document.UpdateSpriteCommonValue(common, _ => "Shared parity name"); document.EndSpriteCommonEdit();
            Require(document.SpriteSettings.Sprites.All(frame => frame.Name == "Shared parity name"), "Aggregate edit must apply one native value to every selected sprite.");
        }
    }
    private static void Import(AssetMetadataDocument document, string artifacts)
    {
        var source = CreatePixels(artifacts); var before = document.SpriteSettingsFrames.Count;
        document.ImportSpriteFiles([source, source]);
        Require(document.SpriteSettingsFrames.Count == before + 2 && document.SelectedSpriteIds.Count == 2,
            "Batch import must insert and select both actual frames in one native transaction.");
        var frames = document.SelectedSpriteIds.Select(id => document.SpriteSettingsFrames.Single(frame => frame.Id == id)).ToArray();
        Require(frames.Select(frame => frame.Id).Distinct().Count() == 2 && frames.All(frame => frame.Value.Source == new UFile(source)),
            "Imported frames must retain distinct native IDs and their real source file.");
        using var decoded = SpriteSourceImage.Load(source);
        document.EditSpriteFrames("Use whole sprite", () =>
        {
            foreach (var frame in frames) document.SetSpriteFrameRegion(frame,
                new Rectangle(0, 0, decoded.Upload.Description.Width, decoded.Upload.Description.Height));
        });
        Require(frames.All(frame => frame.Value.TextureRegion == new Rectangle(0, 0, 16, 16)), "Whole image must use actual native decoder dimensions for every selected sprite.");
    }
    private static SpriteSettingFrame First(AssetMetadataDocument document)
        => document.SpriteSettingsFrames.FirstOrDefault() ?? throw new InvalidOperationException("Sprite fixture requires at least one frame.");
    private static SpriteSettingField Field(SpriteSettingFrame frame, string name) => frame.Fields.First(field => field.Node.Name == name);
    private static void Set(AssetMetadataDocument document, SpriteSettingFrame frame, string name, object value)
    { document.UpdateSpriteSetting(Field(frame, name), value); document.EndSpriteSettingEdit(); }
    private static void SetSheet(AssetMetadataDocument document, string name, object value)
    { document.UpdateSpriteSetting(document.SpriteSettingsFields.First(field => field.Node.Name == name), value); document.EndSpriteSettingEdit(); }
    private static byte[] Snapshot(SpriteSheetAsset asset)
    { using var stream = new MemoryStream(); AssetFileSerializer.Save(stream, asset, null); return SHA256.HashData(stream.ToArray()); }
    private static string CreatePixels(string directory)
    {
        var path = Path.Combine(directory, "sprite-parity-source.png");
        using var image = Image.New2D(16, 16, 1, PixelFormat.R8G8B8A8_UNorm);
        var pixels = Enumerable.Repeat(new Color(255, 0, 255, 0), 16 * 16).ToArray();
        for (var y = 5; y < 10; y++) for (var x = 4; x < 8; x++) pixels[y * 16 + x] = new Color(0, 255, 0, 255);
        image.PixelBuffer[0].SetPixels(pixels);
        using var file = File.Create(path); image.Save(file, ImageFileType.Png); return path;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
