using System.Security.Cryptography;
using Stride.Assets.Rendering;
using Stride.Core.Assets;
using Stride.Rendering.Compositing;
using Stride.Rendering.SubsurfaceScattering;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string CompositorSlotFixtureName = "CapabilitySlotCompositor";
    private Guid[] compositorSlotFixtureRendererIds = [];
    internal void PrepareCompositorSlotParityFixture(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        var ownedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || !full.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compositor slot preparation requires the disposable native project.");
        var first = new ForwardRenderer { Name = "Original slot renderer" };
        var second = new ForwardRenderer { Name = "Replacement slot renderer" };
        var incompatible = new SubsurfaceScatteringBlur { Name = "Image-only renderer" };
        var asset = new GraphicsCompositorAsset { Game = first };
        asset.SharedRenderers.Add(first); asset.SharedRenderers.Add(second); asset.SharedRenderers.Add(incompatible);
        compositorSlotFixtureRendererIds = [first.Id, second.Id, incompatible.Id];
        var path = Path.Combine(Path.GetDirectoryName(full)!, "Assets", CompositorSlotFixtureName + GraphicsCompositorAsset.FileExtension);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The compositor slot fixture destination is occupied.");
        SaveStockNativeFixture(path, asset, CompositorSlotFixtureName);
    }

    internal object VerifyCompositorSlotParity()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test") || workspace == null || sessionHistory == null)
            throw new InvalidOperationException("Compositor slot verification requires the actual loaded private project.");
        var entry = workspace.Assets.Single(asset => asset.Name == CompositorSlotFixtureName && asset.AssetType == typeof(GraphicsCompositorAsset));
        var source = GetAssetTagDocument(entry, activate: false, openView: false) as AssetMetadataDocument
            ?? throw new InvalidOperationException("The native compositor owner is unavailable.");
        var asset = source.CompositorAsset ?? throw new InvalidOperationException("The authored native compositor is unavailable.");
        var owner = authoring?.Find(asset.Id) ?? throw new InvalidOperationException("The canonical compositor generation is absent.");
        if (!owner.IsPublished || !ReferenceEquals(owner.Document, source) || source.IsDirty || !source.CanEditCompositor)
            throw new InvalidOperationException("The compositor slot fixture requires its current clean editable canonical owner.");
        if (compositorSlotFixtureRendererIds.Length != 3 || compositorSlotFixtureRendererIds.Any(id => id == Guid.Empty)
            || compositorSlotFixtureRendererIds.Distinct().Count() != 3 || asset.SharedRenderers.Count != 3
            || !asset.SharedRenderers.Select(renderer => renderer.Id).SequenceEqual(compositorSlotFixtureRendererIds))
            throw new InvalidOperationException("Native serialization must retain all three exact prepared renderer identities and order.");
        var first = asset.SharedRenderers.Single(renderer => renderer.Id == compositorSlotFixtureRendererIds[0]);
        var second = asset.SharedRenderers.Single(renderer => renderer.Id == compositorSlotFixtureRendererIds[1]);
        var incompatible = asset.SharedRenderers.Single(renderer => renderer.Id == compositorSlotFixtureRendererIds[2]);
        if (!ReferenceEquals(asset.Game, first) || first is not ForwardRenderer || second is not ForwardRenderer || incompatible is not SubsurfaceScatteringBlur)
            throw new InvalidOperationException("The loaded native fixture must preserve the exact Game alias and compatible/incompatible renderer types.");
        var initialPosition = sessionHistory.History.Position;
        var historyOwner = sessionHistory;
        var appliedPrefix = historyOwner.Stack.RetrieveAllTransactions().Take(initialPosition).ToArray();
        var appliedParticipants = appliedPrefix.Select(transaction => historyOwner.GetParticipants(transaction).Select(participant => participant.Generation).ToArray()).ToArray();
        var activeDocument = documents.Active;
        var nativeGame = engineViewport?.RuntimeGame; var nativeTarget = engineViewport?.Target;
        var originalSceneHash = activeDocument != null && File.Exists(activeDocument.Path) ? SHA256.HashData(File.ReadAllBytes(activeDocument.Path)) : null;
        var initialDisk = SHA256.HashData(File.ReadAllBytes(source.Path));
        var state = compositorEditors.GetValue(source, static _ => new CompositorEditorState());
        CompositorSlot Slot() => source.CompositorBlocks.Single(block => block.Id == Guid.Empty).Slots.Single(slot => slot.Name == nameof(GraphicsCompositorAsset.Game));
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Arm()
        {
            state.Linking = Slot(); state.LinkingCertificate = CompositorOutputSlotCertificate.Capture(source, state.Linking)
                ?? throw new InvalidOperationException("The production native slot certificate could not be captured.");
        }
        void AssertValue(ISharedRenderer? value, int position)
        {
            Require(ReferenceEquals(asset.Game, value) && ReferenceEquals(Slot().Renderer, value)
                && sessionHistory.History.Position == position && ReferenceEquals(authoring?.Find(asset.Id), owner),
                "Compositor slot value, shared journal position or canonical generation changed unexpectedly.");
        }
        Arm();
        Require(!Slot().Accessor.AcceptValue(incompatible) && CaptureCompositorConnection(source, state, incompatible.Id) == null
            && CaptureCompositorConnection(source, state, Guid.NewGuid()) == null,
            "Incompatible or missing native connection targets must not produce an accepted command.");
        AssertValue(first, initialPosition);
        Require(SHA256.HashData(File.ReadAllBytes(source.Path)).AsSpan().SequenceEqual(initialDisk), "Rejected endpoints changed disk bytes.");
        var connect = CaptureCompositorConnection(source, state, second.Id)
            ?? throw new InvalidOperationException("The compatible production connection handler was not available.");
        connect(); AssertValue(second, initialPosition + 1);
        source.Undo(); AssertValue(first, initialPosition);
        source.Redo(); AssertValue(second, initialPosition + 1);
        Arm();
        var stale = CaptureCompositorConnection(source, state, first.Id)
            ?? throw new InvalidOperationException("The stale endpoint fixture could not capture a legitimate native command.");
        source.ConnectCompositorSlot(Slot(), null); AssertValue(null, initialPosition + 2);
        var rejected = false;
        try { stale(); }
        catch (InvalidOperationException error) when (error.Message == "The compositor connection endpoints changed during validation.") { rejected = true; }
        Require(rejected, "The production captured connection must reject a changed source endpoint.");
        AssertValue(null, initialPosition + 2);
        source.Undo(); AssertValue(second, initialPosition + 1);
        source.Save();
        var native = AssetFileSerializer.Load<GraphicsCompositorAsset>(source.Path,
            assetNamespace: workspace.FindAsset(source.Path).Package.Container?.AssetNamespace).Asset;
        Require(native.Id == asset.Id && (native.Game as ISharedRenderer)?.Id == second.Id
            && ReferenceEquals(native.Game, native.SharedRenderers.Single(renderer => renderer.Id == second.Id))
            && native.SharedRenderers.Select(renderer => renderer.Id).SequenceEqual(asset.SharedRenderers.Select(renderer => renderer.Id)),
            "Independent native YAML reload must preserve graph reference identity and every renderer ID.");
        source.Undo(); AssertValue(first, initialPosition); source.Save();
        var restored = AssetFileSerializer.Load<GraphicsCompositorAsset>(source.Path,
            assetNamespace: workspace.FindAsset(source.Path).Package.Container?.AssetNamespace).Asset;
        Require(restored.Id == asset.Id && (restored.Game as ISharedRenderer)?.Id == first.Id && !source.IsDirty,
            "Owned cleanup must persist the original native connection and clean canonical savepoint.");
        var currentPrefix = historyOwner.Stack.RetrieveAllTransactions().Take(initialPosition).ToArray();
        Require(ReferenceEquals(sessionHistory, historyOwner) && currentPrefix.Select(transaction => transaction.Id).SequenceEqual(appliedPrefix.Select(transaction => transaction.Id))
            && currentPrefix.Select((transaction, index) => historyOwner.GetParticipants(transaction).Select(participant => participant.Generation).SequenceEqual(appliedParticipants[index])).All(value => value)
            && ReferenceEquals(documents.Active, activeDocument) && ReferenceEquals(engineViewport?.RuntimeGame, nativeGame)
            && ReferenceEquals(engineViewport?.Target, nativeTarget)
            && (originalSceneHash == null || SHA256.HashData(File.ReadAllBytes(activeDocument!.Path)).AsSpan().SequenceEqual(originalSceneHash)),
            "Slot verification must preserve the original scene, native compiled authority/target and applied shared history participants.");
        state.Linking = null; state.LinkingCertificate = null;
        return new
        {
            assetId = asset.Id, generation = owner.Generation, sourceSlot = nameof(GraphicsCompositorAsset.Game),
            originalRendererId = first.Id, replacementRendererId = second.Id, incompatibleRendererId = incompatible.Id,
            incompatibleAndMissingRejectedWithoutMutation = true, capturedStaleEndpointRejectedWithoutMutation = true,
            connectDisconnectUndoRedoVerified = true, independentNativeYamlReferenceIdentityVerified = true,
            originalConnectionRestoredAndSaved = true,
            originalSceneAndCompiledAuthorityPreserved = true, appliedSharedHistoryPrefixAndParticipantsPreserved = true,
            coverage = "Native fixture serialization is arrangement. Production captured connection handler and canonical disconnect/Undo/Redo/Save are invoked as services; no synthetic slot click/drag, engine render-output or physical-input claim. Outer terminal retirement required."
        };
    }
}
