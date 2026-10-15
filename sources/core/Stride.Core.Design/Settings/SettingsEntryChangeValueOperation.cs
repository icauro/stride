// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.
using Stride.Core.Transactions;
using Stride.Core.IO;

namespace Stride.Core.Settings;

/// <summary>
/// An internal object allowing to rollback/rollforward changes in a <see cref="SettingsEntry"/>.
/// </summary>
internal sealed class SettingsEntryChangeValueOperation : Operation
{
    private readonly SettingsProfile profile;
    private readonly UFile name;
    private SettingsEntrySnapshot other;

    public SettingsEntryChangeValueOperation(SettingsEntry entry, object oldValue)
    {
        profile = entry.OwningProfile;
        name = entry.Name;
        // Construction occurs before entry.UpdateValue installs the new value.
        other = profile.CaptureAuthoredEntry(name);
    }

    protected override void Rollback()
    {
        var current = profile.CaptureAuthoredEntry(name);
        profile.RestoreAuthoredEntry(name, other);
        other = current;
    }

    protected override void Rollforward() => Rollback();
}
