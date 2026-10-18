// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#if STRIDE_PLATFORM_BROWSER
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stride.Core.IO;

namespace Stride.Games;

/// <summary>Preloads packaged content before synchronous game initialization.</summary>
public static class BrowserContent
{
    private static readonly SemaphoreSlim Installation = new(1, 1);

    /// <summary>
    /// Downloads a versioned manifest and its files, verifies sizes and SHA256,
    /// and installs the complete database under /data/db. Call once before
    /// starting a game; an existing nonempty database is never replaced.
    /// </summary>
    /// <returns>The default bundle name to assign to Game.AssetBundleName.</returns>
    public static async Task<string> PreloadAsync(HttpClient client, Uri manifestUri,
        long maximumBytes = 1024L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri || (manifestUri.Scheme != "http" && manifestUri.Scheme != "https"))
            throw new ArgumentException("Content manifest requires an absolute HTTP(S) URI.", nameof(manifestUri));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        await Installation.WaitAsync(cancellationToken);
        string staging = null;
        try
        {
            var root = VirtualFileSystem.GetAbsolutePath(VirtualFileSystem.ApplicationDatabasePath);
            if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length != 0)
                throw new InvalidOperationException("Preload browser content before opening a database; /data/db is already populated.");
            using var response = await client.GetAsync(manifestUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (json.Length > 1024 * 1024) throw new InvalidDataException("Content manifest exceeds 1 MiB.");
            var manifest = JsonSerializer.Deserialize(json, BrowserContentJson.Default.BrowserContentManifest)
                ?? throw new InvalidDataException("Content manifest is empty.");
            if (manifest.Version != 1 || manifest.Files is null || manifest.Files.Count == 0)
                throw new InvalidDataException("Unsupported or empty content manifest.");
            ValidatePath(manifest.DefaultBundle);
            if (manifest.DefaultBundle.Contains('/')) throw new InvalidDataException("Default bundle must be a name.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (file is null) throw new InvalidDataException("Content manifest has a null file.");
                ValidatePath(file.Path);
                if (!paths.Add(file.Path) || !portablePaths.Add(file.Path) || file.Size < 0 || file.Size > maximumBytes - total ||
                    !ValidHash(file.Sha256))
                    throw new InvalidDataException($"Invalid content manifest entry: {file.Path}");
                total += file.Size;
            }
            if (!paths.Contains("index") || !paths.Contains($"bundles/{manifest.DefaultBundle}.bundle"))
                throw new InvalidDataException("Content manifest omits its index or default bundle.");
            var parent = Path.GetDirectoryName(root);
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, ".browser-content-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var buffer = new byte[64 * 1024];
            foreach (var file in manifest.Files)
            {
                var destination = Path.Combine(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using var download = await client.GetAsync(new Uri(manifestUri, file.Path), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is { } length && length != file.Size)
                    throw new InvalidDataException($"Content length mismatch: {file.Path}");
                using var input = await download.Content.ReadAsStreamAsync(cancellationToken);
                using var output = File.Create(destination);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (count > file.Size - received) throw new InvalidDataException($"Content is larger than declared: {file.Path}");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    received += count;
                }
                if (received != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Content integrity mismatch: {file.Path}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(root)) Directory.Delete(root); // Empty only; never delete an existing database.
            Directory.Move(staging, root);
            staging = null;
            return manifest.DefaultBundle;
        }
        finally
        {
            try { if (staging is not null) Directory.Delete(staging, recursive: true); }
            finally { Installation.Release(); }
        }
    }

    private static bool ValidHash(string hash)
    {
        if (hash?.Length != 64) return false;
        foreach (var character in hash)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Empty content path.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") throw new InvalidDataException($"Invalid content path: {path}");
            foreach (var character in segment)
                if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_'))
                    throw new InvalidDataException($"Content path must be a portable relative file name: {path}");
        }
    }
}
#endif
