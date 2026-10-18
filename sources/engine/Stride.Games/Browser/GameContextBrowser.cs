// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using System;

namespace Stride.Games;

/// <summary>A canvas context whose host advances the game once per browser frame.</summary>
public sealed class GameContextBrowser : GameContext<string>
{
    internal Action<int, int, bool, bool> UpdateWindow;

    /// <summary>Creates a context for an existing canvas selected by CSS selector.</summary>
    public GameContextBrowser(string canvasSelector = "#canvas", int requestedWidth = 0, int requestedHeight = 0)
        : base(canvasSelector, requestedWidth, requestedHeight, isUserManagingRun: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canvasSelector);
        ContextType = AppContextType.Browser;
    }

    /// <summary>Canvas pixel dimensions, focus and visibility are supplied by the browser host.</summary>
    public void AdvanceFrame(int pixelWidth, int pixelHeight, bool focused, bool visible)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelHeight);
        if (RunCallback is null) throw new InvalidOperationException("Start the game before advancing browser frames.");
        UpdateWindow?.Invoke(pixelWidth, pixelHeight, focused, visible);
        RunCallback();
    }

    /// <summary>The title requested by the game, for the host's document title.</summary>
    public string Title { get; internal set; } = string.Empty;

    /// <summary>Whether the host should show the cursor over the canvas.</summary>
    public bool IsMouseVisible { get; internal set; } = true;

    /// <summary>The opacity requested by the game for the canvas.</summary>
    public double Opacity { get; internal set; } = 1.0;
}
#endif
