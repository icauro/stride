using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Stride.Assets.Sprite;

namespace Stride.Editor.ImGui;

internal sealed record MixedSpriteGroupInspection(EditorDocumentEntry Entry, AssetMetadataDocument Document,
    object? Preview, long RenderCount, bool Visible, bool SourceReady, string Name, bool RegisteredVisible);

internal sealed partial class EditorGame
{
    internal (string Path, string BrowserName) PrepareMixedDocumentGroupFixture(string directory)
    {
        PrepareSpriteParityFixture(directory);
        var entry = spriteParityFixtureDocument ?? throw new InvalidOperationException("The private native sprite fixture was not published.");
        var path = entry.Path;
        CloseSpriteParityFixture();
        return (path, Path.GetFileNameWithoutExtension(path));
    }

    internal MixedSpriteGroupInspection? InspectMixedSpriteGroup(string path)
    {
        var entry = documents.Find(path);
        if (entry?.Document is not AssetMetadataDocument { SpriteSettings: not null } document) return null;
        spriteCanvases.TryGetValue(document, out var canvas);
        return new(entry, document, canvas?.Preview, canvas?.Preview?.RenderCount ?? 0,
            canvas?.Preview?.Visible == true, canvas?.Pixels != null && canvas.Error == null && canvas.Preview?.RenderError == null,
            document.SpriteSettingsFrames[0].Value.Name,
            visibleNonSceneDocuments.Values.Any(item => ReferenceEquals(item, entry)));
    }

    internal object? InspectMixedSpriteGroupEvidence(string path)
    {
        if (!Controls.Enabled) return null;
        var entry = documents.Find(path);
        if (entry?.Document is not AssetMetadataDocument { SpriteSettings: not null } document)
            return new { entryId = entry?.Id, documentKind = entry?.Document.GetType().FullName };
        spriteCanvases.TryGetValue(document, out var canvas);
        return new { entryId = entry.Id, canonicalActive = documents.Active?.Id,
            selectedFrameId = document.SelectedSpriteId?.ToString(), selectedFrameCount = document.SelectedSpriteIds.Count,
            canvasExists = canvas != null, pixelsReady = canvas?.Pixels != null,
            source = canvas?.Source, sourceLoad = canvas?.Load?.Status.ToString(), sourceError = canvas?.Error?.ToString(),
            previewExists = canvas?.Preview != null, previewVisible = canvas?.Preview?.Visible,
            nativeRenderCount = canvas?.Preview?.RenderCount, previewError = canvas?.Preview?.RenderError?.ToString(),
            registeredVisible = visibleNonSceneDocuments.Values.Any(item => ReferenceEquals(item, entry)),
            nativeGroup = InspectDocumentGroupParity(entry.Id)?.NativeTabs };
    }

    internal unsafe object? InspectMixedCloseLifecycle()
    {
        if (!Controls.Enabled) return null;
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        var popups = new List<object>();
        for (var index = 0; index < context.OpenPopupStack.Size; index++)
        {
            var window = context.OpenPopupStack[index].Window;
            popups.Add(new { index, id = context.OpenPopupStack[index].PopupId,
                windowId = window == null ? 0 : window->ID,
                window = window == null ? null : Marshal.PtrToStringUTF8((nint)window->Name),
                viewportId = window == null ? 0 : window->ViewportId });
        }
        var groupViewports = new List<object>();
        foreach (var group in DocumentGroupPanel.Model.Groups)
        {
            var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(group.WindowName);
            if (window.IsNull) continue;
            var viewport = Hexa.NET.ImGui.ImGui.FindViewportByID(window.ViewportId);
            if (viewport.IsNull) continue;
            groupViewports.Add(new { group.Id, group.Floating, windowId = window.ID, viewportId = viewport.ID,
                viewportFlags = viewport.Flags.ToString(), position = new { viewport.Pos.X, viewport.Pos.Y },
                size = new { viewport.Size.X, viewport.Size.Y }, nativeWindow = (long)(nint)viewport.PlatformHandleRaw });
        }
        return new { quiescent = DocumentGroupParityCaseQuiescent,
            authoringBusy = IsAuthoringPipelineUpdating, previewBusy = IsAssetPreviewUpdating,
            projectLoad = projectLoading != null, gpuRetiring = IsEditorGpuRetirementPending,
            graphicsRecovering = IsEditorGraphicsRecovering,
            closeOwnerId = closingDocument?.Id, closeOwnerCurrent = closingDocument != null && ReferenceEquals(closingDocument.Document, closingDocumentOwner),
            pendingCloseId = pendingDocumentClose?.Id, openDocumentClose, documentCloseError,
            anyPopupOpen = Hexa.NET.ImGui.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel), popups,
            mouseHoveredViewport = io.MouseHoveredViewport,
            nativeMouseViewport = context.MouseViewport.IsNull ? 0 : context.MouseViewport.Handle->ImGuiViewport.ID,
            backendFlags = io.BackendFlags.ToString(), groupViewports,
            hoveredWindowId = context.HoveredWindow.IsNull ? 0 : context.HoveredWindow.ID,
            navWindowId = context.NavWindow.IsNull ? 0 : context.NavWindow.ID,
            cancelPoint = Controls.Centers.TryGetValue("document-close/cancel", out var point) ? new { point.X, point.Y } : null };
    }

    internal void RenameMixedSpriteThroughNativeAuthoring(string path, string name)
    {
        var current = InspectMixedSpriteGroup(path) ?? throw new InvalidOperationException("The real sprite document is absent.");
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !ReferenceEquals(documents.Active, current.Entry) || !CanMutateDocumentPresentation(current.Document))
            throw new InvalidOperationException("The private native sprite edit must retain its actual canonical command owner.");
        var frame = current.Document.SpriteSettingsFrames[0];
        var field = frame.Fields.Single(item => item.Node.Name == nameof(SpriteInfo.Name));
        ValidatePendingPropertyStrings();
        current.Document.UpdateSpriteSetting(field, name);
        current.Document.EndSpriteSettingEdit();
    }

    internal bool MixedDocumentGroupRemoved(Guid entryId, Guid groupId)
        => documents.Entries.All(entry => entry.Id != entryId)
            && DocumentGroupPanel.Model.Find(groupId) == null
            && visibleNonSceneDocuments.Values.All(entry => entry.Id != entryId);

    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    private static extern bool IsMixedGroupNativeWindow(nint handle);
    internal static bool MixedGroupNativeWindowExists(long handle) => handle != 0 && IsMixedGroupNativeWindow((nint)handle);
}
