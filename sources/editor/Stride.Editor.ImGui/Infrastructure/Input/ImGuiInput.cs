using Hexa.NET.ImGui;
using Stride.Input;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed class ImGuiInput
{
    private static readonly Dictionary<Keys, ImGuiKey> KeyMap = CreateKeyMap();
    internal static bool TryMapKey(Keys key, out ImGuiKey mapped) => KeyMap.TryGetValue(key, out mapped);
    private static readonly Keys[] MappedKeys = KeyMap.Keys.ToArray();
    private readonly bool[] keyDown = new bool[Enum.GetValues<Keys>().Max(key => (int)key) + 1];
    private readonly Dictionary<ImGuiKey, bool> submittedKeys = [];
    private readonly bool[] mouseDown = new bool[5];
    private IKeyboardDevice? keyboard;
    private IMouseDevice? mouse;
    private bool? focused;
    private NVector2 mousePosition = new(-float.MaxValue);
    internal void SuspendNativeModalInput()
    {
        ReleaseAll(Hexa.NET.ImGui.ImGui.GetIO()); focused = false;
    }

    public void Update(InputManager input, bool active, int width, int height, float deltaTime)
    {
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.DisplaySize = new NVector2(width, height);
        io.DisplayFramebufferScale = NVector2.One;
        io.DeltaTime = Math.Max(deltaTime, 0.001f);
        if (focused != active)
        {
            if (!active) ReleaseAll(io);
            io.AddFocusEvent(active);
            focused = active;
        }
        if (!ReferenceEquals(keyboard, input.Keyboard))
        {
            ReleaseKeyboard(io);
            keyboard = input.Keyboard;
        }
        if (!ReferenceEquals(mouse, input.Mouse))
        {
            ReleaseMouse(io);
            mouse = input.Mouse;
        }
        if (!active) return;
        if (mouse != null && mousePosition.X == -float.MaxValue)
            SetMousePosition(io, new NVector2(mouse.Position.X * width, mouse.Position.Y * height));

        // Keep the device queue's transitions, including down/up pairs occurring
        // inside one frame. Polling the final state first would erase those pairs.
        foreach (var inputEvent in input.Events)
        {
            switch (inputEvent)
            {
                case KeyEvent key when ReferenceEquals(key.Device, keyboard):
                    SetKey(io, key.Key, key.IsDown);
                    break;
                case PointerEvent pointer when ReferenceEquals(pointer.Device, mouse):
                    SetMousePosition(io, new NVector2(pointer.Position.X * width, pointer.Position.Y * height));
                    break;
                case MouseButtonEvent button when ReferenceEquals(button.Device, mouse):
                    SetMouseButton(io, MapButton(button.Button), button.IsDown);
                    break;
                case MouseWheelEvent wheel when ReferenceEquals(wheel.Device, mouse) && wheel.WheelDelta != 0:
                    io.AddMouseWheelEvent(0, wheel.WheelDelta);
                    break;
                case TextInputEvent { Type: TextInputEventType.Input } text:
                    foreach (var rune in text.Text.EnumerateRunes()) io.AddInputCharacter((uint)rune.Value);
                    break;
            }
        }

        // Reconcile missing native releases/device state without flooding ImGui
        // with unchanged events; reconnecting or regaining focus also lands here.
        foreach (var key in MappedKeys) SetKey(io, key, input.IsKeyDown(key));
        SetMouseButton(io, 0, input.IsMouseButtonDown(MouseButton.Left));
        SetMouseButton(io, 1, input.IsMouseButtonDown(MouseButton.Right));
        SetMouseButton(io, 2, input.IsMouseButtonDown(MouseButton.Middle));
        SetMouseButton(io, 3, input.IsMouseButtonDown(MouseButton.Extended1));
        SetMouseButton(io, 4, input.IsMouseButtonDown(MouseButton.Extended2));
        if (mouse != null)
            SetMousePosition(io, new NVector2(mouse.Position.X * width, mouse.Position.Y * height));
    }

    private void SetKey(ImGuiIOPtr io, Keys key, bool down)
    {
        if (!KeyMap.TryGetValue(key, out var mapped) || keyDown[(int)key] == down) return;
        keyDown[(int)key] = down;
        var mappedDown = false;
        foreach (var pair in KeyMap)
            if (pair.Value == mapped && keyDown[(int)pair.Key]) { mappedDown = true; break; }
        // Aliases such as different keyboard layouts may share an ImGui key.
        SubmitKey(io, mapped, mappedDown);
        SubmitKey(io, ImGuiKey.ModCtrl, keyDown[(int)Keys.LeftCtrl] || keyDown[(int)Keys.RightCtrl]);
        SubmitKey(io, ImGuiKey.ModShift, keyDown[(int)Keys.LeftShift] || keyDown[(int)Keys.RightShift]);
        SubmitKey(io, ImGuiKey.ModAlt, keyDown[(int)Keys.LeftAlt] || keyDown[(int)Keys.RightAlt]);
        SubmitKey(io, ImGuiKey.ModSuper, keyDown[(int)Keys.LeftWin] || keyDown[(int)Keys.RightWin]);
    }

    private void SubmitKey(ImGuiIOPtr io, ImGuiKey key, bool down)
    {
        if (submittedKeys.TryGetValue(key, out var previous) ? previous == down : !down) return;
        submittedKeys[key] = down;
        io.AddKeyEvent(key, down);
    }

    private void SetMouseButton(ImGuiIOPtr io, int button, bool down)
    {
        if (button < 0 || mouseDown[button] == down) return;
        mouseDown[button] = down;
        io.AddMouseButtonEvent(button, down);
    }

    private void SetMousePosition(ImGuiIOPtr io, NVector2 position)
    {
        if (position == mousePosition) return;
        mousePosition = position;
        io.AddMousePosEvent(position.X, position.Y);
    }

    private void ReleaseKeyboard(ImGuiIOPtr io)
    {
        Array.Clear(keyDown);
        foreach (var pair in submittedKeys)
            if (pair.Value) io.AddKeyEvent(pair.Key, false);
        submittedKeys.Clear();
    }

    private void ReleaseMouse(ImGuiIOPtr io)
    {
        for (var button = 0; button < mouseDown.Length; button++) SetMouseButton(io, button, false);
        SetMousePosition(io, new NVector2(-float.MaxValue));
    }

    private void ReleaseAll(ImGuiIOPtr io)
    {
        ReleaseKeyboard(io);
        ReleaseMouse(io);
        io.InputQueueSurrogate = 0;
    }

    private static int MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Extended1 => 3,
        MouseButton.Extended2 => 4,
        _ => -1,
    };

    private static Dictionary<Keys, ImGuiKey> CreateKeyMap()
    {
        var result = new Dictionary<Keys, ImGuiKey>();
        // Most letters, function keys and navigation keys share names in both APIs.
        foreach (var key in Enum.GetValues<Keys>())
            if (Enum.TryParse<ImGuiKey>(key.ToString(), out var mapped) && mapped >= ImGuiKey.Tab && mapped < ImGuiKey.GamepadStart)
                result[key] = mapped;
        result[Keys.Back] = ImGuiKey.Backspace;
        result[Keys.Return] = ImGuiKey.Enter;
        result[Keys.CapsLock] = ImGuiKey.CapsLock;
        result[Keys.PageUp] = ImGuiKey.PageUp;
        result[Keys.PageDown] = ImGuiKey.PageDown;
        result[Keys.PrintScreen] = ImGuiKey.PrintScreen;
        result[Keys.Left] = ImGuiKey.LeftArrow;
        result[Keys.Right] = ImGuiKey.RightArrow;
        result[Keys.Up] = ImGuiKey.UpArrow;
        result[Keys.Down] = ImGuiKey.DownArrow;
        result[Keys.LeftWin] = ImGuiKey.LeftSuper;
        result[Keys.RightWin] = ImGuiKey.RightSuper;
        result[Keys.Scroll] = ImGuiKey.ScrollLock;
        result[Keys.OemQuotes] = ImGuiKey.Apostrophe;
        result[Keys.OemComma] = ImGuiKey.Comma;
        result[Keys.OemMinus] = ImGuiKey.Minus;
        result[Keys.OemPeriod] = ImGuiKey.Period;
        result[Keys.OemQuestion] = ImGuiKey.Slash;
        result[Keys.OemSemicolon] = ImGuiKey.Semicolon;
        result[Keys.OemPlus] = ImGuiKey.Equal;
        result[Keys.OemOpenBrackets] = ImGuiKey.LeftBracket;
        result[Keys.OemPipe] = ImGuiKey.Backslash;
        result[Keys.OemCloseBrackets] = ImGuiKey.RightBracket;
        result[Keys.OemTilde] = ImGuiKey.GraveAccent;
        result[Keys.Multiply] = ImGuiKey.KeypadMultiply;
        result[Keys.Add] = ImGuiKey.KeypadAdd;
        result[Keys.Subtract] = ImGuiKey.KeypadSubtract;
        result[Keys.Decimal] = ImGuiKey.KeypadDecimal;
        result[Keys.Divide] = ImGuiKey.KeypadDivide;
        result[Keys.NumPadEnter] = ImGuiKey.KeypadEnter;
        result[Keys.Apps] = ImGuiKey.Menu;
        result[Keys.OemBackslash] = ImGuiKey.Oem102;
        result[Keys.BrowserBack] = ImGuiKey.AppBack;
        result[Keys.BrowserForward] = ImGuiKey.AppForward;
        for (var i = 0; i < 10; i++)
        {
            result[Keys.D0 + i] = ImGuiKey.Key0 + i;
            result[Keys.NumPad0 + i] = ImGuiKey.Keypad0 + i;
        }
        return result;
    }
}
