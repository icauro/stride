// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Stride.Games;

internal sealed class GameWindowBrowser : GameWindow<string>
{
    private int width, height;
    private bool focused = true, visible = true, ended, inFrame;
    private GameContextBrowser context;
    private WindowHandle window;
    public override bool AllowUserResizing { get; set; } = true;
    public override Rectangle ClientBounds => new(0, 0, width, height);
    public override DisplayOrientation CurrentOrientation => DisplayOrientation.Default;
    public override bool IsMinimized => !visible || width == 0 || height == 0;
    public override bool Focused => focused;
    public override bool IsMouseVisible { get => context.IsMouseVisible; set => context.IsMouseVisible = value; }
    public override WindowHandle NativeWindow => window;
    public override bool Visible { get => visible; set => visible = value; }
    public override double Opacity { get => context.Opacity; set => context.Opacity = value; }
    public override bool IsBorderLess { get; set; } = true;

    protected override void Initialize(GameContext<string> gameContext)
    {
        context = (GameContextBrowser)gameContext;
        width = gameContext.RequestedWidth > 0 ? gameContext.RequestedWidth : 1280;
        height = gameContext.RequestedHeight > 0 ? gameContext.RequestedHeight : 720;
        window = new WindowHandle(AppContextType.Browser, context.Control, IntPtr.Zero);
        context.UpdateWindow = UpdateWindow;
    }

    internal override void Run()
    {
        // Never enter a blocking loop on the browser main thread.
        context.RunCallback = Advance;
        context.ExitCallback = End;
        InitCallback?.Invoke();
        OnActivated(this, EventArgs.Empty);
    }

    private void Advance()
    {
        if (ended) return;
        if (inFrame) throw new InvalidOperationException("Browser frames cannot overlap.");
        inFrame = true;
        try { RunCallback?.Invoke(); }
        finally { inFrame = false; }
        if (Exiting) End();
    }

    private void End()
    {
        if (ended) return;
        ended = true;
        // Host callbacks may continue until its next animation-frame turn.
        // Ignore resize/focus updates while asynchronous device shutdown yields.
        context.UpdateWindow = null;
        try { OnClosing(this, EventArgs.Empty); }
        finally { ExitCallback?.Invoke(); }
    }

    private void UpdateWindow(int pixelWidth, int pixelHeight, bool hasFocus, bool isVisible)
    {
        if (focused != hasFocus)
        {
            focused = hasFocus;
            if (focused) OnActivated(this, EventArgs.Empty);
            else OnDeactivated(this, EventArgs.Empty);
        }
        visible = isVisible;
        if (pixelWidth != width || pixelHeight != height)
        {
            Resize(pixelWidth, pixelHeight);
            OnClientSizeChanged(this, EventArgs.Empty);
        }
    }

    internal override void Resize(int newWidth, int newHeight)
    {
        if (newWidth == width && newHeight == height) return;
        width = newWidth; height = newHeight;
    }

    public override void BeginScreenDeviceChange(bool willBeFullScreen)
    {
        if (willBeFullScreen) throw new PlatformNotSupportedException("Browser fullscreen must be requested by the host during a user gesture.");
    }
    public override void EndScreenDeviceChange(int clientWidth, int clientHeight) => Resize(clientWidth, clientHeight);
    protected internal override void SetSupportedOrientations(DisplayOrientation orientations) { }
    protected override void SetTitle(string title) => context.Title = title;

    protected override void Destroy()
    {
        End();
        if (context is not null)
        {
            context.UpdateWindow = null;
            context.RunCallback = null;
            context.ExitCallback = null;
        }
        base.Destroy();
    }
}
#endif
