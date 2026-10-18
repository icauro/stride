// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

internal static class TextureDimensionValidation
{
    internal static void Run(GraphicsDevice device, CommandList commands)
    {
        using var line = Texture.New1D(device, 8, MipMapCount.Auto, PixelFormat.R32G32B32A32_Float, TextureFlags.ShaderResource | TextureFlags.RenderTarget);
        for (var mip = 0; mip < line.MipLevelCount; mip++)
        {
            using var view = line.ToTextureView(ViewType.Single, 0, mip);
            var value = (1 << mip) / 8f;
            commands.Clear(view, new Color4(value, value, value, 1));
        }
        for (var mip = 0; mip < line.MipLevelCount; mip++)
        {
            var expected = new Vector4((1 << mip) / 8f, (1 << mip) / 8f, (1 << mip) / 8f, 1);
            if (line.GetData<Vector4>(commands, 0, mip).Any(p => (p - expected).Length() > .00001f))
                throw new Exception($"1D mip {mip} clear changed pixels or another mip.");
        }
        using var other = Texture.New2D(device, 1, 1, PixelFormat.R32G32B32A32_Float, new[] { Vector4.One });
        using var target = Texture.New1D(device, 5, PixelFormat.R32G32B32A32_Float, TextureFlags.RenderTarget);
        using var effect = new Effect(device, CompilerValidation.Compile("WebGpuTexture1D"));
        using var instance = new EffectInstance(effect);
        instance.Parameters.Set(SpriteBaseKeys.MatrixTransform, Matrix.Identity);
        instance.Parameters.Set(ParameterKeys.NewObject<Texture>(name: "WebGpuTexture1D.Input"), line);
        instance.Parameters.Set(ParameterKeys.NewObject<Texture>(name: "WebGpuTexture1D.Other"), other);
        instance.Parameters.Set(ParameterKeys.NewObject<SamplerState>(name: "WebGpuTexture1D.PointSampler"), device.SamplerStates.PointClamp);
        commands.SetRenderTargetAndViewport(null, target);
        new GraphicsContext(device, commandList: commands).DrawQuad(instance);
        if (target.GetData<Vector4>(commands).Any(p => (p - new Vector4(.25f, .5f, 1, 1)).Length() > .00001f))
            throw new Exception("1D sampling/load/mip width or rendering into a 1D target changed values.");

        using var volume = Texture.New3D(device, 8, 4, 4, MipMapCount.Auto, PixelFormat.R8_UNorm, TextureFlags.RenderTarget);
        for (var mip = 0; mip < volume.MipLevelCount; mip++)
        {
            using var view = volume.ToTextureView(ViewType.Single, 0, mip);
            commands.Clear(view, new Color4((mip + 1) / 255f, 0, 0, 1));
        }
        for (var mip = 0; mip < volume.MipLevelCount; mip++)
            if (volume.GetData<byte>(commands, 0, mip).Any(p => p != mip + 1))
                throw new Exception($"3D clear missed a depth slice or changed another mip: {mip}.");
        Console.WriteLine("PASS: 1D mip isolation, shader sampling/load/width queries and render target; complete 3D mip/depth-slice clears.");
    }
}
