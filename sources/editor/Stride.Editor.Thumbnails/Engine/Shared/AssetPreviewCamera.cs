// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using Stride.Core.Mathematics;
using Stride.Rendering.Lights;

namespace Stride.Engine;

public readonly record struct AssetPreviewCameraState(Vector3 Target, BoundingSphere PreviousBounds,
    float Yaw, float Pitch, float Distance, float DefaultYaw, float DefaultPitch, float FramingPadding, float VerticalFieldOfView);

/// <summary>Input-neutral camera and lighting behavior shared by asset preview hosts.</summary>
public sealed class AssetPreviewCamera
{
    private readonly Entity entity;
    private readonly CameraComponent camera;
    private static readonly BoundingSphere InvalidBounds = new(Vector3.Zero, MathUtil.ZeroTolerance);
    private BoundingSphere previousBounds = InvalidBounds;
    private Vector3 target;
    private float yaw, pitch, distance;
    public float DefaultYaw { get; set; }
    public float DefaultPitch { get; set; }
    public float FramingPadding { get; set; } = 1f;
    public float Distance => distance;
    public float Yaw => yaw;
    public float Pitch => pitch;
    public AssetPreviewCameraState CaptureState() => new(target, previousBounds, yaw, pitch, distance,
        DefaultYaw, DefaultPitch, FramingPadding, camera.VerticalFieldOfView);
    public void RestoreState(AssetPreviewCameraState state)
    {
        static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        if (!Finite(state.Target) || !Finite(state.PreviousBounds.Center) || !float.IsFinite(state.PreviousBounds.Radius) || state.PreviousBounds.Radius < 0
            || !float.IsFinite(state.Yaw) || !float.IsFinite(state.Pitch) || !float.IsFinite(state.Distance) || state.Distance < 0
            || !float.IsFinite(state.DefaultYaw) || !float.IsFinite(state.DefaultPitch) || !float.IsFinite(state.FramingPadding) || state.FramingPadding <= 0
            || !float.IsFinite(state.VerticalFieldOfView) || state.VerticalFieldOfView <= 0 || state.VerticalFieldOfView >= 180)
            throw new ArgumentException("The preview camera state is invalid.", nameof(state));
        target = state.Target; previousBounds = state.PreviousBounds;
        yaw = state.Yaw; pitch = state.Pitch; distance = state.Distance;
        DefaultYaw = state.DefaultYaw; DefaultPitch = state.DefaultPitch; FramingPadding = state.FramingPadding;
        camera.VerticalFieldOfView = state.VerticalFieldOfView; UpdateComponents();
    }
    public void SetViewAngles(float viewYaw, float viewPitch)
    {
        if (!float.IsFinite(viewYaw) || !float.IsFinite(viewPitch)) throw new ArgumentException("Camera angles must be finite.");
        yaw = viewYaw; pitch = viewPitch; UpdateComponents();
    }
    public AssetPreviewCamera(Entity entity)
    {
        this.entity = entity;
        camera = entity.Get<CameraComponent>() ?? throw new ArgumentException("The preview entity needs a camera.", nameof(entity));
    }
    public void ResetViewAngle() { yaw = DefaultYaw; pitch = DefaultPitch; UpdateComponents(); }
    public void ResetViewTarget(BoundingSphere bounds) { target = Vector3.Zero; previousBounds = InvalidBounds; AdjustViewTarget(bounds); }
    public void AdjustViewTarget(BoundingSphere bounds)
    {
        if (bounds != previousBounds)
        {
            distance = Math.Max((bounds.Radius + bounds.Radius / MathF.Tan(MathUtil.DegreesToRadians(camera.VerticalFieldOfView / 2))) * FramingPadding, 2 * MathUtil.ZeroTolerance);
            target = bounds.Center;
        }
        previousBounds = bounds; UpdateComponents();
    }
    public void SetViewDistance(float value) { distance = Math.Max(0, value); UpdateComponents(); }
    public void Navigate(Vector2 translation, bool pan, bool orbit, float wheel)
    {
        if (pan)
        {
            var inverseView = entity.Transform.WorldMatrix;
            target -= distance * (translation.X * inverseView.Row1.XYZ() - translation.Y * inverseView.Row2.XYZ());
        }
        else if (orbit) { yaw -= 4 * translation.X; pitch -= 3 * translation.Y; }
        // Preserve the reference wheel curve while guarding unusually large negative deltas.
        distance *= 1 / Math.Max(0.01f, 1 + wheel / 8);
        UpdateComponents();
    }
    public void UpdateComponents()
    {
        distance = Math.Max(distance, 2 * MathUtil.ZeroTolerance);
        pitch = MathUtil.Clamp(pitch, -MathUtil.PiOverTwo + 0.01f, MathUtil.PiOverTwo - 0.01f);
        var cameraPosition = target + Vector3.Transform(new Vector3(0, 0, distance), Quaternion.RotationYawPitchRoll(yaw, pitch, 0));
        entity.Transform.UseTRS = false;
        entity.Transform.LocalMatrix = Matrix.Invert(Matrix.LookAtRH(cameraPosition, target, Vector3.UnitY));
        camera.FarClipPlane = 2.5f * Math.Max(distance, previousBounds.Radius);
        camera.NearClipPlane = camera.FarClipPlane / 1000f;
        entity.Transform.UpdateWorldMatrix();
    }
    public static void AttachLighting(Entity cameraEntity, bool hdr)
    {
        var factor = hdr ? 2f / 0.9f : 1f;
        cameraEntity.AddChild(new Entity("Preview Ambient Light1") { new LightComponent { Type = new LightAmbient(), Intensity = 0.02f * factor } });
        cameraEntity.AddChild(new Entity("Preview Directional Front") { new LightComponent { Intensity = 0.07f * factor } });
        var top = new Entity("Preview Directional Top") { new LightComponent { Intensity = 0.8f * factor } };
        top.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-80));
        cameraEntity.AddChild(top);
    }
}
