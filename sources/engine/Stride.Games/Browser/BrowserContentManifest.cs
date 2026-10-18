// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Stride.Games;

internal sealed class BrowserContentManifest
{
    public int Version { get; set; }
    public string DefaultBundle { get; set; } = string.Empty;
    public List<BrowserContentFile> Files { get; set; } = [];
}

internal sealed class BrowserContentFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

[JsonSerializable(typeof(BrowserContentManifest))]
internal partial class BrowserContentJson : JsonSerializerContext;
#endif
