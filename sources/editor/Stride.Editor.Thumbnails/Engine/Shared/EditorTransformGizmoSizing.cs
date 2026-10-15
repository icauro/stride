// Copyright (c) .NET Foundation and Contributors
// Distributed under the MIT license. See LICENSE.md.
namespace Stride.Engine.Design;

/// <summary>The native editor's mapping from user zoom factor to transform-handle scale.</summary>
public static class EditorTransformGizmoSizing
{
    public static float ToSizeFactor(float value)
        => value > 1 ? 1 + (value - 1) * 0.1f : (float)System.Math.Pow(value, 0.333);
}
