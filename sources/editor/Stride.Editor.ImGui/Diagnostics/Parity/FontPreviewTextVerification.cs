using System.Diagnostics;
using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Stride.Assets.SpriteFont;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private int fontTextVerificationPhase;
    private SpriteFontAssetPreview? fontTextVerificationOwner;
    private AssetMetadataDocument? fontTextVerificationDocument;
    private EditorAuthoringState? fontTextVerificationCanonical;
    private string? fontTextVerificationOriginal, fontTextVerificationHistory, fontTextVerificationArtifacts;
    private byte[]? fontTextVerificationAssetBytes;
    private readonly Dictionary<string, byte[]> fontTextVerificationSources = new(StringComparer.OrdinalIgnoreCase);
    private bool fontTextUiActive, fontTextOriginalDirty;
    private long fontClearAccepted, fontClearBaseline, fontTextQueuedAt, fontTextDeadlineObserved;
    private object? fontClearUiReceipt, fontTextUiReceipt, fontTextEnterUiReceipt;
    private EngineOutputSample? fontTextClearOutput;
    private EngineOutputDifference? fontTextDifference;
    private readonly List<object> fontTextVerificationReceipts = [];
    private const string FontLineOne = "LineOne", FontMultiline = "LineOne\nLineTwo";
    private unsafe void RecordFontPreviewTextUi(SpriteFontAssetPreview preview)
    {
        if (!Controls.Enabled) return;
        fontTextUiActive = Gui.IsItemActive();
        if (ReferenceEquals(preview, fontTextVerificationOwner) && fontTextVerificationPhase is 4 or 7 && fontPreviewTextDeadline is { } deadline)
            fontTextDeadlineObserved = deadline;
        fontTextUiReceipt = new { frame = Gui.GetFrameCount(), nativeId = Gui.GetCurrentContext().LastItemData.ID,
            active = Gui.IsItemActive(), edited = Gui.IsItemEdited(), entered = Gui.IsKeyPressed(ImGuiKey.Enter),
            enterDown = Gui.IsKeyDown(ImGuiKey.Enter), enterReleased = Gui.IsKeyReleased(ImGuiKey.Enter),
            text = fontPreviewText, validated = validatedFontPreviewText, preview.Sample, fontPreviewTextDeadline,
            timestamp = Stopwatch.GetTimestamp(), sameOwner = ReferenceEquals(preview, fontPreview) };
        if (ReferenceEquals(preview, fontTextVerificationOwner) && Gui.IsKeyPressed(ImGuiKey.Enter))
            fontTextEnterUiReceipt = fontTextUiReceipt;
    }
    private unsafe void RecordFontPreviewClearUi(SpriteFontAssetPreview preview, bool pressed)
    {
        if (!Controls.Enabled) return;
        if (pressed) fontClearAccepted++;
        if (pressed) fontClearUiReceipt = new { frame = Gui.GetFrameCount(), nativeId = Gui.GetCurrentContext().LastItemData.ID,
            pressed, nativeReleased = Gui.IsMouseReleased(ImGuiMouseButton.Left), beforeText = fontPreviewText,
            beforeValidated = validatedFontPreviewText, beforeSample = preview.Sample, sameOwner = ReferenceEquals(preview, fontPreview) };
    }
    internal void StartFontPreviewTextVerification(string artifacts)
    {
        if (!args.Contains("--ui-test") || fontTextVerificationOwner != null || fontPreview == null || fontPreviewDocument == null
            || fontPreview.RenderError != null || !VerificationFamilyRendered("font") || fontPreviewTextDeadline != null
            || fontPreview.Sample != validatedFontPreviewText || fontPreviewText != validatedFontPreviewText || sessionHistory == null)
            throw new InvalidOperationException("Font text verification requires a settled actual compiled font fixture.");
        fontTextVerificationOwner = fontPreview; fontTextVerificationDocument = fontPreviewDocument;
        fontTextVerificationCanonical = authoring?.Find(fontPreviewDocument.Path);
        if (fontTextVerificationCanonical == null || !ReferenceEquals(fontTextVerificationCanonical.Document, fontPreviewDocument))
            throw new InvalidOperationException("Font text verification lost its canonical settings owner.");
        fontTextVerificationOriginal = fontPreview.Sample;
        if (fontTextVerificationOriginal.Contains('\n') || fontTextVerificationOriginal.Contains('\r'))
            throw new InvalidOperationException("The representative font fixture must start with its single-line sample.");
        fontTextVerificationAssetBytes = File.ReadAllBytes(fontPreviewDocument.Path);
        fontTextOriginalDirty = fontPreviewDocument.IsDirty;
        fontTextVerificationSources.Clear();
        foreach (var path in FontSourcePaths.Files(fontPreviewDocument.AuthoredAsset, fontPreviewDocument.Path))
            fontTextVerificationSources.Add(path, File.ReadAllBytes(path));
        fontTextVerificationHistory = FontTextHistorySignature();
        fontTextVerificationArtifacts = artifacts; Directory.CreateDirectory(artifacts);
        fontTextVerificationReceipts.Clear(); fontTextVerificationPhase = 1;
        fontClearBaseline = fontClearAccepted;
    }
    private string FontTextHistorySignature() => sessionHistory!.History.Position + ":"
        + string.Join(";", sessionHistory.History.Entries.Select(entry => entry.Id + ":" + entry.IsDone));
    private void RequireFontTextOwnedBaseline()
    {
        if (!ReferenceEquals(fontPreview, fontTextVerificationOwner) || !ReferenceEquals(fontPreviewDocument, fontTextVerificationDocument)
            || !ReferenceEquals(authoring?.Find(fontTextVerificationDocument!.Path), fontTextVerificationCanonical)
            || fontTextVerificationDocument.IsDirty != fontTextOriginalDirty
            || FontTextHistorySignature() != fontTextVerificationHistory
            || !File.ReadAllBytes(fontTextVerificationDocument.Path).SequenceEqual(fontTextVerificationAssetBytes!)
            || fontTextVerificationSources.Any(source => !File.ReadAllBytes(source.Key).SequenceEqual(source.Value)))
            throw new InvalidOperationException("Preview text must not replace native/canonical owners, change authored journals or write font asset bytes.");
    }
    internal bool StepFontPreviewTextVerification(Func<string, bool> click)
    {
        RequireFontTextOwnedBaseline();
        var io = Gui.GetIO();
        switch (fontTextVerificationPhase)
        {
            case 1: if (click("font-preview/clear")) fontTextVerificationPhase++; break;
            case 2:
                if (fontClearAccepted <= fontClearBaseline || fontPreviewText != "" || validatedFontPreviewText != "" || fontPreview!.Sample != "") break;
                if (!VerificationFamilyRendered("font")) break;
                fontTextVerificationReceipts.Add(new { contract = "native-clear", receipt = fontClearUiReceipt });
                fontTextClearOutput = ReadFamilyOutput("font");
                SaveFamilyOutput("font", Path.Combine(fontTextVerificationArtifacts!, "font-clear.png"));
                if (click("font-preview/text")) fontTextVerificationPhase++; break;
            case 3:
                fontTextQueuedAt = Stopwatch.GetTimestamp(); foreach (var character in FontLineOne) io.AddInputCharacter(character);
                fontTextDeadlineObserved = 0; fontTextVerificationPhase++; break;
            case 4:
                if (fontPreviewText != FontLineOne) break;
                fontTextDeadlineObserved = fontPreviewTextDeadline ?? fontTextDeadlineObserved;
                if (fontTextDeadlineObserved == 0 || Stopwatch.GetTimestamp() < fontTextDeadlineObserved
                    || validatedFontPreviewText != FontLineOne || fontPreview!.Sample != FontLineOne || !fontTextUiActive || !VerificationFamilyRendered("font")) break;
                if ((Stopwatch.GetTimestamp() - fontTextQueuedAt) / (double)Stopwatch.Frequency < .5) break;
                var firstLineOutput = ReadFamilyOutput("font");
                fontTextDifference = fontTextClearOutput!.Compare(firstLineOutput, 0, 0, fontTextClearOutput.Width, fontTextClearOutput.Height);
                EngineCapabilityAssertions.VisibleFixtureChange(fontTextDifference.Value, Math.Max(16, fontTextDifference.Value.Pixels / 1000), .99);
                fontTextVerificationReceipts.Add(new { contract = "native-timer-single-line", deadline = fontTextDeadlineObserved,
                    queuedAt = fontTextQueuedAt, validatedAt = Stopwatch.GetTimestamp(), input = fontTextUiReceipt, fontTextDifference });
                io.AddKeyEvent(ImGuiKey.Enter, true); fontTextVerificationPhase++; break;
            case 5:
                if (!Gui.IsKeyDown(ImGuiKey.Enter)) break;
                io.AddKeyEvent(ImGuiKey.Enter, false); fontTextVerificationPhase++; break;
            case 6:
                if (Gui.IsKeyDown(ImGuiKey.Enter) || fontPreviewText != FontLineOne + "\n") break;
                if (fontTextEnterUiReceipt == null) throw new InvalidOperationException("Multiline newline requires actual native Enter acceptance.");
                fontTextVerificationReceipts.Add(new { contract = "native-multiline-enter", input = fontTextEnterUiReceipt, buffer = fontPreviewText });
                fontTextQueuedAt = Stopwatch.GetTimestamp(); foreach (var character in "LineTwo") io.AddInputCharacter(character);
                fontTextDeadlineObserved = 0; fontTextVerificationPhase++; break;
            case 7:
                if (fontPreviewText != FontMultiline) break;
                fontTextDeadlineObserved = fontPreviewTextDeadline ?? fontTextDeadlineObserved;
                if (fontTextDeadlineObserved == 0 || Stopwatch.GetTimestamp() < fontTextDeadlineObserved
                    || validatedFontPreviewText != FontMultiline || fontPreview!.Sample != FontMultiline
                    || (Stopwatch.GetTimestamp() - fontTextQueuedAt) / (double)Stopwatch.Frequency < .5
                    || !fontTextUiActive || !VerificationFamilyRendered("font")) break;
                var metrics = VerificationFontGlyphMetrics(FontMultiline); var single = VerificationFontGlyphMetrics(FontLineOne);
                if (!float.IsFinite(metrics.X) || !float.IsFinite(metrics.Y) || metrics.X <= 0 || metrics.Y <= single.Y)
                    throw new InvalidOperationException("Actual compiled multiline font glyph metrics must contain two positive lines.");
                var multilineOutput = ReadFamilyOutput("font");
                SaveFamilyOutput("font", Path.Combine(fontTextVerificationArtifacts!, "font-multiline.png"));
                fontTextVerificationReceipts.Add(new { contract = "native-timer-multiline-gpu", deadline = fontTextDeadlineObserved,
                    queuedAt = fontTextQueuedAt, validatedAt = Stopwatch.GetTimestamp(), input = fontTextUiReceipt,
                    metrics = new { metrics.X, metrics.Y }, single = new { single.X, single.Y }, multilineOutput.Width, multilineOutput.Height });
                fontClearBaseline = fontClearAccepted; if (click("font-preview/clear")) fontTextVerificationPhase++; break;
            case 8:
                if (fontClearAccepted <= fontClearBaseline || fontPreviewText != "" || fontPreview!.Sample != "") break;
                if (click("font-preview/text")) fontTextVerificationPhase++; break;
            case 9:
                foreach (var character in fontTextVerificationOriginal!) io.AddInputCharacter(character);
                fontTextVerificationPhase++; break;
            case 10:
                if (fontPreviewText != fontTextVerificationOriginal || validatedFontPreviewText != fontTextVerificationOriginal
                    || fontPreview!.Sample != fontTextVerificationOriginal || fontPreviewTextDeadline != null || !VerificationFamilyRendered("font")) break;
                RequireFontTextOwnedBaseline();
                fontTextVerificationReceipts.Add(new { contract = "original-native-sample-owned-history-files-restored",
                    sample = fontPreview.Sample, history = fontTextVerificationHistory,
                    sources = fontTextVerificationSources.Select(source => new { path = source.Key,
                        sha256 = Convert.ToHexString(SHA256.HashData(source.Value)) }).ToArray(),
                    assetSha256 = Convert.ToHexString(SHA256.HashData(fontTextVerificationAssetBytes!)) });
                File.WriteAllText(Path.Combine(fontTextVerificationArtifacts!, "font-preview-text.json"),
                    System.Text.Json.JsonSerializer.Serialize(CaptureFontPreviewTextVerification(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                fontTextVerificationPhase = 11; return true;
            case 11: return true;
        }
        return false;
    }
    internal object CaptureFontPreviewTextVerification() => new { phase = fontTextVerificationPhase, fontPreviewText,
        validatedFontPreviewText, sample = fontPreview?.Sample, deadline = fontPreviewTextDeadline,
        clearAccepted = fontClearAccepted, clearUi = fontClearUiReceipt, textUi = fontTextUiReceipt,
        receipts = fontTextVerificationReceipts.ToArray(), coverage = "Actual native Clear, multiline Enter/input, half-second validation, compiled glyph metrics/GPU and unchanged authored ownership/history/asset bytes." };
    internal void CancelFontPreviewTextVerification()
    {
        Gui.GetIO().AddKeyEvent(ImGuiKey.Enter, false);
        // Failure cleanup is presentation-only; it cannot retire or replace the actual font owner.
        if (fontTextVerificationPhase != 11 && ReferenceEquals(fontPreview, fontTextVerificationOwner) && fontPreview != null)
        {
            fontPreviewText = fontTextVerificationOriginal!; ValidateFontPreviewText(fontPreview, fontTextVerificationOriginal!);
            RequireFontTextOwnedBaseline();
        }
        fontTextVerificationOwner = null;
    }
}
