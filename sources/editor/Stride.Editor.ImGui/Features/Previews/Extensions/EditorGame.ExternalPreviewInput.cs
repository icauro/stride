using System.Text;
using Hexa.NET.ImGui;
using Stride.Editor.Preview;
using Stride.Input;
using Gui = Hexa.NET.ImGui.ImGui;
using SVector2 = Stride.Core.Mathematics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static readonly (Keys Engine, ImGuiKey Native)[] ExternalPreviewKeys = Enum.GetValues<Keys>().Distinct()
        .Where(key => ImGuiInput.TryMapKey(key, out _)).Select(key =>
        { ImGuiInput.TryMapKey(key, out var mapped); return (key, mapped); }).ToArray();

    private unsafe void DispatchExternalPreviewInput(ExternalPreviewOwner owner, AssetPreviewSurfaceRegion region)
    {
        if (owner.Runtime == null) return;
        var io = Gui.GetIO();
        var editorFocused = IsActiveForTick;
        var hovered = editorFocused && Gui.IsItemHovered();
        var leftClick = Gui.IsMouseClicked(ImGuiMouseButton.Left);
        var rightClick = Gui.IsMouseClicked(ImGuiMouseButton.Right);
        var middleClick = Gui.IsMouseClicked(ImGuiMouseButton.Middle);
        var extended1Click = Gui.IsMouseClicked((ImGuiMouseButton)3);
        var extended2Click = Gui.IsMouseClicked((ImGuiMouseButton)4);
        if (leftClick || rightClick || middleClick || extended1Click || extended2Click) owner.InputFocus = hovered;
        owner.PointerLeft = editorFocused && (owner.PointerLeft || hovered && leftClick) && Gui.IsMouseDown(ImGuiMouseButton.Left);
        owner.PointerRight = editorFocused && (owner.PointerRight || hovered && rightClick) && Gui.IsMouseDown(ImGuiMouseButton.Right);
        owner.PointerMiddle = editorFocused && (owner.PointerMiddle || hovered && middleClick) && Gui.IsMouseDown(ImGuiMouseButton.Middle);
        owner.PointerExtended1 = editorFocused && (owner.PointerExtended1 || hovered && extended1Click) && Gui.IsMouseDown((ImGuiMouseButton)3);
        owner.PointerExtended2 = editorFocused && (owner.PointerExtended2 || hovered && extended2Click) && Gui.IsMouseDown((ImGuiMouseButton)4);
        var captured = owner.PointerLeft || owner.PointerRight || owner.PointerMiddle || owner.PointerExtended1 || owner.PointerExtended2;
        var focused = editorFocused && owner.InputFocus && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !io.WantTextInput;
        SetExternalPreviewInputFocus(owner, focused);
        if (owner.Runtime is IAssetPreviewPointerRuntime pointer)
            pointer.HandlePointer(new(new SVector2(io.MousePos.X - region.Position.X, io.MousePos.Y - region.Position.Y),
                hovered || captured ? new SVector2(io.MouseDelta.X, io.MouseDelta.Y) : SVector2.Zero,
                new SVector2(region.Size.X, region.Size.Y), hovered ? io.MouseWheel : 0, hovered ? io.MouseWheelH : 0,
                owner.PointerLeft, owner.PointerRight, owner.PointerMiddle,
                (focused || hovered) && io.KeyCtrl, (focused || hovered) && io.KeyShift, (focused || hovered) && io.KeyAlt, hovered, captured)
            { Extended1 = owner.PointerExtended1, Extended2 = owner.PointerExtended2 });
        if (owner.Runtime is not IAssetPreviewKeyboardRuntime keyboard) return;
        if (!focused) { ReleaseExternalPreviewKeys(owner, keyboard); return; }
        foreach (var (engine, native) in ExternalPreviewKeys)
        {
            var down = Gui.IsKeyDown(native);
            if (down && !owner.DownInputKeys.Contains(engine))
            {
                owner.DownInputKeys.Add(engine);
                keyboard.HandleKeyboard(new(engine, true, false, io.KeyCtrl, io.KeyShift, io.KeyAlt));
            }
            else if (!down && owner.DownInputKeys.Remove(engine)) keyboard.HandleKeyboard(new(engine, false, false, io.KeyCtrl, io.KeyShift, io.KeyAlt));
            else if (down && Gui.IsKeyPressed(native, true)) keyboard.HandleKeyboard(new(engine, true, true, io.KeyCtrl, io.KeyShift, io.KeyAlt));
        }
        if (io.InputQueueCharacters.Size == 0) return;
        var text = new StringBuilder();
        for (var index = 0; index < io.InputQueueCharacters.Size; index++)
        {
            var scalar = (int)io.InputQueueCharacters[index];
            if (Rune.IsValid(scalar)) text.Append(new Rune(scalar).ToString());
        }
        if (text.Length != 0) keyboard.HandleText(text.ToString());
    }

    private static void SetExternalPreviewInputFocus(ExternalPreviewOwner owner, bool focused)
    {
        if (owner.DeliveredInputFocus == focused) return;
        if (owner.Runtime is IAssetPreviewFocusRuntime receiver) receiver.SetInputFocus(focused);
        owner.DeliveredInputFocus = focused;
    }
    private static void ReleaseExternalPreviewKeys(ExternalPreviewOwner owner, IAssetPreviewKeyboardRuntime keyboard)
    {
        foreach (var key in owner.DownInputKeys.ToArray())
        { keyboard.HandleKeyboard(new(key, false, false, false, false, false)); owner.DownInputKeys.Remove(key); }
    }
    private bool ReleaseExternalPreviewInput(ExternalPreviewOwner owner)
    {
        if (DateTime.UtcNow < owner.InputReleaseRetryAt) return false;
        try
        {
            SetExternalPreviewInputFocus(owner, false);
            if (owner.Runtime is IAssetPreviewKeyboardRuntime keyboard) ReleaseExternalPreviewKeys(owner, keyboard);
            if ((owner.PointerLeft || owner.PointerRight || owner.PointerMiddle || owner.PointerExtended1 || owner.PointerExtended2)
                && owner.Runtime is IAssetPreviewPointerRuntime pointer)
                pointer.HandlePointer(new(SVector2.Zero, SVector2.Zero, SVector2.Zero, 0, 0, false, false, false, false, false, false, false, false));
            owner.PointerLeft = owner.PointerRight = owner.PointerMiddle = owner.PointerExtended1 = owner.PointerExtended2 = owner.InputFocus = false;
            owner.InputReleasePending = false;
            owner.InputReleaseRetryAt = default; owner.ReportedInputReleaseError = null;
            return true;
        }
        catch (Exception error)
        {
            owner.InputReleasePending = true;
            owner.InputReleaseRetryAt = DateTime.UtcNow.AddMilliseconds(250);
            if (owner.ReportedInputReleaseError != error.ToString())
            { owner.ReportedInputReleaseError = error.ToString(); RecordAssetPreviewRenderFailure("Asset preview input release", error); }
            return false;
        }
    }
}
