using System.Collections.Concurrent;
using Hexa.NET.ImGui;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.Serialization.Contents;
using Stride.Editor.Preview;
using Stride.Games;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class ExternalPreviewOwner
    {
        internal required NativeAssetPreviewRegistry.Lease ProviderLease;
        internal required object Provider;
        internal required AssetEntry Entry;
        internal required ProjectWorkspace Workspace;
        internal required AssetPreviewRuntimeContext Context;
        internal required CancellationTokenSource Cancellation;
        internal required GraphicsDevice Device;
        internal ContentManager? Cache;
        internal IDisposable? BundleLease;
        internal IDisposable? DocumentLease;
        internal IAssetPreviewRuntimeAdapter? Adapter;
        internal IImGuiAssetPreviewSurface? Surface;
        internal IAssetPreviewRuntime? Runtime;
        internal CompiledProjectContent? OwnedBundle;
        internal Task? Initialization, Disposal;
        internal ImTextureRef Image;
        internal Texture? ImageTexture;
        internal ExternalPreviewGpuGate? ImageGate;
        internal bool ImageGateQueued, CacheRetirementQueued;
        internal bool Closed;
        internal string? Revision;
        internal object? ViewState;
        internal bool SurfaceDisposed, RuntimeDisposed, AdapterDisposed;
        internal DateTime DisposeRetryAt;
        internal string? ReportedDisposeError;
        internal bool InputReleasePending;
        internal DateTime InputReleaseRetryAt;
        internal string? ReportedInputReleaseError;
        internal readonly AuthoredImportPollingCache Polling = new();
        internal DateTime CheckAt;
        internal IDisposable? PendingDocumentLease;
        internal AssetPreviewRuntimeContext? PendingContext;
        internal string? CaptureError;
        internal bool PointerLeft, PointerRight, PointerMiddle, PointerExtended1, PointerExtended2, InputFocus, DeliveredInputFocus;
        internal readonly HashSet<Stride.Input.Keys> DownInputKeys = [];
    }
    // The existing retirement actor calls Dispose only after its actual-device
    // completion marker. This is permission to begin async provider cleanup,
    // not a frame delay or permission to release the host content cache.
    private sealed class ExternalPreviewGpuGate : IDisposable
    {
        internal bool Certified;
        public void Dispose() => Certified = true;
    }
    private sealed class ExternalPreviewCacheResources(ExternalPreviewOwner owner) : IDisposable
    {
        private bool providerReleased, cancellationReleased;
        public void Dispose()
        {
            if (owner.Cache != null) { owner.Cache.UnloadAll(); owner.Cache = null; }
            owner.OwnedBundle?.Dispose(); owner.OwnedBundle = null;
            owner.BundleLease?.Dispose(); owner.BundleLease = null;
            owner.DocumentLease?.Dispose(); owner.DocumentLease = null;
            owner.PendingDocumentLease?.Dispose(); owner.PendingDocumentLease = null;
            if (!providerReleased) { owner.ProviderLease.Dispose(); providerReleased = true; }
            if (!cancellationReleased) { owner.Cancellation.Dispose(); cancellationReleased = true; }
        }
    }
    private NativeAssetPreviewRegistry? externalPreviewRegistry;
    private NativeAssetPreviewRegistry ExternalPreviewRegistry => externalPreviewRegistry ??= new();
    private ExternalPreviewOwner? externalPreview;
    private readonly List<ExternalPreviewOwner> retiredExternalPreviews = [];
    private readonly ConcurrentQueue<(Action Action, CancellationToken Cancellation, TaskCompletionSource Completion)> externalPreviewActions = new();
    private readonly List<TaskCompletionSource> externalPreviewDrains = [];
    private Task externalPreviewDrain = Task.CompletedTask;
    private bool externalDrainIncludesCatalog;
    private bool IsExternalAssetPreviewDrainPending => !externalPreviewDrain.IsCompleted;
    private int externalPreviewActorThread;
    private bool IsExternalAssetPreviewUpdating => externalPreview?.Initialization?.IsCompleted == false || retiredExternalPreviews.Count != 0;

    private Task InvokeExternalPreviewAction(Action action, CancellationToken cancellation)
    {
        if (Environment.CurrentManagedThreadId == externalPreviewActorThread)
        { cancellation.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        externalPreviewActions.Enqueue((action, cancellation, completion)); return completion.Task;
    }
    private bool TryOpenExternalAssetPreview(AssetEntry entry)
    {
        using var lease = ExternalPreviewRegistry.Acquire(entry.AssetType);
        if (lease == null || workspace == null || projectContent == null && lease.RequiresCompiledContent) return false;
        CloseCatalogAssetPreviews(); CloseLegacyAssetPreviews();
        selectedAssetPath = assetPreviewPath = entry.Path; showAssetPreview = true;
        if (lease.RequiresCompiledContent) RequestCatalogPreviewUpdate(entry, external: true);
        else BeginExternalAssetPreview(entry, projectContent, false);
        return true;
    }
    private bool HasExternalAssetPreview(Type type) => ExternalPreviewRegistry.Has(type);
    private bool HasExternalSourceOnlyPreview(Type type)
    { using var lease = ExternalPreviewRegistry.Acquire(type); return lease is { RequiresCompiledContent: false }; }

    private bool BeginExternalAssetPreview(AssetEntry entry, CompiledProjectContent? bundle, bool ownsBundle, string? revision = null)
    {
        var lease = ExternalPreviewRegistry.Acquire(entry.AssetType);
        if (lease == null) { if (ownsBundle) bundle?.Dispose(); return false; }
        if (!AcceptExternalPreviewReattachment(entry, lease))
        { lease.Dispose(); if (ownsBundle) bundle?.Dispose(); return false; }
        ExternalPreviewOwner? owner = null;
        try
        {
            externalPreviewActorThread = Environment.CurrentManagedThreadId;
            var item = workspace!.FindAsset(entry.Id);
            var authored = authoring?.Find(item.Id)?.Document is IAssetTagDocument document ? document.AuthoredAsset : item.Asset;
            var canonical = item.Clone(keepPackage: true, newAsset: authored);
            var device = GraphicsDevice;
            owner = new ExternalPreviewOwner { ProviderLease = lease, Provider = null!, Entry = entry, Workspace = workspace,
                Context = null!, Device = device, Cancellation = new(), OwnedBundle = ownsBundle ? bundle : null };
            owner.BundleLease = bundle?.AcquireLease();
            owner.Cache = bundle?.CreateObjectCache() ?? new ContentManager(base.Services);
            owner.Context = new(this, canonical, owner.Cache, GlobalLogger.GetLogger("Preview"),
                resource => RetireEditorPreview(resource, device), InvokeExternalPreviewAction);
            // Establish the cleanup owner before invoking any external constructor.
            // A subsequent surface/adapter/lease failure must retain that provider
            // until its actual asynchronous cleanup succeeds.
            owner.DocumentLease = authoring?.Find(item.Id)?.AcquireView();
            owner.Revision = revision;
            if (externalPreviewRestoreState is { } restored && restored.Id == entry.Id && restored.Provider == lease.ProviderType)
            { owner.ViewState = restored.State; externalPreviewRestoreState = null; }
            if (externalPreview is { Runtime: IAssetPreviewViewStateRuntime viewState } previous
                && previous.ProviderLease.ProviderType == lease.ProviderType && previous.Entry.Id == entry.Id && !previous.ProviderLease.IsRetiring)
                owner.ViewState = CaptureExternalPreviewViewState(viewState);
            if (lease.AdapterType != null) owner.Adapter = (IAssetPreviewRuntimeAdapter)Activator.CreateInstance(lease.AdapterType)!;
            if (lease.SurfaceType != null) owner.Surface = (IImGuiAssetPreviewSurface)Activator.CreateInstance(lease.SurfaceType)!;
            owner.Provider = Activator.CreateInstance(lease.ProviderType) ?? throw new InvalidOperationException("The actual preview provider could not be created.");
            // No further external constructor can fail before the actual provider
            // has been handed to its direct/adapter lifetime implementation.
            owner.Initialization = InitializeExternalPreview(owner);
            CloseExternalAssetPreview(); externalPreview = owner;
            selectedAssetPath = assetPreviewPath = entry.Path; focusAssetPreview = true; showAssetPreview = true;
            return true;
        }
        catch
        {
            if (owner == null) { lease.Dispose(); if (ownsBundle) bundle?.Dispose(); }
            else
            {
                owner.Closed = true; owner.InputReleasePending = true; retiredExternalPreviews.Add(owner);
                try { owner.Cancellation.Cancel(); }
                catch (Exception cancellationError) { RecordAssetPreviewRenderFailure("Asset preview cancellation", cancellationError); }
            }
            throw;
        }
    }
    private static async Task InitializeExternalPreview(ExternalPreviewOwner owner)
    {
        owner.Runtime = owner.Adapter == null ? (IAssetPreviewRuntime)owner.Provider
            : await owner.Adapter.CreateAsync(owner.Provider, owner.Context, owner.Cancellation.Token);
        await owner.Runtime.InitializeAsync(owner.Context, owner.Cancellation.Token);
    }

    private void PumpExternalAssetPreview()
    {
        externalPreviewActorThread = Environment.CurrentManagedThreadId;
        externalPreviewRegistry?.Pump();
        while (externalPreviewActions.TryDequeue(out var action))
        {
            if (action.Cancellation.IsCancellationRequested) { action.Completion.TrySetCanceled(action.Cancellation); continue; }
            try { externalPreviewActionRevision++; action.Action(); action.Completion.TrySetResult(); }
            catch (Exception error) { action.Completion.TrySetException(error); _ = RoutePreviewGraphicsFailure(error); }
        }
        if (externalPreview is { } current)
        {
            if (current.ProviderLease.IsRetiring && QueueExternalPreviewReattachment(current))
                CloseExternalAssetPreview(preserveReattachment: true);
            else if (current.ProviderLease.IsRetiring || !ReferenceEquals(current.Workspace, workspace)
                || current.Entry.Path != selectedAssetPath || projectContent == null && current.ProviderLease.RequiresCompiledContent) CloseExternalAssetPreview();
            else if (current.Initialization is { IsCompleted: true } initialization)
            {
                current.Initialization = null;
                try
                {
                    initialization.GetAwaiter().GetResult();
                    if (current.PendingContext != null)
                    {
                        current.DocumentLease?.Dispose(); current.DocumentLease = current.PendingDocumentLease; current.PendingDocumentLease = null;
                        current.Context = current.PendingContext; current.PendingContext = null;
                    }
                    if (current.ProviderLease.RequiresCompiledContent && current.Revision != null
                        && CaptureCatalogPreview(current.Entry, true).Revision != current.Revision) CloseExternalAssetPreview();
                    else if (current.ViewState != null && current.Runtime is IAssetPreviewViewStateRuntime state) state.RestoreViewState(current.ViewState);
                    current.ViewState = null;
                }
                catch (OperationCanceledException) when (current.Cancellation.IsCancellationRequested) { CloseExternalAssetPreview(); }
                catch (Exception error) { RecordAssetPreviewBuildFailure(current.Entry.Id, error, AssetThumbnailFailure.Broken); CloseExternalAssetPreview(); }
            }
            if (ReferenceEquals(externalPreview, current) && current.Initialization == null && !current.ProviderLease.RequiresCompiledContent)
                RefreshExternalAuthoredPreview(current);
        }
        foreach (var owner in retiredExternalPreviews.ToArray())
        {
            if (owner.Initialization?.IsCompleted == false) continue;
            if (owner.InputReleasePending && !ReleaseExternalPreviewInput(owner)) continue;
            if (editorGpuRetirement is { Stage: GpuRetirementStage.Initial }) continue;
            if (owner.Initialization?.IsCompleted == false || DateTime.UtcNow < owner.DisposeRetryAt) continue;
            if (owner.Initialization?.IsFaulted == true) _ = owner.Initialization.Exception;
            if (owner.ImageGate?.Certified != true)
            {
                owner.ImageGate ??= new ExternalPreviewGpuGate();
                if (!owner.ImageGateQueued)
                { RetireEditorPreview(owner.ImageGate, owner.Device); owner.ImageGateQueued = true; }
                continue;
            }
            if (owner.Disposal == null)
            {
                if (owner.ImageTexture != null) { renderer?.UnregisterTexture(owner.Image); owner.ImageTexture = null; }
                owner.Disposal = DisposeExternalPreview(owner);
            }
            if (!owner.Disposal.IsCompleted) continue;
            try { owner.Disposal.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                owner.Disposal = null; owner.DisposeRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                if (owner.ReportedDisposeError != error.ToString())
                { owner.ReportedDisposeError = error.ToString(); RecordAssetPreviewRenderFailure("Asset preview disposal", error); }
                continue;
            }
            try
            {
                // Provider cleanup can submit additional native work. Its host
                // cache and physical/document/provider leases need a new marker,
                // queued after that real asynchronous task has completed.
                if (!owner.CacheRetirementQueued)
                { RetireEditorPreview(new ExternalPreviewCacheResources(owner), owner.Device); owner.CacheRetirementQueued = true; }
                retiredExternalPreviews.Remove(owner);
            }
            catch (Exception error)
            {
                owner.DisposeRetryAt = DateTime.UtcNow.AddMilliseconds(250);
                if (owner.ReportedDisposeError != error.ToString())
                { owner.ReportedDisposeError = error.ToString(); RecordAssetPreviewRenderFailure("Asset preview resource retirement", error); }
            }
        }
        if (externalPreview == null && retiredExternalPreviews.Count == 0
            && (!externalDrainIncludesCatalog || !IsCatalogPreviewUpdating))
        {
            foreach (var drain in externalPreviewDrains) drain.TrySetResult();
            externalPreviewDrains.Clear();
            externalDrainIncludesCatalog = false;
        }
        PumpExternalPreviewReattachment();
    }
    private static async Task DisposeExternalPreview(ExternalPreviewOwner owner)
    {
        if (!owner.SurfaceDisposed)
        {
            if (owner.Surface != null) await owner.Surface.DisposeAsync();
            owner.SurfaceDisposed = true;
        }
        if (!owner.RuntimeDisposed)
        {
            if (owner.Runtime != null) await owner.Runtime.DisposeAsync();
            else if (owner.Adapter == null && owner.Provider is IAssetPreviewRuntime direct) await direct.DisposeAsync();
            owner.RuntimeDisposed = true;
        }
        if (!owner.AdapterDisposed)
        {
            if (owner.Adapter != null) await owner.Adapter.DisposeAsync();
            owner.AdapterDisposed = true;
        }
    }
    private void RefreshExternalAuthoredPreview(ExternalPreviewOwner owner)
    {
        if (DateTime.UtcNow < owner.CheckAt || owner.Runtime == null) return;
        owner.CheckAt = DateTime.UtcNow.AddMilliseconds(250);
        try
        {
            var inputs = AuthoredImportPollingCache.CaptureInputs(owner.Workspace, authoring);
            var capture = owner.Polling.Capture(owner.Workspace, authoring, owner.Entry, inputs,
                _ => CaptureCatalogPreview(owner.Entry), includeInboundDependencies: false).Snapshot;
            owner.CaptureError = null;
            if (capture.Revision == owner.Revision) return;
            var item = owner.Workspace.FindAsset(owner.Entry.Id);
            var state = authoring?.Find(item.Id);
            var authored = state?.Document is IAssetTagDocument document ? document.AuthoredAsset : item.Asset;
            owner.PendingDocumentLease = state?.AcquireView();
            owner.PendingContext = new(this, item.Clone(keepPackage: true, newAsset: authored), owner.Context.Content,
                owner.Context.Logger, resource => RetireEditorPreview(resource, owner.Device), InvokeExternalPreviewAction);
            owner.Initialization = owner.Runtime.UpdateAssetAsync(owner.PendingContext, owner.Cancellation.Token).AsTask();
            owner.Revision = capture.Revision;
        }
        catch (Exception error)
        {
            if (owner.CaptureError != error.ToString())
            { owner.CaptureError = error.ToString(); RecordAssetPreviewBuildFailure(owner.Entry.Id, error, AssetThumbnailFailure.Broken); }
            if (owner.PendingContext != null) CloseExternalAssetPreview();
        }
    }
    private void CloseExternalAssetPreview(bool preserveReattachment = false)
    {
        if (!preserveReattachment) externalPreviewReattachment = externalPreviewReattachmentBuild = null;
        if (externalPreview is not { } owner) return;
        externalPreview = null; owner.Closed = true;
        owner.InputReleasePending = true;
        retiredExternalPreviews.Add(owner);
        try { owner.Cancellation.Cancel(); }
        catch (Exception error) { RecordAssetPreviewRenderFailure("Asset preview cancellation", error); }
        if (owner.Initialization?.IsCompleted != false) ReleaseExternalPreviewInput(owner);
        try { if (owner.Runtime != null) owner.Runtime.Visible = false; }
        catch (Exception error) { RecordAssetPreviewRenderFailure("Asset preview visibility", error); }
    }
    private Task DrainExternalAssetPreviewsAsync()
    {
        CloseExternalAssetPreview();
        if (retiredExternalPreviews.Count == 0) return Task.CompletedTask;
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        externalPreviewDrains.Add(drain); return drain.Task;
    }
    private void BeginExternalAssetPreviewDrain(bool graphicsQuiesced = false)
    {
        if (requestedCatalogExternal) { externalDrainIncludesCatalog = true; StopCatalogPreviewUpdate(); }
        externalPreviewDrain = DrainExternalAssetPreviewsAsync();
        // Called only after the protected host's actual initial marker; its
        // allocating device and terminal initialization must match this owner.
        if (graphicsQuiesced)
            foreach (var owner in retiredExternalPreviews)
            {
                if (!ReferenceEquals(owner.Device, GraphicsDevice) || owner.Initialization?.IsCompleted == false || owner.InputReleasePending)
                    throw new InvalidOperationException("The external preview owner is not covered by the initial GPU certificate.");
                owner.ImageGate ??= new ExternalPreviewGpuGate(); owner.ImageGate.Dispose();
            }
        if (externalDrainIncludesCatalog && externalPreviewDrain.IsCompleted)
        {
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            externalPreviewDrains.Add(drain); externalPreviewDrain = drain.Task;
        }
    }
    private Action CaptureExternalAssetPreviewRestore()
    {
        var entry = externalPreview?.Entry ?? (requestedCatalogExternal ? requestedCatalogPreview : null);
        var provider = externalPreview?.ProviderLease.ProviderType;
        var view = externalPreview?.Runtime is IAssetPreviewViewStateRuntime state && !externalPreview.ProviderLease.IsRetiring
            ? CaptureExternalPreviewViewState(state) : null;
        var previousWorkspace = workspace;
        return () =>
        {
            if (entry == null || !ReferenceEquals(previousWorkspace, workspace)) return;
            var current = workspace?.Assets.FirstOrDefault(candidate => candidate.Id == entry.Id);
            if (current == null) return;
            using var lease = ExternalPreviewRegistry.Acquire(current.AssetType);
            if (lease == null || provider != null && lease.ProviderType != provider) return;
            if (TryOpenExternalAssetPreview(current) && externalPreview != null) externalPreview.ViewState = view;
            // Compiled adapters initialize after their actual production build;
            // their state is attached below when the same exact provider opens.
            else if (requestedCatalogExternal) externalPreviewRestoreState = (current.Id, lease.ProviderType, view);
        };
    }
    private (AssetId Id, Type Provider, object? State)? externalPreviewRestoreState;
    private object? CaptureExternalPreviewViewState(IAssetPreviewViewStateRuntime state)
    {
        try { return state.CaptureViewState(); }
        catch (Exception error) { RecordAssetPreviewRenderFailure("Asset preview state", error); return null; }
    }
    private void DisposeExternalAssetPreviewRegistry()
    {
        if (externalPreview != null || retiredExternalPreviews.Count != 0)
            throw new InvalidOperationException("External preview providers must drain before editor/content service disposal.");
        externalPreviewRegistry?.Dispose(); externalPreviewRegistry = null;
    }
    private void ResetExternalAssetPreviewVisibility()
    {
        if (externalPreview?.Runtime is not { } runtime) return;
        try { runtime.Visible = false; }
        catch (Exception error) { RecordAssetPreviewRenderFailure("Asset preview visibility", error); CloseExternalAssetPreview(); }
    }
    private bool TryDrawExternalAssetPreview()
    {
        var owner = externalPreview;
        if (owner != null && owner.Entry.Path != selectedAssetPath) { CloseExternalAssetPreview(); owner = null; }
        if (owner == null)
        {
            if (externalPreviewReattachment != null)
            { using var reattachingWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview); return true; }
            if (requestedCatalogPreview is not { } request || !HasExternalAssetPreview(request.AssetType)) return false;
            using var loadingWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview); return true;
        }
        if (owner.Runtime == null || owner.Initialization != null)
        { using var initializingWindow = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview); return true; }
        var runtime = owner.Runtime;
        if (focusAssetPreview) { Gui.SetWindowFocus(PreviewWindowCaption.Text); focusAssetPreview = false; }
        using var window = ImGuiScopes.Window(PreviewWindowCaption.Text, ref showAssetPreview);
        if (!window.DrawContents) return true;
        try
        {
            var region = owner.Surface?.Draw(owner.Provider, runtime)
                ?? new AssetPreviewSurfaceRegion(Gui.GetCursorScreenPos(), Gui.GetContentRegionAvail());
            if (region.Size.X <= 0 || region.Size.Y <= 0) return true;
            runtime.Resize((int)region.Size.X, (int)region.Size.Y); runtime.Visible = true;
            if (runtime.Output is { } texture)
            {
                if (!ReferenceEquals(texture.GraphicsDevice, GraphicsDevice))
                    throw new NotSupportedException("The explicit preview adapter must expose an output on the actual editor graphics device.");
                if (!ReferenceEquals(texture, owner.ImageTexture))
                {
                    if (owner.ImageTexture != null) renderer!.UnregisterTexture(owner.Image);
                    owner.Image = renderer!.RegisterTexture(texture); owner.ImageTexture = texture;
                }
                Gui.SetCursorScreenPos(region.Position); Gui.Image(owner.Image, region.Size);
            }
            Gui.SetCursorScreenPos(region.Position);
            EditorPreviewPointerInput.Draw("##ExternalPreviewInput", region.Size);
            DispatchExternalPreviewInput(owner, region);
        }
        catch (Exception error) { RecordAssetPreviewRenderFailure("Asset preview", error); CloseExternalAssetPreview(); }
        return true;
    }
    private void RenderExternalAssetPreview(GameTime time)
    {
        var owner = externalPreview;
        if (owner?.Runtime is not { Visible: true } runtime || owner.Initialization != null || owner.ProviderLease.IsRetiring)
        {
            if (owner != null)
            {
                owner.InputReleasePending = true;
                if (owner.Initialization?.IsCompleted != false) ReleaseExternalPreviewInput(owner);
            }
            return;
        }
        try { runtime.Update(time); runtime.Render(time); }
        catch (Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(GraphicsDevice, error); RecordAssetPreviewRenderFailure("Asset preview", error); CloseExternalAssetPreview(); }
    }
}
