// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering.ComputeEffect.LambertianPrefiltering;
using Stride.Rendering.Images.SphericalHarmonics;
using Stride.Rendering.Skyboxes;

namespace Stride.Rendering.LightProbes;

/// <summary>Captures one real light probe per step using the native cubemap and spherical-harmonics pipeline.</summary>
public sealed class LightProbeCaptureSession : IDisposable
{
    private readonly ISceneRendererContext context;
    private readonly IReadOnlyList<LightProbeComponent> probes;
    private readonly Texture cube;
    private readonly LambertianPrefilteringSHNoCompute filtering;
    private readonly Dictionary<LightProbeComponent, List<Color3>> results = new();
    private int next;
    private bool disposed;
    public IReadOnlyDictionary<LightProbeComponent, List<Color3>> Results => results;
    public bool Complete => next == probes.Count;
    public LightProbeCaptureSession(ISceneRendererContext context, IReadOnlyList<LightProbeComponent> probes)
    {
        this.context = context; this.probes = probes;
        cube = Texture.NewCube(context.GraphicsDevice, 256, PixelFormat.R16G16B16A16_Float);
        try
        {
            filtering = new LambertianPrefilteringSHNoCompute(RenderContext.GetShared(context.Services))
            { HarmonicOrder = LightProbeGenerator.LambertHamonicOrder, RadianceMap = cube };
        }
        catch { cube.Dispose(); throw; }
    }
    public bool Step()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Complete) return true;
        var probe = probes[next];
        using var renderer = new CubemapSceneRenderer(context, 256);
        using (renderer.DrawContext.PushRenderTargetsAndRestore())
        {
            context.GraphicsContext.ResourceGroupAllocator.Reset(context.GraphicsContext.CommandList);
            renderer.Draw(probe.Entity.Transform.WorldMatrix.TranslationVector, cube);
            filtering.Draw(renderer.DrawContext);
            var source = filtering.PrefilteredLambertianSH.Coefficients;
            var values = new List<Color3>(source.Length);
            for (var index = 0; index < source.Length; index++) values.Add(source[index] * SphericalHarmonics.BaseCoefficients[index]);
            results.Add(probe, values);
        }
        next++;
        return Complete;
    }
    public void Dispose()
    {
        if (disposed) return;
        if (!filteringReleased) { filtering.Dispose(); filteringReleased = true; }
        cube.Dispose(); disposed = true;
    }
    private bool filteringReleased;
}
