// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using System;
using System.Collections.Generic;
using System.Linq;
using Stride.Core.Mathematics;

namespace Stride.Input;

/// <summary>Browser events supplied by the canvas host, consumed by the normal input update.</summary>
public sealed class InputSourceBrowser : InputSourceBase
{
    private BrowserKeyboard keyboard;
    private BrowserMouse mouse;
    private BrowserPointer pointer;
    private bool disposed;

    public override void Initialize(InputManager inputManager)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keyboard is not null) throw new InvalidOperationException("Browser input is already initialized.");
        keyboard = new BrowserKeyboard(this);
        mouse = new BrowserMouse(this);
        pointer = new BrowserPointer(this);
        RegisterDevice(keyboard);
        RegisterDevice(mouse);
        RegisterDevice(pointer);
    }

    /// <summary>Accepts a physical KeyboardEvent.code; false means the code is not mapped.</summary>
    public bool SubmitKey(string code, bool down)
    {
        CheckReady();
        var name = code switch
        {
            "ArrowLeft" => "Left", "ArrowRight" => "Right", "ArrowUp" => "Up", "ArrowDown" => "Down",
            "ShiftLeft" => "LeftShift", "ShiftRight" => "RightShift",
            "ControlLeft" => "LeftCtrl", "ControlRight" => "RightCtrl",
            "AltLeft" => "LeftAlt", "AltRight" => "RightAlt", "MetaLeft" => "LeftWin", "MetaRight" => "RightWin",
            "Backspace" => "Back", "Enter" => "Return", "NumpadEnter" => "Return",
            "Space" => "Space", "Escape" => "Escape", "Tab" => "Tab",
            "Home" => "Home", "End" => "End", "PageUp" => "PageUp", "PageDown" => "PageDown",
            "Insert" => "Insert", "Delete" => "Delete", "CapsLock" => "CapsLock",
            _ when code?.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] is >= 'A' and <= 'Z' => code[3..],
            _ when code?.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(code[5]) => "D" + code[5],
            _ when code?.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code[1..], out var function) && function is >= 1 and <= 24 => code,
            _ => null,
        };
        if (name is null || !Enum.TryParse<Keys>(name, out var key)) return false;
        if (down) keyboard.HandleKeyDown(key); else keyboard.HandleKeyUp(key);
        return true;
    }

    public void SetSurfaceSize(int width, int height)
    {
        CheckReady();
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var size = new Vector2(Math.Max(1, width), Math.Max(1, height));
        mouse.Resize(size);
        pointer.Resize(size);
    }

    public void SubmitMouseMove(float x, float y) { CheckReady(); mouse.Move(Position(x, y)); }
    public void SubmitMouseButton(int button, bool down)
    {
        CheckReady();
        if (button is < 0 or > 4) return;
        mouse.Button((MouseButton)button, down);
    }
    /// <summary>Wheel motion in ticks, positive upwards.</summary>
    public void SubmitWheel(float ticks) { CheckReady(); if (float.IsFinite(ticks)) mouse.Wheel(ticks); }
    public void CancelMouse() { CheckReady(); mouse.Release(); }
    public void SubmitPointer(int id, PointerEventType type, float x, float y)
    {
        CheckReady();
        if (type is < PointerEventType.Pressed or > PointerEventType.Canceled) throw new ArgumentOutOfRangeException(nameof(type));
        pointer.Submit(id, type, Position(x, y));
    }

    public override void Pause()
    {
        if (keyboard is null || disposed) return;
        foreach (var key in keyboard.DownKeys.ToArray()) keyboard.HandleKeyUp(key);
        mouse.Release();
        pointer.Cancel();
    }
    public override void Dispose()
    {
        if (disposed) return;
        Pause();
        disposed = true;
        base.Dispose();
    }
    private void CheckReady()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (keyboard is null) throw new InvalidOperationException("Initialize browser input before submitting events.");
    }
    private static Vector2 Position(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        return new Vector2(x, y); // Captured pointers may move outside [0,1].
    }

    private sealed class BrowserKeyboard(IInputSource source) : KeyboardDeviceBase
    {
        public override string Name => "Browser keyboard";
        public override Guid Id { get; } = Guid.NewGuid();
        public override IInputSource Source => source;
    }
    private sealed class BrowserMouse : MouseDeviceBase
    {
        public BrowserMouse(IInputSource source) { Source = source; SetSurfaceSize(Vector2.One); }
        public override string Name => "Browser mouse";
        public override Guid Id { get; } = Guid.NewGuid();
        public override IInputSource Source { get; }
        public override bool IsPositionLocked => false;
        public void Resize(Vector2 size) => SetSurfaceSize(size);
        public void Move(Vector2 normalized) => MouseState.HandleMove(normalized * SurfaceSize);
        public void Button(MouseButton button, bool down) { if (down) MouseState.HandleButtonDown(button); else MouseState.HandleButtonUp(button); }
        public void Wheel(float ticks) => MouseState.HandleMouseWheel(ticks);
        public void Release() { foreach (var button in DownButtons.ToArray()) MouseState.HandleButtonUp(button); }
        public override void SetPosition(Vector2 position) => throw new PlatformNotSupportedException("Browsers do not permit cursor warping.");
        public override void LockPosition(bool forceCenter = false) => throw new PlatformNotSupportedException("Pointer lock requires an asynchronous host user gesture.");
        public override void UnlockPosition() { }
    }
    private sealed class BrowserPointer : PointerDeviceBase
    {
        private readonly Dictionary<int, (int Slot, Vector2 Position)> active = [];
        public BrowserPointer(IInputSource source) { Source = source; SetSurfaceSize(Vector2.One); }
        public override string Name => "Browser touch and pen";
        public override Guid Id { get; } = Guid.NewGuid();
        public override IInputSource Source { get; }
        public void Resize(Vector2 size) => SetSurfaceSize(size);
        public void Submit(int id, PointerEventType type, Vector2 position)
        {
            if (type == PointerEventType.Pressed)
            {
                if (active.ContainsKey(id)) return;
                var slot = 0;
                while (active.Values.Any(point => point.Slot == slot)) slot++;
                active.Add(id, (slot, position));
            }
            if (!active.TryGetValue(id, out var point)) return;
            PointerState.PointerInputEvents.Add(new PointerDeviceState.InputEvent { Id = point.Slot, Type = type, Position = position });
            if (type is PointerEventType.Released or PointerEventType.Canceled) active.Remove(id);
            else active[id] = (point.Slot, position);
        }
        public void Cancel()
        {
            foreach (var (id, point) in active.ToArray()) Submit(id, PointerEventType.Canceled, point.Position);
        }
    }
}
#endif
