// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using Stride.Core.Yaml;
using Stride.Rendering.Lights;
using Xunit;

namespace Stride.Assets.Tests;

public class TestLightShadowSerialization
{
    [Fact]
    public void LegacyDirectionalShadowWithoutSizeKeepsItsDefault()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Shadow: {}\n"));
        var restored = (LightDirectional)AssetYamlSerializer.Default.Deserialize(stream, typeof(LightDirectional));
        Assert.Equal(LightShadowMapSize.Large, restored.Shadow.Size);
    }
    [Theory]
    [InlineData(LightShadowMapSize.XSmall)]
    [InlineData(LightShadowMapSize.Small)]
    [InlineData(LightShadowMapSize.Medium)]
    [InlineData(LightShadowMapSize.Large)]
    [InlineData(LightShadowMapSize.XLarge)]
    public void DirectionalLightRetainsShadowSize(LightShadowMapSize size)
    {
        var light = new LightDirectional();
        light.Shadow.Size = size;
        using var stream = new MemoryStream();
        AssetYamlSerializer.Default.Serialize(stream, light);
        stream.Position = 0;
        var restored = (LightDirectional)AssetYamlSerializer.Default.Deserialize(stream, typeof(LightDirectional));
        Assert.Equal(size, restored.Shadow.Size);
    }
}
