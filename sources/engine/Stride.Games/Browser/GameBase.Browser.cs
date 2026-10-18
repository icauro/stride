// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER && STRIDE_GRAPHICS_API_WEBGPU
using System;
using System.Threading.Tasks;

namespace Stride.Games;

public abstract partial class GameBase : IAsyncDisposable
{
    private Task browserDisposal;

    /// <summary>
    /// Stops browser frames, releases game systems and asynchronously drains the
    /// GPU before destroying the device. The host must await this on the browser
    /// thread instead of synchronously disposing a running browser game.
    /// </summary>
    public ValueTask DisposeAsync() => new(browserDisposal ??= DisposeBrowserAsync());

    private async Task DisposeBrowserAsync()
    {
        // Publish the shared disposal task before EndRun/Closing callbacks can
        // reenter DisposeAsync. Continuations stay on the browser thread.
        await Task.Yield();
        if (IsDisposed) return;
        if (IsRunning)
        {
            Exit();
            // Complete the nonblocking platform's EndRun/Exiting/Closing path.
            // Tick sees IsExiting and performs no further updates or draws.
            var context = (GameContextBrowser)Context;
            context.AdvanceFrame(Window.ClientBounds.Width, Window.ClientBounds.Height, Window.Focused, Window.Visible);
        }
        // Systems can release resources or submit final work while the device
        // is still alive. Drain only after their teardown, then dispose once.
        if (Window is { IsActivated: true } window) window.OnPause();
        DisposeGameSystems();
        if (GraphicsDevice is { } device)
        {
            // Match GraphicsDeviceManager's teardown order: a presenter owns
            // resources that must be released before its device/context.
            device.Presenter?.Dispose();
            device.Presenter = null;
            await device.DisposeAsync();
        }
        Dispose();
    }
}
#endif
