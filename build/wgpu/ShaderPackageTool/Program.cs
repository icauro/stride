// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stride.Core.Storage;
using Stride.Graphics;
using Stride.Shaders;

if (args.Length is < 3 or > 4) throw new ArgumentException("Usage: ShaderPackageTool <naga-29.0.0.exe> <input-effect> <output-effect> [variant-requests.json]");
var naga = Path.GetFullPath(args[0]);
var version = (await RunNaga("--version")).Trim();
if (version != "29.0.0") throw new InvalidOperationException($"Expected naga-cli 29.0.0, got '{version}'.");
string identity;
using (var executable = File.OpenRead(naga)) identity = $"naga-cli/{version};sha256:{Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant()};prepare:1;keep-coordinate-space";
var requests = new List<VariantRequest> { new() };
if (args.Length == 4)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new JsonStringEnumConverter());
    requests.AddRange(JsonSerializer.Deserialize<VariantRequest[]>(File.ReadAllText(args[3]), options) ?? throw new InvalidDataException("Missing variant requests."));
}
using var input = File.OpenRead(args[1]);
var effect = EffectBytecode.FromStream(input);
var temporary = Directory.CreateTempSubdirectory("stride-wgsl-");
try
{
    var converted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var stage in effect.Stages)
    {
        var original = WebGpuShaderPackage.GetSpirV(stage.Data).ToArray();
        var sourceHash = Convert.ToHexString(SHA256.HashData(original));
        if (!converted.TryGetValue(sourceHash, out var package))
        {
            var variants = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var request in requests)
            {
                var depths = request.DepthBindings.ToHashSet();
                var key = WebGpuShaderBytecode.WgslVariantKey(original, depths, request.StorageFormats);
                if (variants.ContainsKey(key)) continue;
                var prepared = WebGpuShaderBytecode.Prepare(original, depths, request.StorageFormats);
                var spv = Path.Combine(temporary.FullName, "input.spv");
                var wgsl = Path.Combine(temporary.FullName, "output.wgsl");
                await File.WriteAllBytesAsync(spv, prepared);
                await RunNaga("--keep-coordinate-space", spv, wgsl);
                // Validate the emitted WGSL through the frontend as well.
                await RunNaga(wgsl);
                variants.Add(key, await File.ReadAllTextAsync(wgsl, Encoding.UTF8));
            }
            package = WebGpuShaderPackage.Create(original, variants, identity);
            converted.Add(sourceHash, package);
            Console.WriteLine($"Converted {sourceHash}: {variants.Count} WGSL resource variant(s), {package.Length} packaged bytes.");
        }
        stage.Data = package;
        stage.Id = ObjectId.FromBytes(package);
    }
    using var output = File.Create(args[2]);
    effect.WriteTo(output);
}
finally { temporary.Delete(recursive: true); }

async Task<string> RunNaga(params string[] arguments)
{
    var start = new ProcessStartInfo(naga) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch Naga.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    var output = await stdout;
    var errors = await stderr;
    if (process.ExitCode != 0) throw new InvalidDataException($"Naga conversion/validation failed ({process.ExitCode}): {errors}{output}");
    return output;
}

internal sealed class VariantRequest
{
    public uint[] DepthBindings { get; set; } = [];
    public Dictionary<uint, PixelFormat> StorageFormats { get; set; } = [];
}
