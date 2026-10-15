using System.Diagnostics;
using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Stride.Core.Assets.Tracking;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record SpriteControlReceipt(SpriteSheetAssetPreview Owner, int Frame, int BeforeFrame, int AfterFrame,
        bool BeforeTextures, bool AfterTextures, bool Focused, bool Active);
    private readonly Dictionary<string, SpriteControlReceipt> spriteControlDrawn = [];
    private readonly Dictionary<string, SpriteControlReceipt> spriteControlAccepted = [];
    private void RecordSpritePreviewControl(string id, bool accepted, SpriteSheetAssetPreview owner, int beforeFrame, bool beforeTextures)
    {
        if (!Controls.Enabled) return;
        var receipt = new SpriteControlReceipt(owner, Gui.GetFrameCount(), beforeFrame, owner.CurrentFrame,
            beforeTextures, owner.SpriteTextures, Gui.IsItemFocused(), Gui.IsItemActive());
        spriteControlDrawn[id] = receipt;
        if (accepted) spriteControlAccepted[id] = receipt;
    }
    private SpritePreviewControlsRun? spritePreviewControls;
    internal void CleanSpritePreviewControlsInput()
    {
        Gui.GetIO().AddKeyEvent(ImGuiKey.Home, false);
        Gui.GetIO().AddKeyEvent(ImGuiKey.RightArrow, false);
        // Mouse press/release belongs to the supplied parent's Click actor.
    }
    internal object? SpritePreviewControlsEvidence => spritePreviewControls?.Inspection;
    internal bool StepSpritePreviewControls(Func<string, bool> click, string assetPath)
    {
        if (!Controls.Enabled || !args.Contains("--ui-test")) throw new InvalidOperationException("Sprite control verification requires its native isolated fixture.");
        spritePreviewControls ??= new(this, assetPath);
        return spritePreviewControls.Step(click);
    }

    private sealed class SpritePreviewControlsRun
    {
        private readonly EditorGame game;
        private readonly SpriteSheetAssetPreview preview;
        private readonly EditorAuthoringState state;
        private readonly IAssetTagDocument document;
        private readonly PreviewFamilyLifetimeVerification lifetime;
        private readonly string path;
        private readonly byte[] originalBytes;
        private readonly Dictionary<string, string> sourceHashes;
        private readonly Guid[] transactions;
        private readonly int position, originalFrame, spriteCount, textureCount;
        private readonly long revision;
        private readonly bool dirty;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<object> receipts = [];
        private EngineOutputSample? firstOutput, restoredOutput;
        private int phase, issuedFrame;
        internal SpritePreviewControlsRun(EditorGame game, string assetPath)
        {
            this.game = game; path = Path.GetFullPath(assetPath);
            preview = game.spriteSheetPreview ?? throw new InvalidOperationException("Open the real compiled sprite preview first.");
            var native = ReadFamilyField(preview, "sheet") as SpriteSheet ?? throw new InvalidOperationException("The real compiled native sprite sheet is unavailable.");
            spriteCount = native.Sprites.Count; textureCount = native.Sprites.Select(sprite => sprite.Texture).Distinct().Count();
            if (preview.SpriteTextures || preview.CurrentFrame != 1 || spriteCount < 2 || textureCount < 1
                || !game.VerificationFamilyRendered("sprite") || preview.RenderError != null)
                throw new InvalidOperationException("Run after the native compiled Next-frame/GPU assertion in Sprites mode.");
            originalFrame = preview.CurrentFrame;
            if (!string.Equals(game.assetPreviewPath, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Sprite control fixture path is not the exact retained preview owner.");
            var item = game.workspace!.FindAsset(path);
            state = game.authoring!.Find(item.Id) ?? throw new InvalidOperationException("The sprite has no canonical authoring generation.");
            document = state.Document as IAssetTagDocument ?? throw new InvalidOperationException("The sprite lacks its real authored asset owner.");
            originalBytes = File.ReadAllBytes(path); revision = document.EditRevision; dirty = document.IsDirty;
            sourceHashes = new SourceFilesCollector().GetSourceFiles(document.AuthoredAsset).Where(pair => pair.Value)
                .Select(pair => Path.GetFullPath(pair.Key.ToOSPath(), Path.GetDirectoryName(path)!)).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(source => source, Hash, StringComparer.OrdinalIgnoreCase);
            position = game.sessionHistory!.History.Position;
            transactions = game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
            lifetime = game.CaptureFamilyLifetime("sprite");
        }
        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        internal object Inspection => new
        {
            phase, elapsedSeconds = clock.Elapsed.TotalSeconds, path, originalFrame, spriteCount, textureCount, receipts,
            frame = preview.CurrentFrame, textures = preview.SpriteTextures, sameOwner = ReferenceEquals(game.spriteSheetPreview, preview),
            accepted = game.spriteControlAccepted.Select(pair => new { action = pair.Key, pair.Value.Frame, pair.Value.BeforeFrame,
                pair.Value.AfterFrame, pair.Value.BeforeTextures, pair.Value.AfterTextures, sameOwner = ReferenceEquals(pair.Value.Owner, preview) }).ToArray(),
            coverage = "Actual compiled sprite preview controls through synthetic native GUI input, GPU output, canonical owner/history/file preservation; no hardware input or authored sprite-edit/compile-generation claim.",
        };
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private void Stable(bool files = false)
        {
            Require(ReferenceEquals(game.spriteSheetPreview, preview) && !preview.IsDisposed && preview.RenderError == null
                && ReferenceEquals(game.authoring!.Find(document.AuthoredAsset.Id), state) && state.IsPublished
                && ReferenceEquals(state.Document, document) && document.EditRevision == revision && document.IsDirty == dirty,
                "Sprite presentation controls changed a canonical owner, revision or authored dirty state.");
            Require(game.sessionHistory!.History.Position == position
                && game.sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).SequenceEqual(transactions),
                "Sprite preview-only controls changed actual native authoring history.");
            var roots = lifetime.Cache!.GetLoadedAssetsSnapshot().Cast<object>().ToArray();
            Require(lifetime.Roots.All(root => roots.Any(value => ReferenceEquals(root, value)))
                && !lifetime.Targets.IsDisposed && game.renderer!.HasVerificationTexture(lifetime.Reference),
                "Sprite controls retired or replaced the retained compiled content/registered GPU target.");
            if (files) Require(File.ReadAllBytes(path).SequenceEqual(originalBytes) && sourceHashes.All(pair => Hash(pair.Key) == pair.Value),
                "Sprite preview controls changed native asset YAML or actual source image bytes.");
        }
        private bool Accepted(string action)
            => game.spriteControlAccepted.TryGetValue(action, out var receipt) && ReferenceEquals(receipt.Owner, preview) && receipt.Frame >= issuedFrame;
        private bool Focused => game.spriteControlDrawn.TryGetValue("frame", out var receipt) && ReferenceEquals(receipt.Owner, preview) && receipt.Focused;
        private bool Click(Func<string, bool> click, string id, int next)
        {
            if (!click("sprite-preview/" + id)) return false;
            issuedFrame = Gui.GetFrameCount(); phase = next; return true;
        }
        private void Key(ImGuiKey key, int next) { Gui.GetIO().AddKeyEvent(key, true); issuedFrame = Gui.GetFrameCount(); phase = next; }
        private bool Rendered => game.VerificationFamilyRendered("sprite") && Gui.GetFrameCount() > issuedFrame;
        private void Output(string action, EngineOutputSample baseline, bool changed)
        {
            var actual = game.ReadFamilyOutput("sprite");
            var delta = baseline.Compare(actual, 0, 0, baseline.Width, baseline.Height);
            if (changed) EngineCapabilityAssertions.VisibleFixtureChange(delta, Math.Max(16, delta.Pixels / 1000), .99);
            else Require(delta.ChangedPixels == 0, "Returning to frame zero did not restore its actual native GPU output.");
            receipts.Add(new { action, delta }); Stable(true);
        }
        internal bool Step(Func<string, bool> click)
        {
            Stable(); Require(clock.Elapsed.TotalSeconds < 40, "Sprite control sequence exceeded its bounded native input/render deadline.");
            switch (phase)
            {
                case 0: Click(click, "frame", 1); break;
                case 1: if (Focused) Key(ImGuiKey.Home, 2); break;
                case 2: Gui.GetIO().AddKeyEvent(ImGuiKey.Home, false); phase = 3; break;
                case 3:
                    if (!Accepted("frame") || !Rendered) break;
                    Require(preview.CurrentFrame == 0, "Actual frame-slider Home must select the first compiled sprite.");
                    firstOutput = game.ReadFamilyOutput("sprite"); receipts.Add(new { action = "Native slider Home", frame = preview.CurrentFrame });
                    Click(click, "previous", 4); break;
                case 4:
                    if (!Accepted("previous") || !Rendered) break;
                    var wrap = game.spriteControlAccepted["previous"];
                    Require(wrap.BeforeFrame == 0 && wrap.AfterFrame == spriteCount - 1 && preview.CurrentFrame == spriteCount - 1,
                        "Previous must wrap from zero to the final actual compiled sprite.");
                    Output("Previous wrap GPU", firstOutput!, true); Click(click, "frame", 5); break;
                case 5: if (Focused) Key(ImGuiKey.Home, 6); break;
                case 6: Gui.GetIO().AddKeyEvent(ImGuiKey.Home, false); phase = 7; break;
                case 7:
                    if (!Accepted("frame") || !Rendered) break;
                    Require(preview.CurrentFrame == 0, "Slider must scrub back to compiled frame zero."); Output("Slider restores first-frame GPU", firstOutput!, false);
                    if (Focused) Key(ImGuiKey.RightArrow, 8); break;
                case 8: Gui.GetIO().AddKeyEvent(ImGuiKey.RightArrow, false); phase = 9; break;
                case 9:
                    if (!Accepted("frame") || !Rendered) break;
                    Require(preview.CurrentFrame == 1, "Actual slider Right must advance one compiled frame.");
                    restoredOutput = game.ReadFamilyOutput("sprite"); Click(click, "mode", 10); break;
                case 10: Click(click, "mode/textures", 11); break;
                case 11:
                    if (!Accepted("mode/textures") || !Rendered) break;
                    Require(preview.SpriteTextures && preview.FrameCount == textureCount && preview.CurrentFrame == Math.Min(1, textureCount - 1),
                        "Texture display mode must expose actual distinct compiled textures and clamp its selected frame.");
                    Output("Sprites to Texture native GPU", restoredOutput!, true); Click(click, "mode", 12); break;
                case 12: Click(click, "mode/sprites", 13); break;
                case 13:
                    if (!Accepted("mode/sprites") || !Rendered) break;
                    Require(!preview.SpriteTextures && preview.FrameCount == spriteCount, "Sprites mode did not restore actual compiled sprite regions.");
                    Click(click, "frame", 14); break;
                case 14: if (Focused) Key(ImGuiKey.Home, 15); break;
                case 15: Gui.GetIO().AddKeyEvent(ImGuiKey.Home, false); phase = 16; break;
                case 16:
                    // A no-op Home may retain the same frame; require actual focus/render, not a forged change receipt.
                    if (!Focused || !Rendered) break;
                    Require(preview.CurrentFrame == 0, "Final native slider Home did not restore frame zero."); Key(ImGuiKey.RightArrow, 17); break;
                case 17: Gui.GetIO().AddKeyEvent(ImGuiKey.RightArrow, false); phase = 18; break;
                case 18:
                    if (!Accepted("frame") || !Rendered) break;
                    Require(preview.CurrentFrame == originalFrame && !preview.SpriteTextures, "Sprite control verification did not restore the initial selected frame/mode.");
                    Stable(true); receipts.Add(new { action = "Original frame/mode and native owners/files/history restored", originalFrame }); return true;
            }
            return false;
        }
    }
}
