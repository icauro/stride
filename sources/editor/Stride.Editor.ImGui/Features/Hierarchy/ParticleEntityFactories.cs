using Stride.Animations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles;
using Stride.Particles.Components;
using Stride.Particles.Initializers;
using Stride.Particles.Modules;
using Stride.Particles.ShapeBuilders;
using Stride.Particles.Spawners;
using Stride.Particles.Updaters;

namespace Stride.Editor.ImGui;

internal enum EditorParticleEntityKind { Simple, Smoke, Ribbon }

internal static class ParticleEntityFactories
{
    internal static Entity Create(EditorParticleEntityKind kind)
    {
        var component = new ParticleSystemComponent();
        var emitter = new ParticleEmitter { ParticleLifetime = kind == EditorParticleEntityKind.Ribbon ? new Vector2(2, 2) : new Vector2(1, 2) };
        switch (kind)
        {
            case EditorParticleEntityKind.Simple:
                emitter.Spawners.Add(new SpawnerPerSecond());
                emitter.Initializers.Add(new InitialSizeSeed { RandomSize = new Vector2(0.05f, 0.1f) });
                emitter.Initializers.Add(new InitialPositionSeed());
                emitter.Initializers.Add(new InitialVelocitySeed { VelocityMin = new Vector3(-0.5f, 1, -0.5f), VelocityMax = new Vector3(0.5f, 3, 0.5f) });
                var gravity = new UpdaterForceField { EnergyConservation = 1, FieldShape = null,
                    ForceFixed = new Vector3(0, -9.81f, 0), ForceDirected = 0, ForceRepulsive = 0, ForceVortex = 0 };
                gravity.FieldFalloff.StrengthInside = gravity.FieldFalloff.StrengthOutside = 1;
                emitter.Updaters.Add(gravity);
                break;
            case EditorParticleEntityKind.Smoke:
                emitter.Spawners.Add(new SpawnerPerSecond { SpawnCount = 20 });
                emitter.Initializers.Add(new InitialSizeSeed { RandomSize = new Vector2(0.35f, 0.55f) });
                emitter.Initializers.Add(new InitialPositionSeed { PositionMin = new Vector3(-0.2f, 0, -0.2f), PositionMax = new Vector3(0.2f, 0, 0.2f) });
                emitter.Initializers.Add(new InitialVelocitySeed { VelocityMin = new Vector3(-0.5f, 1, -0.5f), VelocityMax = new Vector3(0.5f, 3, 0.5f) });
                break;
            case EditorParticleEntityKind.Ribbon:
                emitter.Spawners.Add(new SpawnerPerSecond { SpawnCount = 30 });
                emitter.ShapeBuilder = new ShapeBuilderRibbon { SmoothingPolicy = SmoothingPolicy.Best, Segments = 15,
                    TextureCoordinatePolicy = TextureCoordinatePolicy.Stretched, TexCoordsFactor = 1 };
                emitter.Initializers.Add(new InitialVelocitySeed { VelocityMin = new Vector3(-0.15f, 3, -0.15f), VelocityMax = new Vector3(0.15f, 3, 0.15f) });
                emitter.Initializers.Add(new InitialSpawnOrder());
                var curve = new ComputeAnimationCurveFloat();
                curve.KeyFrames.Add(new AnimationKeyFrame<float> { Key = 0, Value = 0.1f });
                curve.KeyFrames.Add(new AnimationKeyFrame<float> { Key = 0.9f, Value = 0 });
                emitter.Updaters.Add(new UpdaterSizeOverTime { SamplerMain = { Curve = curve } });
                emitter.SortingPolicy = EmitterSortingPolicy.ByOrder;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        component.ParticleSystem.Emitters.Add(emitter);
        var entity = new Entity(kind switch { EditorParticleEntityKind.Simple => "Simple particle system",
            EditorParticleEntityKind.Smoke => "Smoke particle system", _ => "Ribbon particle system" });
        entity.Add(component);
        return entity;
    }
}
