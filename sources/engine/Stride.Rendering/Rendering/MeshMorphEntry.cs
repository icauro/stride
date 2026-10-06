// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using Stride.Core;
using Stride.Core.Mathematics;

namespace Stride.Rendering;

/// <summary>A byte-identical CPU/GPU sparse contribution: vertex32, shape16, nine binary16 components.</summary>
[DataContract]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshMorphEntry
{
    public const int SizeInBytes = 24;

    public uint VertexIndex;
    public uint ShapeAndPositionX;
    public uint PositionYZ;
    public uint NormalXY;
    public uint NormalZTangentX;
    public uint TangentYZ;

    [DataMemberIgnore] public readonly ushort ShapeIndex => (ushort)ShapeAndPositionX;
    [DataMemberIgnore] public readonly Vector3 PositionDelta => new(Unpack(ShapeAndPositionX >> 16), Unpack(PositionYZ), Unpack(PositionYZ >> 16));
    [DataMemberIgnore] public readonly Vector3 NormalDelta => new(Unpack(NormalXY), Unpack(NormalXY >> 16), Unpack(NormalZTangentX));
    [DataMemberIgnore] public readonly Vector3 TangentDelta => new(Unpack(NormalZTangentX >> 16), Unpack(TangentYZ), Unpack(TangentYZ >> 16));
    [DataMemberIgnore] public readonly bool IsZero => PositionDelta == Vector3.Zero && NormalDelta == Vector3.Zero && TangentDelta == Vector3.Zero;

    public static MeshMorphEntry Create(uint vertex, ushort shape, Vector3 position, Vector3 normal, Vector3 tangent)
    {
        return new MeshMorphEntry
        {
            VertexIndex = vertex,
            ShapeAndPositionX = shape | (uint)Pack(position.X) << 16,
            PositionYZ = Pair(position.Y, position.Z),
            NormalXY = Pair(normal.X, normal.Y),
            NormalZTangentX = Pair(normal.Z, tangent.X),
            TangentYZ = Pair(tangent.Y, tangent.Z),
        };
    }

    private static uint Pair(float low, float high) => Pack(low) | (uint)Pack(high) << 16;

    private static ushort Pack(float value)
    {
        if (!float.IsFinite(value) || Math.Abs(value) > 65504f)
            throw new ArgumentException("Morph delta components must be finite and within binary16 range.");
        return BitConverter.HalfToUInt16Bits((System.Half)value);
    }

    private static float Unpack(uint value) => (float)BitConverter.UInt16BitsToHalf((ushort)value);
}
