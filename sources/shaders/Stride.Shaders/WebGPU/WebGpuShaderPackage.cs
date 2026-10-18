// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Stride.Shaders;

/// <summary>Versioned WebGPU shader data with native SPIR-V and precompiled WGSL resource variants.</summary>
public static class WebGpuShaderPackage
{
    private const uint Magic = 0x31475753; // SWG1
    private const int Version = 1;
    private const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool IsPackage(ReadOnlySpan<byte> data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    /// <summary>Gets the original SPIR-V for reflection and native specialization.</summary>
    public static ReadOnlySpan<byte> GetSpirV(ReadOnlySpan<byte> data)
    {
        if (!IsPackage(data)) return data;
        if (data.Length < 12 || data.Length > MaximumBytes) throw new InvalidDataException("Invalid WebGPU shader package length.");
        if (BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != Version) throw new NotSupportedException("Unsupported WebGPU shader package version.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (length < 20 || length % 4 != 0 || length > data.Length - 12) throw new InvalidDataException("Invalid packaged SPIR-V length.");
        var spirv = data.Slice(12, length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(spirv) != 0x07230203) throw new InvalidDataException("Invalid packaged SPIR-V header.");
        return spirv;
    }

    public static string VariantKey(IEnumerable<uint> depthBindings, IEnumerable<KeyValuePair<uint, uint>> storageFormats)
        => "depth:" + string.Join(",", depthBindings.Distinct().OrderBy(binding => binding).Select(binding => binding.ToString(CultureInfo.InvariantCulture))) + ";storage:" +
           string.Join(",", storageFormats.OrderBy(pair => pair.Key).Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Packages offline-converted shaders. Converter identity participates in the package's content hash.</summary>
    public static byte[] Create(byte[] spirv, IReadOnlyDictionary<string, string> variants, string converterIdentity)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentException.ThrowIfNullOrWhiteSpace(converterIdentity);
        if (IsPackage(spirv) || spirv.Length < 20 || spirv.Length % 4 != 0 || BinaryPrimitives.ReadUInt32LittleEndian(spirv) != 0x07230203)
            throw new ArgumentException("Expected original SPIR-V.", nameof(spirv));
        if (variants.Count is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(variants));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(spirv.Length); writer.Write(spirv);
            writer.Write(converterIdentity); writer.Write(variants.Count);
            foreach (var variant in variants.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(variant.Key);
                ArgumentException.ThrowIfNullOrWhiteSpace(variant.Value);
                writer.Write(variant.Key); writer.Write(variant.Value);
                if (stream.Length > MaximumBytes) throw new InvalidDataException("WebGPU shader package exceeds 64 MiB.");
            }
        }
        return stream.ToArray();
    }

    /// <summary>Selects an exact offline variant; never substitutes a different resource layout.</summary>
    public static string SelectWgsl(byte[] data, string key)
    {
        if (!IsPackage(data)) throw new NotSupportedException("Browser WebGPU requires an offline WGSL shader package; SPIR-V cannot be submitted to the browser.");
        var spirv = GetSpirV(data);
        using var stream = new MemoryStream(data, writable: false);
        stream.Position = 12 + spirv.Length;
        using var reader = new BinaryReader(stream, Utf8);
        var converter = reader.ReadString();
        var count = reader.ReadInt32();
        if (count is < 1 or > 1024) throw new InvalidDataException("Invalid WGSL variant count.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string? selected = null;
        for (var index = 0; index < count; index++)
        {
            var candidate = reader.ReadString();
            var code = reader.ReadString();
            if (!keys.Add(candidate) || string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Invalid or duplicate WGSL variant.");
            if (candidate == key) selected = code;
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing WebGPU shader package data.");
        return selected ?? throw new NotSupportedException($"Missing offline WGSL variant '{key}' (converter {converter}). Rebuild this effect with the required depth/storage resource formats.");
    }
}
