using System.Text.Json;
using Stride.Core.Assets;
using Stride.Assets.Entities;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private const string NativeCloseEditedName = "Native close saved – 星";
    private SceneDocument? nativeCloseOwner;
    private Guid nativeCloseEntity;
    private string? nativeCloseOriginalName;
    private readonly Dictionary<string, object> nativeCloseButtons = [];

    private void RecordNativeCloseButton(string action, bool pressed)
    {
        if (!args.Contains("--native-close-test")) return;
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        var context = Gui.GetCurrentContext();
        nativeCloseButtons[action] = new { minX = minimum.X, minY = minimum.Y, maxX = maximum.X, maxY = maximum.Y,
            id = context.LastItemData.ID, hovered = Gui.IsItemHovered(), active = Gui.IsItemActive(), pressed,
            itemFlags = (int)context.LastItemData.ItemFlags };
    }

    private void DrawNativeCloseInspection()
    {
        var index = Array.IndexOf(args, "--native-close-test");
        if (index < 0) return;
        if (index + 1 >= args.Length) throw new ArgumentException("--native-close-test requires a state path.");
        var reload = args.Contains("--native-close-reload");
        if (nativeCloseOwner == null && document is SceneDocument scene && VerificationSceneHasRuntime && !IsAuthoringPipelineUpdating)
        {
            if (document.IsDirty) throw new InvalidOperationException("Native close fixture must start clean.");
            nativeCloseOwner = scene;
            var entity = document.Asset.Hierarchy.Parts.Values.OrderBy(part => part.Entity.Id).First().Entity;
            nativeCloseEntity = entity.Id; nativeCloseOriginalName = entity.Name;
            CloseApprovalPrepared += PublishNativeCloseApproval;
            // Fixture arrangement through the real authoring/history service;
            // close and modal choices remain exclusively native OS input.
            if (!reload) document.Rename(entity, NativeCloseEditedName);
        }
        var path = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId, handle = Window.NativeWindow.Handle.ToInt64(),
            frame = Gui.GetFrameCount(), coordinateSpace = "screen", controlsEnabled = Controls.Enabled,
            physicalInputBackend = workflowTest == null && engineSceneProbe == null && engineAdditionalProbe == null,
            platformViewportsEnabled = platformViewports != null, ready = nativeCloseOwner != null,
            runtimeAvailable = VerificationSceneHasRuntime, dirty = nativeCloseOwner?.IsDirty,
            renderRevision = VerificationSceneRenderRevision,
            runtimeName = nativeCloseOwner == null ? null : InspectionViewport?.ResolveRuntimeEntity(nativeCloseOwner, nativeCloseEntity)?.Name,
            runtimeEntityId = nativeCloseOwner == null ? null : InspectionViewport?.ResolveRuntimeEntity(nativeCloseOwner, nativeCloseEntity)?.Id,
            scenePath = nativeCloseOwner?.Path, entityId = nativeCloseEntity,
            originalName = nativeCloseOriginalName, editedName = NativeCloseEditedName,
            currentName = nativeCloseOwner?.Asset.Hierarchy.Parts[nativeCloseEntity].Entity.Name,
            reload, closePromptOpen, closeRequested, exitApproved, closeError,
            mouse = new { x = Gui.GetIO().MousePos.X, y = Gui.GetIO().MousePos.Y,
                leftDown = Gui.GetIO().MouseDown[0], hoveredViewportId = Gui.GetIO().MouseHoveredViewport },
            activeId = Gui.GetCurrentContext().ActiveId, hoveredId = Gui.GetCurrentContext().HoveredId,
            nativePointer = platformViewports?.InspectNativePointer(),
            buttons = closePromptOpen ? nativeCloseButtons : null,
        }));
        try { File.Move(temporary, path, true); nativeInputPublishError = null; }
        catch (IOException exception) when (IsNativeInputPublishContention(exception, path)) { RetryNativeInputPublish(exception); }
        catch (UnauthorizedAccessException exception) when (IsNativeInputPublishContention(exception, path)) { RetryNativeInputPublish(exception); }
    }

    private void PublishNativeCloseApproval()
    {
        var owner = nativeCloseOwner ?? throw new InvalidOperationException("Native close owner unavailable.");
        // Deserialize persisted bytes into an independent asset while the project's
        // serializer registrations still exist, before normal graph retirement.
        var disk = AssetFileSerializer.Load<SceneAsset>(owner.Path).Asset;
        var path = Path.GetFullPath(args[Array.IndexOf(args, "--native-close-test") + 1]) + ".approval.json";
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId, entityId = nativeCloseEntity,
            scenePath = owner.Path, diskName = disk.Hierarchy.Parts[nativeCloseEntity].Entity.Name,
            authoredName = owner.Asset.Hierarchy.Parts[nativeCloseEntity].Entity.Name, dirty = owner.IsDirty,
            independentlyDeserialized = true,
        }));
        File.Move(path + ".tmp", path, true);
    }
}
