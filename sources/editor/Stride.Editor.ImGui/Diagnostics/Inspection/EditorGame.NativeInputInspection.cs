using System.Numerics;
using System.Text.Json;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string nativeInputText = "Initial Stride text";
    private string nativeInputReadOnlyText = "Stride read-only Ω 中文 日本語";
    private ImGuiInputTextCallback? nativeInputCallback, nativeReadOnlyCallback;
    private int nativeInputSelectionStart, nativeInputSelectionEnd, nativeInputByteLength;
    private int nativeReadOnlySelectionStart, nativeReadOnlySelectionEnd, nativeReadOnlyByteLength;
    private bool nativeInputFixtureSubmitted;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? nativeInputPublishError;
    private long nativeInputPublishRetryStarted;

    // A diagnostic-only fixture. It receives the normal OS input/clipboard
    // backend; it does not inject events or call authoring services.
    private unsafe void DrawNativeInputInspection()
    {
        var index = Array.IndexOf(args, "--native-input-test");
        if (index < 0) return;
        if (index + 1 >= args.Length) throw new ArgumentException("--native-input-test requires a state output path.");
        nativeInputCallback ??= data =>
        {
            nativeInputSelectionStart = data->SelectionStart; nativeInputSelectionEnd = data->SelectionEnd; nativeInputByteLength = data->BufTextLen;
            return 0;
        };
        nativeReadOnlyCallback ??= data =>
        {
            nativeReadOnlySelectionStart = data->SelectionStart; nativeReadOnlySelectionEnd = data->SelectionEnd; nativeReadOnlyByteLength = data->BufTextLen;
            return 0;
        };
        var main = Gui.GetMainViewport();
        var detachTest = args.Contains("--native-detach-test");
        if (!detachTest || !nativeInputFixtureSubmitted) Gui.SetNextWindowViewport(main.ID);
        Gui.SetNextWindowPos(main.WorkPos + new Vector2(40, 80), ImGuiCond.Once);
        Gui.SetNextWindowSize(new Vector2(720, 240), ImGuiCond.Once);
        object? editable = null, readOnly = null, fixture = null;
        var fixtureFlags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse;
        if (!detachTest) fixtureFlags |= ImGuiWindowFlags.NoDocking;
        if (Gui.Begin("Native input acceptance fixture###NativeInputFixture", fixtureFlags))
        {
            Gui.SetNextItemWidth(-1);
            Gui.InputText("Editable###NativeInputEditable", ref nativeInputText, 4096, ImGuiInputTextFlags.CallbackAlways, nativeInputCallback);
            editable = CaptureNativeInputItem(nativeInputText, nativeInputSelectionStart, nativeInputSelectionEnd, nativeInputByteLength);
            Gui.SetNextItemWidth(-1);
            Gui.InputText("Read-only###NativeInputReadOnly", ref nativeInputReadOnlyText, 4096, ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.CallbackAlways, nativeReadOnlyCallback);
            readOnly = CaptureNativeInputItem(nativeInputReadOnlyText, nativeReadOnlySelectionStart, nativeReadOnlySelectionEnd, nativeReadOnlyByteLength);
            var window = ImGuiP.GetCurrentWindowRead();
            var viewport = Gui.GetWindowViewport();
            var title = ImGuiP.TitleBarRect(window);
            fixture = new
            {
                viewportId = viewport.ID, mainViewportId = main.ID, handle = ((nint)viewport.PlatformHandle).ToInt64(),
                docked = Gui.IsWindowDocked(), dockId = Gui.GetWindowDockID(),
                focused = Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows),
                flags = (int)window.Flags, classId = window.WindowClass.ClassId,
                titleBar = CaptureNativeInputRectangle(title.Min, title.Max),
                rectangle = CaptureNativeInputRectangle(window.Pos, window.Pos + window.Size),
            };
        }
        Gui.End();
        nativeInputFixtureSubmitted = true;
        object? dockTarget = null;
        if (detachTest)
        {
            var nodes = Gui.GetCurrentContext().DockContext.Nodes.Data;
            for (var nodeIndex = 0; nodeIndex < nodes.Size; nodeIndex++)
            {
                var root = (ImGuiDockNode*)nodes[nodeIndex].Union.ValP;
                if (root == null || root->ParentNode != null || root->HostWindow == null
                    || root->HostWindow->Viewport == null || root->HostWindow->Viewport->ImGuiViewport.ID != main.ID
                    || (root->MergedFlags & (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space) == 0) continue;
                var target = root->CentralNode == null ? root : root->CentralNode;
                dockTarget = new { dockId = target->ID, flags = (int)target->MergedFlags,
                    classId = target->WindowClass.ClassId, allowUnclassed = target->WindowClass.DockingAllowUnclassed != 0,
                    rectangle = CaptureNativeInputRectangle(target->Pos, target->Pos + target->Size) };
                break;
            }
        }
        var path = Path.GetFullPath(args[index + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var nativeContext = Gui.GetCurrentContext();
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId, handle = Window.NativeWindow.Handle.ToInt64(), frame = Gui.GetFrameCount(),
            coordinateSpace = "screen", physicalInputBackend = workflowTest == null && engineSceneProbe == null && engineAdditionalProbe == null,
            controlsEnabled = Controls.Enabled, platformViewportsEnabled = platformViewports != null,
            backBufferWidth = GraphicsDevice.Presenter.BackBuffer.Width, backBufferHeight = GraphicsDevice.Presenter.BackBuffer.Height,
            editable, readOnly, fixture, dockTarget, dockingRequiresShift = Gui.GetIO().ConfigDockingWithShift,
            mouse = new { x = Gui.GetIO().MousePos.X, y = Gui.GetIO().MousePos.Y,
                hoveredViewportId = Gui.GetIO().MouseHoveredViewport, leftDown = Gui.GetIO().MouseDown[0] },
            docking = new { movingWindow = nativeContext.MovingWindow.IsNull ? 0u : nativeContext.MovingWindow.ID,
                hoveredWindow = nativeContext.HoveredWindow.IsNull ? 0u : nativeContext.HoveredWindow.ID,
                hoveredUnderMoving = nativeContext.HoveredWindowUnderMovingWindow.IsNull ? 0u : nativeContext.HoveredWindowUnderMovingWindow.ID,
                dragDropActive = nativeContext.DragDropActive, acceptId = nativeContext.DragDropAcceptIdCurr },
            nativePointer = platformViewports?.InspectNativePointer(),
        }));
        try
        {
            File.Move(temporary, path, true);
            nativeInputPublishError = null;
        }
        catch (IOException exception) when (IsNativeInputPublishContention(exception, path))
        {
            RetryNativeInputPublish(exception);
        }
        catch (UnauthorizedAccessException exception) when (IsNativeInputPublishContention(exception, path))
        {
            RetryNativeInputPublish(exception);
        }
    }

    private static bool IsNativeInputPublishContention(Exception exception, string path)
    {
        var code = exception.HResult & 0xffff;
        // Windows can report ACCESS_DENIED when replacing a file whose reader
        // omitted FILE_SHARE_DELETE. Never retry directory or read-only targets.
        return code is 32 or 33 || code == 5 && File.Exists(path)
            && (File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Directory)) == 0;
    }

    private void RetryNativeInputPublish(Exception exception)
    {
        if (nativeInputPublishError == null)
        {
            nativeInputPublishError = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            nativeInputPublishRetryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(nativeInputPublishRetryStarted) >= TimeSpan.FromSeconds(1))
            nativeInputPublishError.Throw();
        // Keep the last complete JSON snapshot; the next frame retries an atomic
        // replacement with fresh actual state, without blocking the render loop.
    }

    private static object CaptureNativeInputRectangle(Vector2 minimum, Vector2 maximum)
        => new { minX = minimum.X, minY = minimum.Y, maxX = maximum.X, maxY = maximum.Y };

    private static object CaptureNativeInputItem(string text, int selectionStart, int selectionEnd, int byteLength)
    {
        var minimum = Gui.GetItemRectMin(); var maximum = Gui.GetItemRectMax();
        return new { text, active = Gui.IsItemActive(), visible = Gui.IsItemVisible(), selectionStart, selectionEnd, byteLength,
            rectangle = new { minX = minimum.X, minY = minimum.Y, maxX = maximum.X, maxY = maximum.Y } };
    }
}
