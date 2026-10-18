// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Stride.Graphics.WebGPU;

public static unsafe partial class WebGPU
{
    private static readonly object LibraryLock = new();
    private static nint nativeLibrary;
    private static string? loadedPath;

    static WebGPU()
    {
        // Browser hosts register statically linked, ABI-compatible core imports.
        // NativeLibrary's dynamic DLL resolver is only meaningful on native hosts.
        if (OperatingSystem.IsBrowser()) return;
        NativeLibrary.SetDllImportResolver(typeof(WebGPU).Assembly, (name, _, _) =>
        {
            if (name != LibraryName) return 0;
            lock (LibraryLock)
                return nativeLibrary != 0 ? nativeLibrary : throw new InvalidOperationException("Call WebGPU.LoadNativeLibrary with the pinned wgpu-native library before using the API.");
        });
    }

    /// <summary>Loads the exact supported native library and verifies its version and exported entry points.</summary>
    /// <remarks>The library stays loaded for the process lifetime because native callbacks and objects may outlive individual calls.</remarks>
    public static void LoadNativeLibrary(string path)
    {
        if (OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("Browser WebGPU uses statically linked core imports, not a native DLL.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute native library path is required.", nameof(path));
        path = Path.GetFullPath(path);
        lock (LibraryLock)
        {
            if (nativeLibrary != 0)
            {
                if (!string.Equals(loadedPath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("A different WebGPU library is already loaded.");
                return;
            }
            var candidate = NativeLibrary.Load(path);
            try
            {
                var version = ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(candidate, "wgpuGetVersion"))();
                if (version != NativeVersion)
                    throw new NotSupportedException($"Expected wgpu-native {NativeRelease} (0x{NativeVersion:x8}), got 0x{version:x8}.");
                foreach (var method in typeof(WebGPU).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var import = method.GetCustomAttribute<LibraryImportAttribute>();
                    if (import is not null) _ = NativeLibrary.GetExport(candidate, import.EntryPoint ?? method.Name);
                }
                nativeLibrary = candidate;
                loadedPath = path;
            }
            catch
            {
                NativeLibrary.Free(candidate);
                throw;
            }
        }
    }
}
