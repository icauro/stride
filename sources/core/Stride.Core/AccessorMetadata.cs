// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Reflection;

namespace Stride.Core;

/// <summary>
/// Metadata used for providing attached getter/setter to a <see cref="PropertyKey"/>.
/// </summary>
public class AccessorMetadata : PropertyKeyMetadata
{
    /// <summary>
    /// Setter delegate.
    /// </summary>
    /// <param name="propertyContainer">The property container holding the value.</param>
    /// <param name="value">The value to set</param>
    public delegate void SetterDelegate(ref PropertyContainer propertyContainer, object value);

    /// <summary>
    /// Getter delegate
    /// </summary>
    /// <param name="propertyContainer">The property container holding the value.</param>
    /// <returns>Returns the value</returns>
    public delegate object GetterDelegate(ref PropertyContainer propertyContainer);

    private readonly SetterDelegate setter;
    private readonly GetterDelegate getter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessorMetadata"/> class.
    /// </summary>
    /// <param name="getter">Getter delegate.</param>
    /// <param name="setter">Setter delegate.</param>
    public AccessorMetadata(GetterDelegate getter, SetterDelegate setter)
    {
        this.getter = getter;
        this.setter = setter;
    }

    internal IEnumerable<Type> RegistrationTypes()
    {
        foreach (var callback in new Delegate?[] { getter, setter })
        {
            if (callback == null) continue;
            foreach (var method in callback.GetInvocationList())
            {
                if (method.Method.DeclaringType != null) yield return method.Method.DeclaringType;
                foreach (var argument in method.Method.GetGenericArguments()) yield return argument;
                if (method.Target != null) yield return method.Target.GetType();
            }
        }
    }

    internal IEnumerable<Assembly> RegistrationAssemblies()
    {
        foreach (var callback in new Delegate?[] { getter, setter })
        {
            if (callback == null) continue;
            foreach (var method in callback.GetInvocationList())
            {
                yield return method.Method.Module.Assembly;
                if (method.Target != null) yield return method.Target.GetType().Assembly;
            }
        }
    }

    /// <summary>
    /// Gets the value from a <see cref="PropertyContainer"/> associated to this getter.
    /// </summary>
    /// <param name="obj">the property container.</param>
    /// <returns>The value stored.</returns>
    public object GetValue(ref PropertyContainer obj)
    {
        return getter(ref obj);
    }

    /// <summary>
    /// Sets the value for a <see cref="PropertyContainer"/> value.
    /// </summary>
    /// <param name="obj">The property container.</param>
    /// <param name="value">The value to set.</param>
    public void SetValue(ref PropertyContainer obj, object value)
    {
        setter(ref obj, value);
    }
}
