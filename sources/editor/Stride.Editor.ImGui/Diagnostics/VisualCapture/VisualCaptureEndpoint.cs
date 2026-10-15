using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hexa.NET.ImGui;
using Stride.Graphics;
using Vector2 = System.Numerics.Vector2;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class VisualCaptureEndpoint : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly string directory;
    private readonly string requests;
    private readonly string responses;
    private readonly string hwnd;
    private readonly nint windowHandle;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, CaptureTarget> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> windowAliases = new(StringComparer.Ordinal);
    private readonly HashSet<string> implemented = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> aliasInstances = new(StringComparer.Ordinal);
    private PendingRequest? pending;
    private long nextPoll;
    private long frame;
    private Vector2 displayPosition, framebufferScale = Vector2.One;
    private CaptureDpi currentDpi = new(96, 96);
    internal Func<string, bool>? RevealTarget { get; set; }
    internal Func<string, string, JsonElement, object>? TargetAction { get; set; }
    internal Action? CancelPendingAction { get; set; }
    internal object InspectMatchedBenchmarkViewport(string assetId)
    {
        var matches = targets.Values.Where(target => target.Visible && target.State == "visible"
            && target.DocumentAssetId == assetId && (target.Id == "scene.viewport" || target.SemanticIds?.Contains("scene.viewport") == true)).ToArray();
        if (matches.Length != 1 || matches[0].PartiallyClipped || matches[0].OccludedBy?.Length > 0)
            throw new InvalidOperationException("Benchmark requires one actual visible unoccluded scene viewport rectangle.");
        return matches[0];
    }
    internal Func<object>? StateMetadata { get; set; }

    internal VisualCaptureEndpoint(string directory, nint hwnd)
    {
        this.directory = Path.GetFullPath(directory);
        this.hwnd = "0x" + hwnd.ToString("x", CultureInfo.InvariantCulture);
        windowHandle = hwnd;
        requests = Path.Combine(this.directory, "requests"); responses = Path.Combine(this.directory, "responses");
        Directory.CreateDirectory(requests); Directory.CreateDirectory(responses);
        using var process = Process.GetCurrentProcess();
        WriteAtomic(Path.Combine(this.directory, "session.json"), new
        {
            protocolVersion = 1, sessionId, processId = Environment.ProcessId,
            processStartFileTime = process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture),
            backend = "imgui", editorName = "Stride Dear ImGui editor", captureDirectory = this.directory, hwnd = this.hwnd,
            startedAt = DateTimeOffset.UtcNow, capabilities = new[] { "list", "capture", "show", "action" }
        });
        DefineTarget("editor.shell", "window", "Editor", implemented: true);
    }

    internal void DefineTarget(string id, string kind, string name, string? windowName = null, string? parentId = null, bool implemented = true)
    {
        targets.TryAdd(id, new(id, kind, name, implemented ? "closed" : "unavailable", false, hwnd,
            new(0, 0, 0, 0), "framebuffer-physical", new(96, 96), null, parentId));
        if (implemented) this.implemented.Add(id);
        if (!string.IsNullOrEmpty(windowName))
        {
            windowAliases[windowName] = id;
            var marker = windowName.IndexOf("###", StringComparison.Ordinal);
            if (marker >= 0) windowAliases[windowName[(marker + 3)..]] = id;
        }
    }

    internal void BeginFrame()
    {
        frame++;
        aliasInstances.Clear();
        foreach (var id in targets.Keys.ToArray())
        {
            var target = targets[id];
            if (target.Visible) targets[id] = target with { Visible = false, State = "hidden" };
        }
        var viewport = Gui.GetMainViewport(); displayPosition = viewport.Pos;
        var dpi = 96 * WindowsDpi.GetScale(windowHandle); currentDpi = new(dpi, dpi);
        framebufferScale = Gui.GetIO().DisplayFramebufferScale;
        if (framebufferScale.X <= 0 || framebufferScale.Y <= 0) framebufferScale = Vector2.One;
    }

    internal bool HasTarget(string id) => targets.ContainsKey(id);
    internal bool HasDocumentTarget(string id, string document, string generation)
        => targets.TryGetValue(id, out var target) && target.DocumentId == document && target.DocumentGeneration == generation;
    internal void ForgetDocument(string id)
    {
        foreach (var key in targets.Where(pair => pair.Value.DocumentId == id).Select(pair => pair.Key).ToArray())
            targets.Remove(key);
    }
    internal void ForgetDocumentTargets()
    {
        foreach (var key in targets.Where(pair => pair.Value.DocumentId != null).Select(pair => pair.Key).ToArray()) targets.Remove(key);
    }
    internal void HideInactiveDocumentAliases(string? activeDocument)
    {
        foreach (var key in targets.Where(pair => pair.Value.DocumentId != null && pair.Value.DocumentId != activeDocument
            && !pair.Key.StartsWith("documents.", StringComparison.Ordinal)).Select(pair => pair.Key).ToArray())
            targets[key] = targets[key] with { Visible = false, State = "hidden" };
    }
    internal void RegisterItem(string id, string kind = "control", string? name = null)
        => RegisterRegion(id, kind, name ?? id, Gui.GetItemRectMin(), Gui.GetItemRectMax(), null, Gui.IsItemVisible());

    internal unsafe void RegisterRegion(string id, string kind, string name, Vector2 minimum, Vector2 maximum, string? parentId = null, bool visible = true,
        VisualCaptureDocumentOwner? documentOwner = null)
    {
        var window = ImGuiP.GetCurrentWindowRead();
        var viewportId = window.IsNull ? Gui.GetMainViewport().ID : window.ViewportId;
        var original = ViewportRect(minimum, maximum, viewportId);
        if (!window.IsNull)
        {
            minimum = Vector2.Max(minimum, window.ClipRect.Min);
            maximum = Vector2.Min(maximum, window.ClipRect.Max);
            visible &= IsRenderedWindowBody(window);
            parentId ??= NativeWindowTargetId(window);
        }
        SetRegion(id, kind, name, minimum, maximum, parentId, viewportId, visible);
        targets[id] = targets[id] with
        {
            SourceRect = original, PartiallyClipped = original != targets[id].Rect,
            DocumentId = documentOwner?.Id, DocumentPath = documentOwner?.Path, DocumentKind = documentOwner?.Kind,
            DocumentAssetId = documentOwner?.AssetId, DocumentGroupId = documentOwner?.GroupId, DocumentGeneration = documentOwner?.Generation,
        };
    }

    private Vector2 ViewportScale(uint viewportId)
        => viewportId == Gui.GetMainViewport().ID ? framebufferScale : Vector2.One;
    private CaptureRect ViewportRect(Vector2 min, Vector2 max, uint viewportId)
    {
        var viewport = Gui.FindViewportByID(viewportId);
        var position = viewport.IsNull ? displayPosition : viewport.Pos;
        var scale = ViewportScale(viewportId);
        var origin = (min - position) * scale;
        var extent = (max - min) * scale;
        return new(origin.X, origin.Y, Math.Max(0, extent.X), Math.Max(0, extent.Y));
    }
    private unsafe void SetRegion(string id, string kind, string name, Vector2 min, Vector2 max, string? parentId, uint viewportId, bool visible)
    {
        var viewport = Gui.FindViewportByID(viewportId);
        var rect = ViewportRect(min, max, viewportId);
        var handle = viewport.IsNull || viewport.PlatformHandleRaw == null ? windowHandle : (nint)viewport.PlatformHandleRaw;
        var dpi = 96 * WindowsDpi.GetScale(handle);
        visible &= rect.Width > 0 && rect.Height > 0;
        targets[id] = new(id, kind, name, visible ? "visible" : "hidden", visible,
            "0x" + handle.ToString("x", CultureInfo.InvariantCulture), rect, "framebuffer-physical",
            new(dpi, dpi), viewportId, parentId, SourceRect: rect);
        implemented.Add(id);
    }

    private unsafe string WindowTargetId(ImGuiWindowPtr window)
    {
        var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
        if (windowAliases.TryGetValue(name, out var semantic)) return semantic;
        var marker = name.IndexOf("###", StringComparison.Ordinal);
        if (marker >= 0 && windowAliases.TryGetValue(name[(marker + 3)..], out semantic)) return semantic;
        marker = name.IndexOf("##", StringComparison.Ordinal);
        if (marker >= 0 && windowAliases.TryGetValue(name[..marker], out semantic)) return semantic;
        return NativeWindowTargetId(window);
    }

    private static string NativeWindowTargetId(ImGuiWindowPtr window) => "imgui.window." + window.ID.ToString("x8", CultureInfo.InvariantCulture);

    private static bool IsRenderedWindowBody(ImGuiWindowPtr window)
    {
        var frame = Gui.GetFrameCount();
        // Begin submits inactive dock tabs too; Active is not proof that their
        // body was drawn. Child regions inherit the native parent's visibility.
        var visited = new HashSet<uint>();
        for (var current = window; !current.IsNull && visited.Add(current.ID); current = current.ParentWindow)
        {
            if (!current.Active || current.Hidden || current.SkipItems || current.LastFrameActive != frame) return false;
            if (current.DockIsActive)
            {
                if (!current.DockTabIsVisible) return false;
                var node = current.DockNode;
                if (!node.IsNull && !node.VisibleWindow.IsNull && node.VisibleWindow.ID != current.ID) return false;
            }
            // Popup/modal ParentWindow is an opening context, not a rendered
            // body ancestor. Only actual child windows inherit visibility.
            if (!current.Flags.HasFlag(ImGuiWindowFlags.ChildWindow)) break;
        }
        return true;
    }

    internal unsafe void InventoryWindows()
    {
        var windows = Gui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size; index++)
        {
            var window = windows[index];
            if (window.IsNull) continue;
            var name = Marshal.PtrToStringUTF8((nint)window.Name) ?? "";
            var kind = window.Flags.HasFlag(ImGuiWindowFlags.Tooltip) ? "tooltip" : window.Flags.HasFlag(ImGuiWindowFlags.Modal) ? "modal" : window.Flags.HasFlag(ImGuiWindowFlags.Popup) ? "popup"
                : window.Flags.HasFlag(ImGuiWindowFlags.ChildWindow) ? "panel" : "window";
            var visible = IsRenderedWindowBody(window);
            var parentId = window.ParentWindow.IsNull ? null : NativeWindowTargetId(window.ParentWindow);
            var id = NativeWindowTargetId(window);
            var alias = WindowTargetId(window);
            SetRegion(id, kind, name, window.Pos, window.Pos + window.Size, parentId, window.ViewportId, visible);
            var viewport = Gui.FindViewportByID(window.ViewportId);
            var drawData = viewport.IsNull ? Gui.GetDrawData() : new ImDrawDataPtr(viewport.Handle->DrawData);
            var drawOrder = -1;
            for (var drawIndex = 0; !drawData.IsNull && drawIndex < drawData.CmdListsCount; drawIndex++)
                if (drawData.CmdLists[drawIndex].Handle == window.DrawList.Handle) { drawOrder = drawIndex; break; }
            targets[id] = targets[id] with { NativeId = window.ID.ToString("x8", CultureInfo.InvariantCulture), SemanticIds = alias == id ? [] : [alias], DrawOrder = drawOrder,
                RenderedRect = drawOrder < 0 ? null : RenderedScissorBounds(window, drawData) };
            if (alias != id)
            {
                if (visible) aliasInstances[alias] = aliasInstances.GetValueOrDefault(alias) + 1;
                if (!targets.TryGetValue(alias, out var existing) || visible || !existing.Visible)
                    targets[alias] = targets[id] with { Id = alias, InstanceId = id };
            }
        }
        foreach (var pair in aliasInstances.Where(pair => pair.Value > 1)) targets[pair.Key] = targets[pair.Key] with { Visible = false, State = "unavailable", InstanceId = null };
        var io = Gui.GetIO();
        SetRegion("editor.shell", "window", "Editor", displayPosition, displayPosition + io.DisplaySize, null, Gui.GetMainViewport().ID, true);
        var occluders = targets.Values.Where(target => target.Visible && target.RenderedRect != null && target.DrawOrder >= 0 && target.Id.StartsWith("imgui.window.", StringComparison.Ordinal)).ToArray();
        foreach (var id in targets.Keys.ToArray())
        {
            var target = targets[id];
            if (!target.Visible || target.Id == "editor.shell") continue;
            bool BelongsTo(CaptureTarget start, CaptureTarget ancestor)
            {
                var current = start;
                var visited = new HashSet<string>();
                while (visited.Add(current.Id))
                {
                    if (current.Id == ancestor.Id || current.InstanceId == ancestor.Id || current.Id == ancestor.InstanceId
                        || current.NativeId != null && current.NativeId == ancestor.NativeId) return true;
                    if (current.ParentId is not { } parent || !targets.TryGetValue(parent, out var next)) return false;
                    current = next;
                }
                return false;
            }
            var orderTarget = target;
            var orderVisited = new HashSet<string>();
            while (orderTarget.DrawOrder < 0 && orderTarget.ParentId is { } orderParent && orderVisited.Add(orderParent) && targets.TryGetValue(orderParent, out var owner)) orderTarget = owner;
            var occluded = occluders.Where(window => window.ViewportId == target.ViewportId && window.DrawOrder > orderTarget.DrawOrder
                && !BelongsTo(target, window)
                && (window.Kind is "popup" or "modal" or "tooltip" || !BelongsTo(window, target)
                    && !(target.NativeId == null && BelongsTo(window, orderTarget) && Contains(target.Rect, window.Rect)))
                && Intersects(window.RenderedRect!, target.Rect)).Select(window => window.Id).ToArray();
            targets[id] = target with { OccludedBy = occluded };
        }
    }

    private static unsafe CaptureRect? RenderedScissorBounds(ImGuiWindowPtr window, ImDrawDataPtr data)
    {
        var width = Math.Max(0, (int)(data.DisplaySize.X * data.FramebufferScale.X));
        var height = Math.Max(0, (int)(data.DisplaySize.Y * data.FramebufferScale.Y));
        var minimumX = width; var minimumY = height; var maximumX = 0; var maximumY = 0;
        var list = window.DrawList;
        for (var index = 0; index < list.CmdBuffer.Size; index++)
        {
            var command = list.CmdBuffer[index];
            if (command.ElemCount == 0) continue;
            var clip = command.ClipRect;
            // Same physical scissor conversion as ImGuiRenderer.Render.
            var left = Math.Clamp((int)((clip.X - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
            var top = Math.Clamp((int)((clip.Y - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
            var right = Math.Clamp((int)MathF.Ceiling((clip.Z - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
            var bottom = Math.Clamp((int)MathF.Ceiling((clip.W - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
            if (right <= left || bottom <= top) continue;
            minimumX = Math.Min(minimumX, left); minimumY = Math.Min(minimumY, top);
            maximumX = Math.Max(maximumX, right); maximumY = Math.Max(maximumY, bottom);
        }
        if (maximumX <= minimumX || maximumY <= minimumY) return null;
        var scale = data.FramebufferScale; var origin = data.DisplayPos;
        var outerLeft = (window.Pos.X - origin.X) * scale.X;
        var outerTop = (window.Pos.Y - origin.Y) * scale.Y;
        var outerRight = (window.Pos.X + window.Size.X - origin.X) * scale.X;
        var outerBottom = (window.Pos.Y + window.Size.Y - origin.Y) * scale.Y;
        var x = Math.Max(minimumX, outerLeft); var y = Math.Max(minimumY, outerTop);
        var rightEdge = Math.Min(maximumX, outerRight); var bottomEdge = Math.Min(maximumY, outerBottom);
        return rightEdge > x && bottomEdge > y ? new(x, y, rightEdge - x, bottomEdge - y) : null;
    }

    internal unsafe void CompleteFrame(Texture backBuffer, GraphicsContext context, Func<uint, Texture?>? viewportBuffer = null)
    {
        // UpdatePlatformWindows can create the native HWND after UI registration.
        // Keep rendered pixel rectangles, but resolve the now-current native owner.
        foreach (var id in targets.Keys.ToArray())
        {
            var registered = targets[id];
            if (!registered.Visible || registered.ViewportId is not { } ownerId) continue;
            var owner = Gui.FindViewportByID(ownerId);
            if (owner.IsNull || owner.PlatformHandleRaw == null) continue;
            var handle = (nint)owner.PlatformHandleRaw;
            var dpi = 96 * WindowsDpi.GetScale(handle);
            targets[id] = registered with { Hwnd = "0x" + handle.ToString("x", CultureInfo.InvariantCulture), Dpi = new(dpi, dpi) };
        }
        if (pending == null && Stopwatch.GetTimestamp() >= nextPoll)
        {
            nextPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
            ReadRequest();
        }
        if (pending is not { } current) return;
        try
        {
            var request = current.Request;
            if (request.Operation == "list")
            {
                Respond(request.Id, new { targets = targets.Values.OrderBy(target => target.Id).ToArray() }); pending = null; return;
            }
            if (request.Operation == "action")
            {
                if (TargetAction == null) throw new CaptureFailure("action-unavailable", "This editor has no scripted navigation handler.");
                if (!current.ActionStarted)
                {
                    current.ActionStarted = true;
                    current.ActionResult = TargetAction(request.Target!, request.Action!, request.Parameters);
                }
                object? result = current.ActionResult;
                if (result is Task<object> completion)
                {
                    if (!completion.IsCompleted)
                    {
                        if (current.Elapsed.ElapsedMilliseconds > Math.Clamp(request.TimeoutMs, 100, 120000))
                        { CancelPendingAction?.Invoke(); throw new CaptureFailure("action-timeout", "The real asynchronous geometry action did not complete within the bounded timeout."); }
                        return;
                    }
                    result = completion.GetAwaiter().GetResult();
                }
                Respond(request.Id, new { action = request.Action, target = request.Target, result });
                pending = null; return;
            }
            if (!current.RevealAttempted)
            {
                current.RevealAttempted = true;
                if ((request.Reveal || request.Operation == "show") && !(RevealTarget?.Invoke(request.Target!) ?? false))
                    throw new CaptureFailure("reveal-unavailable", "This target cannot be revealed without an editor workflow.");
            }
            if (aliasInstances.GetValueOrDefault(request.Target!) > 1) throw new CaptureFailure("ambiguous-target", "Multiple visible instances match this semantic target; select an actual native target id.");
            if (targets.TryGetValue(request.Target!, out var target) && target.Visible)
            {
                var captureBuffer = target.ViewportId == Gui.GetMainViewport().ID ? backBuffer
                    : target.ViewportId is { } viewportId ? viewportBuffer?.Invoke(viewportId) : null;
                if (request.Focus && !current.FocusAttempted)
                {
                    current.FocusAttempted = true;
                    var windowTarget = target;
                    var visited = new HashSet<string>();
                    while (!windowTarget.Id.StartsWith("imgui.window.", StringComparison.Ordinal) && windowTarget.InstanceId == null
                        && windowTarget.ParentId is { } parentId && visited.Add(parentId) && targets.TryGetValue(parentId, out var parent)) windowTarget = parent;
                    if (windowTarget.Id != "editor.shell") Gui.SetWindowFocus(windowTarget.Name);
                    if (nint.TryParse(target.Hwnd.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var targetHandle))
                        NativeSetForegroundWindow(targetHandle);
                    current.StableFrames = 0; return;
                }
                if (current.PreviousRect == target.Rect && current.PreviousViewport == target.ViewportId
                    && current.PreviousGeneration == target.DocumentGeneration) current.StableFrames++;
                else
                {
                    current.PreviousRect = target.Rect; current.PreviousViewport = target.ViewportId;
                    current.PreviousGeneration = target.DocumentGeneration; current.StableFrames = 1;
                }
                if (current.StableFrames >= Math.Max(1, request.SettleFrames))
                {
                    if (request.Operation == "show") Respond(request.Id, new { targets = new[] { target } }, "ready");
                    else
                    {
                        if (captureBuffer == null) throw new CaptureFailure("viewport-not-rendered", "The target's native viewport has no rendered output in this frame.");
                        var rect = Clip(target.Rect, captureBuffer.Width, captureBuffer.Height);
                        if (rect.Width <= 0 || rect.Height <= 0) throw new CaptureFailure("clipped-target", "The target has no pixels in the rendered framebuffer.");
                        if (target.PartiallyClipped || rect.X > target.Rect.X || rect.Y > target.Rect.Y
                            || rect.X + rect.Width < target.Rect.X + target.Rect.Width || rect.Y + rect.Height < target.Rect.Y + target.Rect.Height)
                            throw new CaptureFailure("clipped-target", "The complete target is clipped by its window or framebuffer.");
                        if (target.OccludedBy is { Length: > 0 }) throw new CaptureFailure("occluded-target", "A later rendered window covers part of this target; capture that window or the whole editor explicitly.");
                        var output = Path.GetFullPath(request.Output!, directory);
                        if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new CaptureFailure("invalid-output", "Capture output must be a PNG path.");
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        var temporary = output + "." + request.Id + ".tmp";
                        try
                        {
                            using (var stream = File.Create(temporary))
                            {
                                if (rect.X == 0 && rect.Y == 0 && rect.Width == captureBuffer.Width && rect.Height == captureBuffer.Height)
                                    captureBuffer.Save(context.CommandList, stream, ImageFileType.Png);
                                else
                                {
                                    using var region = Texture.New2D(captureBuffer.GraphicsDevice, (int)rect.Width, (int)rect.Height, captureBuffer.Format, TextureFlags.ShaderResource);
                                    context.CommandList.CopyRegion(captureBuffer, 0, new ResourceRegion((int)rect.X, (int)rect.Y, 0, (int)(rect.X + rect.Width), (int)(rect.Y + rect.Height), 1), region, 0);
                                    region.Save(context.CommandList, stream, ImageFileType.Png);
                                }
                            }
                            File.Move(temporary, output, true);
                        }
                        finally
                        {
                            // Secondary presenters have already called EndDraw. Native
                            // readback may transition their buffer to CopySource; restore
                            // the present layout before the owning presenter submits it.
                            if (target.ViewportId != Gui.GetMainViewport().ID)
                                context.CommandList.ResourceBarrierTransition(captureBuffer, BarrierLayout.Present);
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                        Respond(request.Id, new { capture = new { target, output, rect, sourceRect = target.SourceRect,
                            partiallyClipped = target.PartiallyClipped || rect != target.Rect, occludedBy = target.OccludedBy,
                            coordinateSpace = "framebuffer-physical", dpi = target.Dpi, transport = "gpu", hwnd = target.Hwnd, state = "visible" } });
                    }
                    pending = null; return;
                }
            }
            else current.StableFrames = 0;
            if (current.Elapsed.ElapsedMilliseconds >= request.TimeoutMs)
                throw new CaptureFailure(targets.ContainsKey(request.Target!) ? "target-not-visible" : "unknown-target", "The target did not become visible before the capture timeout.");
        }
        catch (Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(backBuffer.GraphicsDevice, error);
            RespondError(current.Request.Id, error is CaptureFailure failure ? failure.Code : "capture-failed", error.Message);
            pending = null;
        }
    }

    private void ReadRequest()
    {
        foreach (var file in Directory.EnumerateFiles(requests, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (File.Exists(Path.Combine(responses, id + ".json"))) continue;
            try
            {
                if (new FileInfo(file).Length > 65536) throw new CaptureFailure("invalid-request", "The request exceeds the protocol size limit.");
                var request = JsonSerializer.Deserialize<CaptureRequest>(File.ReadAllText(file), Json) ?? throw new CaptureFailure("invalid-request", "The request is empty.");
                if (request.ProtocolVersion != 1 || request.SessionId != sessionId || request.Id != id || id.Length is < 1 or > 100 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                    throw new CaptureFailure("invalid-request", "The request version or identity is invalid.");
                if (request.Operation is not ("list" or "capture" or "show" or "action") || request.Operation != "list" && string.IsNullOrWhiteSpace(request.Target)
                    || request.Operation == "capture" && string.IsNullOrWhiteSpace(request.Output))
                    throw new CaptureFailure("invalid-request", "The requested operation or parameters are unsupported.");
                if (request.Operation == "action" && string.IsNullOrWhiteSpace(request.Action)) throw new CaptureFailure("invalid-request", "Specify an allowlisted navigation action.");
                request.TimeoutMs = Math.Clamp(request.TimeoutMs, 1, 120000); request.SettleFrames = Math.Clamp(request.SettleFrames, 0, 1200);
                pending = new(request); return;
            }
            catch (Exception error) { RespondError(id, error is CaptureFailure failure ? failure.Code : "invalid-request", error.Message); }
        }
    }

    private void Respond(string id, object payload, string status = "ok")
    {
        var result = new Dictionary<string, object?> { ["protocolVersion"] = 1, ["id"] = id, ["status"] = status,
            ["processId"] = Environment.ProcessId, ["backend"] = "imgui", ["frame"] = frame, ["metadata"] = StateMetadata?.Invoke() };
        foreach (var property in JsonSerializer.SerializeToElement(payload, Json).EnumerateObject()) result[property.Name] = property.Value;
        WriteAtomic(Path.Combine(responses, id + ".json"), result);
    }
    private void RespondError(string id, string code, string message) => Respond(id, new { error = new { code, message } }, "error");
    private static void WriteAtomic(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static CaptureRect Clip(CaptureRect rect, int width, int height)
    {
        var x = Math.Clamp((int)Math.Floor(rect.X), 0, width); var y = Math.Clamp((int)Math.Floor(rect.Y), 0, height);
        var right = Math.Clamp((int)Math.Ceiling(rect.X + rect.Width), 0, width); var bottom = Math.Clamp((int)Math.Ceiling(rect.Y + rect.Height), 0, height);
        return new(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
    private static bool Intersects(CaptureRect left, CaptureRect right) => left.X < right.X + right.Width && right.X < left.X + left.Width
        && left.Y < right.Y + right.Height && right.Y < left.Y + left.Height;
    private static bool Contains(CaptureRect outer, CaptureRect inner) => inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
    public void Dispose()
    {
        if (pending?.ActionResult is Task<object>) CancelPendingAction?.Invoke();
        if (pending is { } current) RespondError(current.Request.Id, "editor-closed", "The editor closed before completing the request.");
        pending = null;
    }
    private sealed class PendingRequest(CaptureRequest request)
    {
        internal CaptureRequest Request { get; } = request;
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        internal bool ActionStarted;
        internal object? ActionResult;
        internal bool RevealAttempted;
        internal bool FocusAttempted;
        internal int StableFrames;
        internal CaptureRect? PreviousRect;
        internal uint? PreviousViewport;
        internal string? PreviousGeneration;
    }
    private sealed class CaptureRequest
    {
        public int ProtocolVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public string? SessionId { get; set; }
        public string Operation { get; set; } = "";
        public string? Target { get; set; }
        public string? Output { get; set; }
        public string? Action { get; set; }
        public JsonElement Parameters { get; set; }
        public int SettleFrames { get; set; } = 2;
        public int TimeoutMs { get; set; } = 15000;
        public bool Focus { get; set; }
        public bool Reveal { get; set; }
    }
    private sealed class CaptureFailure(string code, string message) : Exception(message) { internal string Code { get; } = code; }
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetForegroundWindow(nint window);
    private sealed record CaptureRect(float X, float Y, float Width, float Height);
    private sealed record CaptureDpi(float X, float Y);
    private sealed record CaptureTarget(string Id, string Kind, string Name, string State, bool Visible, string Hwnd,
        CaptureRect Rect, string CoordinateSpace, CaptureDpi Dpi, uint? ViewportId, string? ParentId,
        CaptureRect? SourceRect = null, bool PartiallyClipped = false, string[]? OccludedBy = null, string? NativeId = null,
        string[]? SemanticIds = null, string? InstanceId = null, int DrawOrder = -1,
        string? DocumentId = null, string? DocumentPath = null, string? DocumentKind = null, string? DocumentAssetId = null,
        string? DocumentGroupId = null, string? DocumentGeneration = null, CaptureRect? RenderedRect = null);
}

internal sealed record VisualCaptureDocumentOwner(string Id, string Path, string Kind, string? AssetId, string? GroupId, string Generation);
