using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Quantum;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IEnumerator<object?>? extensionLifecycleVerification;
    private EditorExtensionSession? extensionLifecycleDrawSession;
    private Exception? extensionLifecycleDrawFailure;
    internal void StartNativeExtensionOwnedEditorLifecycle(AssetId assetId)
    {
        if (extensionLifecycleVerification != null) throw new InvalidOperationException("Extension lifecycle verification is already pending.");
        extensionLifecycleDrawFailure = null;
        extensionLifecycleVerification = RunNativeExtensionOwnedEditorLifecycle(assetId).GetEnumerator();
    }
    internal object? StepNativeExtensionOwnedEditorLifecycle()
    {
        var run = extensionLifecycleVerification ?? throw new InvalidOperationException("Extension lifecycle verification is not pending.");
        try
        {
            if (extensionLifecycleDrawFailure != null) throw new InvalidOperationException("Native extension fixture drawing failed.", extensionLifecycleDrawFailure);
            if (!run.MoveNext()) throw new InvalidOperationException("Extension lifecycle verification ended without evidence.");
            var result = run.Current;
            if (result != null) CancelNativeExtensionOwnedEditorLifecycle();
            return result;
        }
        catch { CancelNativeExtensionOwnedEditorLifecycle(); throw; }
    }
    internal void CancelNativeExtensionOwnedEditorLifecycle()
    {
        extensionLifecycleDrawSession = null;
        var run = extensionLifecycleVerification; extensionLifecycleVerification = null;
        run?.Dispose(); extensionLifecycleDrawFailure = null;
    }
    private void DrawNativeExtensionLifecycleVerification()
    {
        if (extensionLifecycleDrawSession == null || extensionLifecycleDrawFailure != null) return;
        try { extensionLifecycleDrawSession.DrawFrame(); }
        catch (Exception error) { extensionLifecycleDrawFailure = error; }
    }
    private IEnumerable<object?> RunNativeExtensionOwnedEditorLifecycle(AssetId assetId)
    {
        if (!Environment.GetCommandLineArgs().Contains("--ui-test")) throw new InvalidOperationException("Extension verification requires a private UI fixture.");
        var current = workspace ?? throw new InvalidOperationException("No native project is loaded.");
        var history = sessionHistory ?? throw new InvalidOperationException("No canonical history is loaded.");
        var item = current.Session.FindAsset(assetId) ?? throw new InvalidOperationException("The fixture asset is missing.");
        var entry = new AssetEntry(item.FullPath.ToOSPath(), item.UnqualifiedUrl.ToString(), Path.GetExtension(item.FullPath.ToOSPath()), item.Asset.GetType(), item.Id, item.Asset.Tags.ToArray());
        var document = GetAssetTagDocument(entry, false) as AssetMetadataDocument
            ?? throw new InvalidOperationException("Extension lifecycle verification requires a real metadata asset fixture.");
        var owner = history.Registry.Find(assetId) ?? throw new InvalidOperationException("The fixture canonical owner is missing.");
        var nativeItem = item.Clone(keepPackage: true, newAsset: document.AuthoredAsset);
        var originalTags = document.AuthoredAsset.Tags.ToArray();
        var originalDirty = document.IsDirty;
        var originalHistoryPosition = history.History.Position;
        var appliedPrefix = history.Stack.RetrieveAllTransactions().Take(originalHistoryPosition).ToArray();
        var appliedParticipants = appliedPrefix.Select(transaction => history.GetParticipants(transaction)
            .Select(state => state.Generation).ToArray()).ToArray();
        var originalAssetBytes = File.ReadAllBytes(document.Path);
        var originalPackageBytes = File.ReadAllBytes(current.PackagePath);
        var fixture = new ExtensionLifecycleFixture(nativeItem.Asset.GetType(), item.Asset);
        var context = new EditorExtensionContext(current.Session, GlobalLogger.GetLogger("ExtensionLifecycleVerification"),
            id => id == assetId ? document.AuthoringGraph : throw new InvalidOperationException("Unexpected fixture asset."),
            (id, mutation, title) => { if (id != assetId) throw new InvalidOperationException("Unexpected fixture asset."); document.ApplyInheritedAssetMutation(() => mutation(document.AuthoringGraph), title); },
            (action, title) => { using var capture = history.CaptureOperationsAtomic(action); capture.Publish(history, title); },
            id => id == assetId ? owner : throw new InvalidOperationException("Unexpected fixture owner."));
        using var registration = EditorExtensions.Register(() => fixture);
        using var extensions = new EditorExtensionSession(context);
        extensions.Refresh();
        var tags = document.AuthoringGraph.RootNode[nameof(Asset.Tags)].Target!;
        document.ApplyInheritedAssetMutation(() => tags.Add("Extension lifecycle original"), "Arrange extension virtual target");
        using (var presentation = new EditorAssetPropertyPresentation(document.AuthoringGraph, extensions))
        {
            var row = presentation.VirtualRows.Single(node => node.Name == ExtensionLifecycleUpdater.RowName);
            var command = row.Commands.Single(node => node.Name == "Extension lifecycle replace");
            if (!command.CanExecute([row], "Extension lifecycle edited") || command.CanExecute([row], 42))
                throw new InvalidOperationException("The actual virtual presenter command has incorrect eligibility.");
            var original = (string)row.Value;
            document.ApplyInheritedAssetMutation(() => command.Execute(row, "Extension lifecycle edited", null).GetAwaiter().GetResult(), "Execute extension virtual command");
            if ((string)row.Value != "Extension lifecycle edited") throw new InvalidOperationException("Virtual command did not author its canonical node.");
            var editedHistory = history.Stack.RetrieveAllTransactions().Count();
            document.ApplyInheritedAssetMutation(() => command.Execute(row, "Extension lifecycle edited", null).GetAwaiter().GetResult(), "No-op extension virtual command");
            if (history.Stack.RetrieveAllTransactions().Count() != editedHistory) throw new InvalidOperationException("No-op virtual command added authored history.");
            history.Undo(); if ((string)row.Value != original) throw new InvalidOperationException("Virtual command Undo lost original native value.");
            history.Redo(); if ((string)row.Value != "Extension lifecycle edited") throw new InvalidOperationException("Virtual command Redo lost authored native value.");
            history.Undo();
        }
        history.Undo(); // Retire the actual fixture tag arrangement.
        if (!extensions.TryOpenAssetEditor(nativeItem) || fixture.Opened != 1 || fixture.View == null)
            throw new InvalidOperationException("The real extension provider did not open its canonical owned editor.");
        if (!extensions.TryOpenAssetEditor(nativeItem) || fixture.Opened != 1 || fixture.View.Activated != 1)
            throw new InvalidOperationException("Reopening the extension editor did not activate the retained resource.");
        // Keep the isolated actual session alive across one normal native frame.
        // Stop discovery by the host session without refreshing this retained provider yet.
        registration.Dispose();
        extensionLifecycleDrawSession = extensions;
        yield return null;
        extensionLifecycleDrawSession = null;
        if (fixture.View.RenderedWindowId == 0 || fixture.View.RenderedFrame != Gui.GetFrameCount())
            throw new InvalidOperationException("The extension's actual native editor window was not rendered in the current normal frame.");
        using (var template = extensions.AcquireAssetTemplateAdapter(fixture.Template)
            ?? throw new InvalidOperationException("The registered native template adapter is unavailable."))
        {
            template.Validate();
            var parameters = new AssetTemplateGeneratorParameters(new UDirectory(""))
            { Package = item.Package, Description = fixture.Template, Name = "ExtensionLifecycle" + Guid.NewGuid().ToString("N"),
                Namespace = item.Package.Container?.AssetNamespace, OutputDirectory = item.Package.RootDirectory, Logger = new LoggerResult(), Unattended = true };
            var prepared = template.Adapter.PrepareAssets(parameters);
            if (prepared.Count != 1 || ReferenceEquals(prepared[0].Asset, item.Asset)) throw new InvalidOperationException("Native template preparation did not return a detached asset.");
            PublishRegisteredTemplateAssets(item.Package, prepared.ToArray());
            var published = current.Session.FindAsset(prepared[0].Id) ?? throw new InvalidOperationException("Native template output was not published.");
            var publishedOwner = history.Registry.Find(published.Id) ?? throw new InvalidOperationException("Native template output has no authored generation.");
            if (!publishedOwner.IsPublished || File.Exists(published.FullPath.ToOSPath())) throw new InvalidOperationException("Template publication bypassed native unsaved-asset policy.");
            history.Undo(); if (current.Session.FindAsset(published.Id) != null) throw new InvalidOperationException("Template Undo retained a published output.");
            history.Redo(); if (current.Session.FindAsset(published.Id) == null) throw new InvalidOperationException("Template Redo did not restore native output.");
            history.Undo();
            using var retained = template.Retain();
            registration.Dispose(); extensions.Refresh();
            if (extensions.HasAssetEditor(nativeItem.Asset.GetType())) throw new InvalidOperationException("An unregistered provider remained available for new editor opens.");
            try { retained.Validate(); throw new InvalidOperationException("An unavailable template provider accepted new use."); }
            catch (InvalidOperationException error) when (error.Message == "The template provider is no longer available.") { }
            if (fixture.View.Disposed != 0 || owner.IsDisposed || !ReferenceEquals(history.Registry.Find(assetId), owner))
                throw new InvalidOperationException("Unregistration discarded an actual retained editor or canonical owner.");
        }
        context.CloseAssetEditor(assetId);
        if (fixture.View.Disposed != 1 || extensions.HasOwnedAssetEditors) throw new InvalidOperationException("Explicit close failed to release the owned editor resource.");
        extensions.Dispose();
        if (fixture.SessionClosed != 1 || fixture.Disposed != 1 || fixture.View.Disposed != 1
            || !ReferenceEquals(history.Registry.Find(assetId), owner) || !document.AuthoredAsset.Tags.SequenceEqual(originalTags))
            throw new InvalidOperationException("Extension retirement changed canonical history or duplicated resource disposal.");
        var finalApplied = history.Stack.RetrieveAllTransactions().Take(history.History.Position).ToArray();
        if (history.History.Position != originalHistoryPosition
            || !finalApplied.Select(transaction => transaction.Id).SequenceEqual(appliedPrefix.Select(transaction => transaction.Id))
            || finalApplied.Where((transaction, index) => !history.GetParticipants(transaction).Select(state => state.Generation)
                .SequenceEqual(appliedParticipants[index])).Any()
            || document.IsDirty != originalDirty
            || !originalAssetBytes.SequenceEqual(File.ReadAllBytes(document.Path))
            || !originalPackageBytes.SequenceEqual(File.ReadAllBytes(current.PackagePath)))
            throw new InvalidOperationException("Extension cleanup must retain the exact previously applied shared-history prefix, canonical participant generations and original authored disk bytes.");
        yield return new { assetId = assetId.ToString(), fixture.Opened, activations = fixture.View.Activated,
            editorDisposals = fixture.View.Disposed, fixture.SessionClosed, providerDisposals = fixture.Disposed,
            canonicalGeneration = owner.Generation, virtualCommandUndoRedo = true, templatePublicationUndoRedo = true, nativeWindowId = fixture.View.RenderedWindowId, nativeFrame = fixture.View.RenderedFrame,
            priorAppliedHistoryPreserved = true, canonicalParticipantsPreserved = true, authoredDiskBytesPreserved = true,
            originalHistoryPosition, appliedTransactionIds = appliedPrefix.Select(transaction => transaction.Id).ToArray(), appliedParticipants,
            coverage = "Actual canonical virtual presenter command, no-op history, Undo/Redo; detached native template preparation/publication; rendered retained editor and explicit provider retirement. CLR collectible unload is not exercised." };
    }
    private sealed class ExtensionLifecycleFixture(Type assetType, Asset source) : IEditorExtension, IDisposable
    {
        internal readonly TemplateAssetDescription Template = new();
        internal ExtensionLifecycleView? View;
        internal int Opened, SessionClosed, Disposed;
        public void Initialize(EditorExtensionContext context, EditorExtensionRegistration registration)
        {
            registration.RegisterOwnedAssetEditor(assetType, _ => { Opened++; return View = new(); });
            registration.RegisterFrameAction(() => View?.Draw());
            registration.RegisterNodePresenterUpdater((_, _) => new ExtensionLifecycleUpdater());
            registration.RegisterNodePresenterCommand(_ => new ExtensionLifecycleCommand());
            registration.RegisterAssetTemplateAdapter(new(new ExtensionLifecycleGenerator(Template), assetType,
                parameters => { var clone = AssetCloner.Clone(source); clone.Id = AssetId.New(); return [new AssetItem(new UFile(parameters.Name), clone)]; }));
        }
        public void SessionDisposed(EditorExtensionContext context) => SessionClosed++;
        public void Dispose() => Disposed++;
    }
    private sealed class ExtensionLifecycleGenerator(TemplateAssetDescription template) : ITemplateGenerator
    { public bool IsSupportingTemplate(TemplateDescription description) => ReferenceEquals(description, template); }
    private sealed class ExtensionLifecycleView : IDisposable, IEditorAssetEditorState
    {
        internal int Activated, Disposed;
        internal uint RenderedWindowId;
        internal int RenderedFrame = -1;
        internal void Draw()
        {
            if (IsClosed) return;
            Gui.Begin("Extension lifecycle fixture");
            try { RenderedWindowId = Hexa.NET.ImGui.ImGuiP.GetCurrentWindowRead().ID; RenderedFrame = Gui.GetFrameCount(); Gui.TextUnformatted("Extension lifecycle fixture"); }
            finally { Gui.End(); }
        }
        public bool IsClosed => Disposed != 0;
        public void Activate() { if (IsClosed) throw new ObjectDisposedException(nameof(ExtensionLifecycleView)); Activated++; }
        public void Dispose() { if (Disposed != 0) throw new InvalidOperationException("Duplicate extension editor disposal."); Disposed++; }
    }
    private sealed class ExtensionLifecycleUpdater : NodePresenterUpdaterBase
    {
        internal const string RowName = "ExtensionLifecycleVirtualTag";
        public override void UpdateNode(INodePresenter node)
        {
            if (node.Parent != null || node.Factory is not EditorNodePresenterFactory factory) return;
            var collection = node.GetNodeAccessor().Node is IObjectNode root ? root[nameof(Asset.Tags)].Target : null;
            if (collection?.Indices == null || !collection.Indices.Any()) throw new InvalidOperationException("Native extension fixture requires its authored tag.");
            var index = collection.Indices.Last();
            factory.CreateVirtualNode(node, RowName, typeof(string), new NodeAccessor(collection, index),
                accessor => accessor.RetrieveValue()!, (accessor, value) => accessor.UpdateValue(value));
        }
    }
    private sealed class ExtensionLifecycleCommand : SyncNodePresenterCommandBase
    {
        public override string Name => "Extension lifecycle replace";
        public override bool CanAttach(INodePresenter nodePresenter) => nodePresenter.Name == ExtensionLifecycleUpdater.RowName;
        public override bool CanExecute(IReadOnlyCollection<INodePresenter> nodes, object? parameter)
            => parameter is string && nodes.Count == 1 && nodes.All(node => CanAttach(node) && !node.IsReadOnly);
        protected override void ExecuteSync(INodePresenter node, object? parameter, object? preExecuteResult)
        { if (!CanExecute([node], parameter)) throw new InvalidOperationException("The native virtual command is ineligible."); if (!Equals(node.Value, parameter)) node.UpdateValue(parameter!); }
    }
}
