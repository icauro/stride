using System;
using Stride.Core.Mathematics;
using Stride.Rendering.Lights;

namespace Stride.Engine;

/// <summary>Camera, lighting and bounds setup shared with WPF's entity thumbnail commands.</summary>
public static class EntityThumbnailScene
{
    public static Entity CreateCamera(CameraComponent camera, float aspectRatio)
    {
        camera.UseCustomAspectRatio = true; camera.AspectRatio = aspectRatio;
        var entity = new Entity("Thumbnail Camera") { camera };
        var cameraToFront = new Vector2(1f / MathF.Tan(MathUtil.DegreesToRadians(camera.VerticalFieldOfView / 2 * camera.AspectRatio)),
            1f / MathF.Tan(MathUtil.DegreesToRadians(camera.VerticalFieldOfView / 2)));
        var distance = 1f + Math.Max(cameraToFront.X, cameraToFront.Y);
        entity.Transform.Position = new Vector3(0, 0, distance);
        var rotation = Quaternion.RotationX(-MathUtil.Pi / 6) * Quaternion.RotationY(-MathUtil.Pi / 4);
        rotation.Rotate(ref entity.Transform.Position); entity.Transform.Rotation = rotation;
        camera.NearClipPlane = distance / 50f; camera.FarClipPlane = distance * 50f;
        return entity;
    }
    public static void SetupLighting(Scene scene, bool hdr)
    {
        var factor = hdr ? 5.0f : 1.0f;
        var ambient = new Entity("Thumbnail Ambient Light1") { new LightComponent { Type = new LightAmbient(), Intensity = 0.02f } };
        var front = new Entity("Thumbnail Directional Front") { new LightComponent { Intensity = 0.07f * factor } };
        var top = new Entity("Thumbnail Directional Top") { new LightComponent { Intensity = 0.8f * factor } };
        top.Transform.Rotation = Quaternion.RotationX(MathUtil.DegreesToRadians(-80));
        scene.Entities.Add(ambient); scene.Entities.Add(front); scene.Entities.Add(top);
    }
    public static void NormalizeEntity(Entity entity, BoundingSphere bounds)
    {
        entity.Transform.Scale = bounds.Radius > MathUtil.ZeroTolerance ? new Vector3(1f / bounds.Radius) : Vector3.One;
        entity.Transform.Position = -entity.Transform.Scale * bounds.Center;
    }
}
