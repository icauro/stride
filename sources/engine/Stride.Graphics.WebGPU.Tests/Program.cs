// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Graphics;
using Stride.Core.Mathematics;

if (args.Length == 2 && args[0] == "--export-wgsl")
{
    WgslValidation.Export(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--wgsl")
{
    using var device = GraphicsDevice.New();
    WgslValidation.Run(device, args[1]);
    return;
}

if (args.Contains("--portable"))
{
    Stride.Graphics.WebGPU.WebGPU.LoadNativeLibrary(Environment.GetEnvironmentVariable("STRIDE_WEBGPU_NATIVE_LIBRARY") ?? throw new Exception("Missing native provider."));
    GraphicsAdapterFactory.Dispose();
    await GraphicsAdapterFactory.InitializeWebGPUAsync();
    await using (var portable = GraphicsDevice.New())
    {
        using var commands = CommandList.New(portable);
        using var data = Stride.Graphics.Buffer.New(portable, new uint[] { 1, 2, 3, 4 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
        await portable.WaitForWebGPUIdleAsync();
        if (!data.GetData<uint>(commands).SequenceEqual(new uint[] { 1, 2, 3, 4 }))
            throw new Exception("Portable GraphicsDevice buffer roundtrip failed.");
        Console.WriteLine($"PASS: prepared portable GraphicsDevice startup, queue and buffer roundtrip on {portable.RendererName}.");
    }
    GraphicsAdapterFactory.Dispose();
    return;
}

if (GraphicsDevice.Platform != GraphicsPlatform.WebGPU) throw new InvalidOperationException("This validation must be built with StrideGraphicsApi=WebGPU.");
Console.WriteLine($"Adapter: {GraphicsAdapterFactory.DefaultAdapter.Description}");
using (var device = GraphicsDevice.New(GraphicsAdapterFactory.DefaultAdapter, DeviceCreationFlags.None, windowHandle: null))
{
    Console.WriteLine($"PASS: Stride GraphicsDevice created: {device.RendererName}");
    FormatValidation.Run(device);
    using var buffer = Stride.Graphics.Buffer.New(device, new uint[] { 1, 2, 3, 4 }, BufferFlags.VertexBuffer, GraphicsResourceUsage.Default);
    device.WaitForGpuIdle();
    Console.WriteLine("PASS: Stride buffer upload and device queue completion.");
    using var commands = CommandList.New(device);
    commands.BeginProfile(Color4.White, "WebGPU validation — upload");
    commands.EndProfile();
    commands.BeginProfile(Color4.White, "Outer scope across submission");
    commands.BeginProfile(Color4.White, "Nested scope across submission");
    commands.Flush();
    commands.Reset();
    commands.EndProfile();
    commands.Flush();
    commands.Reset();
    commands.EndProfile();
    commands.Flush();
    commands.Reset();
    try { commands.EndProfile(); throw new Exception("An unmatched WebGPU profile end was accepted."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("no active WebGPU profile")) { }
    Console.WriteLine("PASS: Nested GPU profile scopes survive submission/reset with balanced native debug groups.");
    if (!buffer.GetData<uint>(commands).SequenceEqual(new uint[] { 1, 2, 3, 4 })) throw new Exception("Stride buffer readback did not match the upload.");
    Console.WriteLine("PASS: Stride buffer copy, submission and staging readback.");
    using var dynamicBuffer = Stride.Graphics.Buffer.New(device, 16, BufferFlags.VertexBuffer, GraphicsResourceUsage.Dynamic);
    dynamicBuffer.SetData(commands, new uint[] { 9, 8, 7, 6 });
    if (!dynamicBuffer.GetData<uint>(commands).SequenceEqual(new uint[] { 9, 8, 7, 6 })) throw new Exception("Dynamic buffer WriteDiscard upload mismatch.");
    Console.WriteLine("PASS: Dynamic buffer mapping records an ordered upload.");
    TextureValidation.Run(device, commands);
    TextureDimensionValidation.Run(device, commands);
    TextureMapValidation.Run(device, commands);
    CopyRegionValidation.Run(device, commands);
    BufferClearValidation.Run(device, commands);
    TextureClearValidation.Run(device, commands);
    CompilerValidation.Run(device, commands);
    BindingCacheValidation.Run(device);
    QueryValidation.Run(device);
    DepthValidation.Run(device, commands);
    DepthCopyValidation.Run(device, commands);
    ShaderLayoutValidation.Run(device, commands);
    ComputeValidation.Run(device, commands);
    DrawValidation.Run(device, commands);
    LifetimeValidation.Run(device);
    SpriteBatchValidation.Run(device, commands);

    // An odd width forces the WebGPU 256-byte copy pitch to differ from the
    // tightly packed public Texture.GetData result.
    using var target = Texture.New2D(device, 67, 19, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
    commands.Clear(target, new Color4(1, 0, 0, 1));
    var pixels = target.GetData<uint>(commands);
    if (pixels.Length != 67 * 19 || pixels.Any(pixel => pixel != 0xff0000ff)) throw new Exception("Stride clear/readback pixels are incorrect.");
    Console.WriteLine("PASS: Stride render-target clear and exact pixel readback with padded rows.");

    var pattern = Enumerable.Range(0, 43 * 17).Select(i => unchecked((uint)(i * 2654435761L))).ToArray();
    using var uploaded = Texture.New2D(device, 43, 17, PixelFormat.R8G8B8A8_UNorm, pattern);
    using var copied = Texture.New2D(device, 43, 17, PixelFormat.R8G8B8A8_UNorm, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
    commands.Copy(uploaded, copied);
    if (!copied.GetData<uint>(commands).SequenceEqual(pattern)) throw new Exception("Stride texture copy did not match the uploaded pattern.");
    Console.WriteLine("PASS: Stride texture upload, texture-to-texture copy and complete pattern readback.");

    var context = new GraphicsContext(device, commandList: commands);
    using var firstDraw = Texture.New2D(device, 37, 23, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
    using var secondDraw = Texture.New2D(device, 37, 23, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget);
    using var white = Texture.New2D(device, 2, 2, PixelFormat.R8G8B8A8_UNorm, new uint[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue });
    commands.SetRenderTargetAndViewport(null, firstDraw);
    context.DrawTexture(white, new Color4(1, 0, 0, 1));
    commands.SetRenderTargetAndViewport(null, secondDraw);
    context.DrawTexture(white, new Color4(0, 1, 0, 1));
    // Both draws are recorded before submitting. Reusing SpriteEffect's
    // constant buffer must preserve the distinct colors in command order.
    var red = firstDraw.GetData<uint>(commands);
    var green = secondDraw.GetData<uint>(commands);
    if (red.Any(pixel => pixel != 0xff0000ff) || green.Any(pixel => pixel != 0xff00ff00))
        throw new Exception($"SpriteEffect rendered incorrect colors: red center {red[red.Length / 2]:x8}, green center {green[green.Length / 2]:x8}.");
    Console.WriteLine("PASS: Stride DrawTexture/SpriteEffect renders exact pixels; two draws preserve distinct constant-buffer updates.");
    commands.SetRenderTargetAndViewport(null, copied);
    context.DrawTexture(uploaded, device.SamplerStates.PointClamp);
    if (!copied.GetData<uint>(commands).SequenceEqual(pattern)) throw new Exception("SpriteEffect texture coordinates or point sampling changed the pattern.");
    Console.WriteLine("PASS: SpriteEffect preserves every texel and its orientation with point sampling.");

    using var depth = Texture.New2D(device, 32, 32, PixelFormat.D24_UNorm_S8_UInt, TextureFlags.DepthStencil);
    commands.Clear(depth, DepthStencilClearOptions.DepthBuffer | DepthStencilClearOptions.Stencil, 0.5f, 7);
    var compiled = commands.Close();
    device.ExecuteCommandList(compiled);
    try { device.ExecuteCommandList(compiled); throw new Exception("Duplicate WebGPU submission was accepted."); }
    catch (InvalidOperationException ex) when (ex.Message.Contains("already been submitted")) { }
    device.WaitForGpuIdle();
    commands.Reset();
    Console.WriteLine("PASS: Depth/stencil clear and copied command-list submission lifetime.");
    if (args.Contains("--window")) WindowValidation.Run(device);
    foreach (var stage in SpriteEffect.Bytecode.Stages)
    {
        Console.WriteLine($"SpriteEffect stage {stage.Stage}: {stage.Data.Length} SPIR-V bytes.");
        if (args.Length == 2 && args[0] == "--export-shaders")
        {
            Directory.CreateDirectory(args[1]);
            File.WriteAllBytes(Path.Combine(args[1], $"SpriteEffect.{stage.Stage}.spv"), stage.Data);
        }
    }
}
GraphicsAdapterFactory.Dispose();
