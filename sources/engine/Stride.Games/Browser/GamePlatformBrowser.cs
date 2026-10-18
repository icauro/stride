// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using Stride.Core;

namespace Stride.Games;

internal sealed class GamePlatformBrowser : GamePlatform
{
    public GamePlatformBrowser(GameBase game) : base(game)
    {
        IsBlockingRun = false;
        FullName = "Browser WebAssembly";
    }

    public override string DefaultAppDirectory => PlatformFolders.ApplicationBinaryDirectory;

    internal override GameWindow GetSupportedGameWindow(AppContextType type)
        => type == AppContextType.Browser ? new GameWindowBrowser() : null;
}
#endif
