// Copyright (c) .NET Foundation and Contributors.
// Distributed under the MIT license. See LICENSE.md in the project root.
using System;
using Xunit;

namespace Stride.Graphics.Tests;

public class TestCompressedTextureTransfers : GraphicTestGameBase
{
    [SkippableFact]
    public void ReadbackRejectsUndersizedDestination()
    {
        PerformTest(game =>
        {
            using var texture = Texture.New2D(game.GraphicsDevice, 4, 4, PixelFormat.R8G8B8A8_UNorm);
            Assert.Throws<ArgumentException>(() => texture.GetData(game.GraphicsContext.CommandList, new byte[63]));
        });
    }

    [SkippableTheory]
    [InlineData(PixelFormat.BC1_UNorm)]
    [InlineData(PixelFormat.BC3_UNorm)]
    [InlineData(PixelFormat.BC5_UNorm)]
    [InlineData(PixelFormat.BC7_UNorm)]
    public void UploadAndReadEveryMipPreservesEncodedBlocks(PixelFormat format)
    {
        PerformTest(game =>
        {
            Skip.If((game.GraphicsDevice.Features[format].FormatSupport & FormatSupport.ShaderSample) == 0, "Device does not support this compressed format.");
            var commandList = game.GraphicsContext.CommandList;
            foreach (var usage in new[] { GraphicsResourceUsage.Default, GraphicsResourceUsage.Dynamic })
            {
                // Use one subresource for dynamic writes. Default textures exercise
                // the full chain, including physical blocks of tiny mips.
                using var texture = Texture.New2D(game.GraphicsDevice, 8, 8,
                    new MipMapCount(usage == GraphicsResourceUsage.Default), format, usage: usage);
                for (var mip = 0; mip < texture.MipLevelCount; mip++)
                {
                    var size = Math.Max(1, 8 >> mip);
                    var blocks = (size + 3) / 4;
                    Assert.Equal(blocks * format.BlockSize, texture.CalculateWidth<byte>(mip));
                    var bytes = new byte[blocks * blocks * format.BlockSize];
                    for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 17 + mip * 37);
                    texture.SetData(commandList, bytes, mipLevel: mip);
                    Assert.Equal(bytes, texture.GetData<byte>(commandList, mipLevel: mip));
                }
            }
        });
    }
}
