using System.Numerics;
using System.Security.Cryptography;
using Hexa.NET.ImGui;
using Stride.UI.Assets;
using Stride.Core.Assets;
using Stride.UI;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed record UiCanvasLayout(float Width, float Height, Thickness Margin, HorizontalAlignment Horizontal, VerticalAlignment Vertical)
{
    public float MarginLeft => Margin.Left;
    public float MarginRight => Margin.Right;
    public float MarginTop => Margin.Top;
    public float MarginBottom => Margin.Bottom;
    public float MarginFront => Margin.Front;
    public float MarginBack => Margin.Back;
    internal static UiCanvasLayout Read(UIElement element) => new(element.Width, element.Height, element.Margin, element.HorizontalAlignment, element.VerticalAlignment);
}
internal sealed record UiCanvasReceipt(UIDocument Document, Guid ElementId, int Frame, uint ItemId, uint ViewportId,
    Vector2 CanvasMinimum, Vector2 CanvasMaximum, Vector2 Body, Vector2 Resize, bool BodyVisible, bool ResizeVisible, bool Selected, bool NativeTargetReady,
    bool PointerCaptured, bool RuntimeChanged, Guid? MovingElement, bool Resizing, UiCanvasLayout Authored, UiCanvasLayout Runtime);
internal sealed record UiCanvasContext(EditorSessionHistory History, EditorAuthoringState Owner, Guid[] AppliedIds,
    Guid[][] AppliedParticipants, Dictionary<string, byte[]> OtherDisks, Dictionary<string, bool> OtherDirty);

internal sealed partial class UIEditorPanel
{
    private readonly bool canvasParityEnabled = Environment.GetCommandLineArgs().Contains("--ui-test");
    private int canvasParityFrame = -1;
    private uint canvasParityItem, canvasParityViewport;
    private Vector2 canvasParityMinimum, canvasParityMaximum;
    private void RecordCanvasParityItem()
    {
        if (!canvasParityEnabled) return;
        canvasParityFrame = Gui.GetFrameCount(); canvasParityItem = Gui.GetItemID();
        canvasParityViewport = Gui.GetWindowViewport().ID;
        canvasParityMinimum = Gui.GetItemRectMin(); canvasParityMaximum = Gui.GetItemRectMax();
    }
    internal UiCanvasReceipt? InspectCanvasParity(Guid elementId)
    {
        if (document == null || viewport == null || canvasParityFrame < Gui.GetFrameCount() - 1
            || !viewport.TryElementRect(elementId, out var minimum, out var maximum)
            || document.Find(elementId) is not { } authored || viewport.FindRuntime(elementId) is not { } runtime) return null;
        var body = canvasParityMinimum + (minimum + maximum) * .5f;
        var resize = canvasParityMinimum + maximum;
        bool Visible(Vector2 point) => point.X > canvasParityMinimum.X && point.X < canvasParityMaximum.X
            && point.Y > canvasParityMinimum.Y && point.Y < canvasParityMaximum.Y;
        return new(document, elementId, canvasParityFrame, canvasParityItem, canvasParityViewport, canvasParityMinimum, canvasParityMaximum,
            body, resize, Visible(body), Visible(resize), selected.Contains(elementId), viewport.CanvasParityNativeTargetReady,
            canvasPress != null, layoutRuntimeChanged, moving, resizing != 0, UiCanvasLayout.Read(authored), UiCanvasLayout.Read(runtime));
    }
}
internal sealed partial class UIEditorViewport
{
    internal bool CanvasParityNativeTargetReady => target != null && !target.IsDisposed && component.Page?.RootElement != null && RenderError == null && !disposed;
}
internal sealed partial class EditorGame
{
    private string? uiCanvasFixturePath;
    internal void PrepareUiCanvasParityFixture() => uiCanvasFixturePath = PrepareAuthoredUiFixture();
    internal string UiCanvasParityFixturePath => uiCanvasFixturePath ?? throw new InvalidOperationException("Prepare the real native UI fixture before loading.");
    internal UiCanvasReceipt? InspectUiCanvasParity() => ownedUIEditor?.InspectCanvasParity(UiWorkflowTest.AuthoredUiButtonId);
    internal object? InspectUiCanvasParityEvidence()
    {
        var receipt = InspectUiCanvasParity();
        return receipt == null ? null : new { path = receipt.Document.Path, assetId = receipt.Document.AuthoredAsset.Id.ToString(),
            receipt.ElementId, receipt.Frame, receipt.ItemId, receipt.ViewportId,
            canvasMinimum = new { receipt.CanvasMinimum.X, receipt.CanvasMinimum.Y }, canvasMaximum = new { receipt.CanvasMaximum.X, receipt.CanvasMaximum.Y },
            body = new { receipt.Body.X, receipt.Body.Y }, resize = new { receipt.Resize.X, receipt.Resize.Y },
            receipt.BodyVisible, receipt.ResizeVisible, receipt.Selected, receipt.NativeTargetReady, receipt.PointerCaptured,
            receipt.RuntimeChanged, receipt.MovingElement, receipt.Resizing, receipt.Authored, receipt.Runtime, dirty = receipt.Document.IsDirty,
            generation = authoring?.Find(receipt.Document.AuthoredAsset.Id)?.Generation };
    }
    internal int UiCanvasParityHistoryPosition => sessionHistory?.History.Position ?? throw new InvalidOperationException("No native shared history.");
    internal UiCanvasContext CaptureUiCanvasParityContext(UiCanvasReceipt receipt)
    {
        var history = sessionHistory ?? throw new InvalidOperationException("No canonical UI history.");
        var owner = authoring?.Find(receipt.Document.AuthoredAsset.Id) ?? throw new InvalidOperationException("No canonical UI owner.");
        var prefix = history.Stack.RetrieveAllTransactions().Take(history.History.Position).ToArray();
        return new(history, owner, prefix.Select(transaction => transaction.Id).ToArray(),
            prefix.Select(transaction => history.GetParticipants(transaction).Select(participant => participant.Generation).ToArray()).ToArray(),
            documents.AuthoredDocuments.Where(document => !ReferenceEquals(document, receipt.Document) && File.Exists(document.Path))
                .ToDictionary(document => document.Path, document => SHA256.HashData(File.ReadAllBytes(document.Path)), StringComparer.OrdinalIgnoreCase),
            documents.AuthoredDocuments.Where(document => !ReferenceEquals(document, receipt.Document) && File.Exists(document.Path))
                .ToDictionary(document => document.Path, document => document.IsDirty, StringComparer.OrdinalIgnoreCase));
    }
    internal void VerifyUiCanvasParityContext(UiCanvasContext expected, UiCanvasReceipt current)
    {
        var prefix = expected.History.Stack.RetrieveAllTransactions().Take(expected.AppliedIds.Length).ToArray();
        var checks = new
        {
            sameHistory = ReferenceEquals(sessionHistory, expected.History),
            sameOwner = ReferenceEquals(authoring?.Find(current.Document.AuthoredAsset.Id), expected.Owner),
            ownerPublished = expected.Owner.IsPublished,
            ownerNotDisposed = !expected.Owner.IsDisposed,
            sameDocument = ReferenceEquals(expected.Owner.Document, current.Document),
            sameAppliedIds = prefix.Select(transaction => transaction.Id).SequenceEqual(expected.AppliedIds),
            sameParticipants = prefix.Select((transaction, index) => expected.History.GetParticipants(transaction).Select(participant => participant.Generation).SequenceEqual(expected.AppliedParticipants[index])).All(value => value),
            disks = expected.OtherDisks.Select(pair => new
            {
                path = pair.Key,
                expectedSha256 = Convert.ToHexString(pair.Value),
                baselineDirty = expected.OtherDirty[pair.Key],
                actualSha256 = File.Exists(pair.Key) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) : null,
                documentDirty = documents.AuthoredDocuments.FirstOrDefault(document => string.Equals(document.Path, pair.Key, StringComparison.OrdinalIgnoreCase))?.IsDirty,
            }).ToArray(),
        };
        UiCanvasContextVerificationEvidence = checks;
        if (!checks.sameHistory || !checks.sameOwner || !checks.ownerPublished || !checks.ownerNotDisposed || !checks.sameDocument
            || !checks.sameAppliedIds || !checks.sameParticipants || checks.disks.Any(disk => disk.expectedSha256 != disk.actualSha256))
            throw new InvalidOperationException("Canvas gestures must preserve canonical owner, applied shared history participants and unrelated authored files. "
                + System.Text.Json.JsonSerializer.Serialize(checks));
    }
    internal object? UiCanvasContextVerificationEvidence { get; private set; }
    internal object CaptureUiCanvasInitialIsolationEvidence(UiCanvasContext context)
    {
        return context.OtherDirty.Select(pair =>
        {
            var owner = authoring?.Find(pair.Key);
            var document = owner?.Document;
            object? metadata = null;
            if (document is PackageMetadataDocument packageDocument)
                metadata = packageDocument.CaptureUiCanvasInitialMetadataEvidence();
            return new { path = pair.Key, baselineDirty = pair.Value, currentDirty = document?.IsDirty, metadata };
        }).ToArray();
    }
    internal (string Path, string Control, string[] Locations) InspectUiCanvasBrowserNavigation()
    {
        var current = workspace ?? throw new InvalidOperationException("No native UI project.");
        var asset = current.Assets.Single(entry => SamePath(entry.Path, UiCanvasParityFixturePath));
        var item = current.FindAsset(asset.Id);
        var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, item.Package));
        return (asset.Path, "asset/" + asset.Name, [root.Id, root.Children.First(folder => !folder.IsCode).Id]);
    }
    internal void VerifyUiCanvasPersisted(UiCanvasReceipt expected)
    {
        if (!ReferenceEquals(authoring?.Find(expected.Document.AuthoredAsset.Id)?.Document, expected.Document))
            throw new InvalidOperationException("UI canvas verification lost its actual canonical generation.");
        var asset = AssetFileSerializer.Load<UIPageAsset>(expected.Document.Path,
            assetNamespace: workspace?.FindAsset(expected.Document.Path).Package.Container?.AssetNamespace).Asset;
        if (asset.Id != expected.Document.AuthoredAsset.Id || asset.Hierarchy.Parts.Count != 2
            || !asset.Hierarchy.Parts.TryGetValue(expected.ElementId, out var design) || UiCanvasLayout.Read(design.UIElement) != expected.Authored
            || !ReferenceEquals(asset.Hierarchy.RootParts.Single().VisualChildren.Single(), design.UIElement))
            throw new InvalidOperationException("Independent native UI reload lost layout, element identity or root/child alias ownership.");
    }
}

internal sealed partial class PackageMetadataDocument
{
    internal object CaptureUiCanvasInitialMetadataEvidence()
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test"))
            throw new InvalidOperationException("UI canvas metadata diagnostics require the private UI-test host.");
        using var stream = new MemoryStream();
        Stride.Core.Yaml.AssetYamlSerializer.Default.Serialize(stream, package);
        return new
        {
            initiallyDirty, revision, savedRevision, nativePackageDirty = package.IsDirty,
            savedMetadataSha256 = Convert.ToHexString(savedMetadata),
            currentMetadataSha256 = Convert.ToHexString(SHA256.HashData(stream.ToArray())),
            currentNativeYaml = System.Text.Encoding.UTF8.GetString(stream.ToArray()),
            diskYaml = File.ReadAllText(Path),
        };
    }
}