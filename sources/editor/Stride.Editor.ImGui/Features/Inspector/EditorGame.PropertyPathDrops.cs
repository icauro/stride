using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record PropertyPathDropTarget(NVector2 Min, NVector2 Max, nint Window, EditorAuthoringState State,
        IEditorDocument Document, Type Type, object? Expected, Func<bool> CanEdit, Func<object?> Read, Action<UPath> Assign);
    private readonly List<PropertyPathDropTarget> propertyPathDropTargets = [];

    private void BeginPropertyPathDropFrame() => propertyPathDropTargets.Clear();
    private void ClearPropertyPathDropTargets() { propertyPathDropTargets.Clear(); }

    private unsafe void RegisterPropertyPathDropTarget(IEditorDocument owner, Type type, object? expected,
        Func<bool> canEdit, Func<object?> read, Action<UPath> assign)
    {
        if (type != typeof(UFile) && type != typeof(UDirectory) || !Gui.IsItemVisible()
            || sessionHistory?.Registry.Find(owner.Path) is not { IsPublished: true, IsDisposed: false } state
            || !ReferenceEquals(state.Document, owner)) return;
        var draw = Gui.GetWindowDrawList();
        var min = NVector2.Max(Gui.GetItemRectMin(), Gui.GetClipRectMin(draw));
        var max = NVector2.Min(Gui.GetItemRectMax(), Gui.GetClipRectMax(draw));
        if (max.X <= min.X || max.Y <= min.Y) return;
        var window = (nint)Gui.GetWindowViewport().PlatformHandleRaw;
        if (window == 0) window = Window.NativeWindow.Handle;
        propertyPathDropTargets.Add(new(min, max, window, state, owner, type, expected, canEdit, read, assign));
    }

    private bool TryHandlePropertyPathFileDrop(string[] files, NVector2 physicalPosition)
    {
        if (Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)) return false;
        var actualWindow = PathDropGetAncestor(PathDropWindowFromPoint(new((int)Math.Floor(physicalPosition.X), (int)Math.Floor(physicalPosition.Y))), 2);
        for (var index = propertyPathDropTargets.Count - 1; index >= 0; index--)
        {
            var target = propertyPathDropTargets[index];
            if (physicalPosition.X < target.Min.X || physicalPosition.Y < target.Min.Y
                || physicalPosition.X >= target.Max.X || physicalPosition.Y >= target.Max.Y
                || actualWindow == 0 || actualWindow != PathDropGetAncestor(target.Window, 2)) continue;
            // A rejected path drop over this field must not become a browser import.
            if (files.Length != 1 || IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null) return true;
            if (target.State.IsDisposed || !target.State.IsPublished || !ReferenceEquals(target.State.Document, target.Document)
                || !ReferenceEquals(sessionHistory?.Registry.Find(target.State.CanonicalPath), target.State)) return true;
            UPath? path = target.Type == typeof(UFile) && File.Exists(files[0]) ? new UFile(files[0])
                : target.Type == typeof(UDirectory) && Directory.Exists(files[0]) ? new UDirectory(files[0]) : null;
            if (path == null) return true;
            Execute(() =>
            {
                if (target.State.IsDisposed || !target.State.IsPublished || !ReferenceEquals(target.State.Document, target.Document)
                    || !ReferenceEquals(sessionHistory?.Registry.Find(target.State.CanonicalPath), target.State)
                    || !target.CanEdit() || !Equals(target.Expected, target.Read()))
                    throw new InvalidOperationException("The path property changed before the file drop could be applied.");
                target.Assign(path);
            });
            return true;
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PathDropPoint(int X, int Y);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint PathDropWindowFromPoint(PathDropPoint point);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint PathDropGetAncestor(nint window, uint flags);
}
