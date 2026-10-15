namespace Stride.Editor.ImGui;

internal sealed class SpriteCommonSetting(SpriteSettingField[] fields)
{
    private long version = -1;
    public SpriteSettingField[] Fields { get; } = fields;
    public SpriteSettingField First => Fields[0];
    public object?[] Values { get; } = new object?[fields.Length];
    private readonly object?[][] coordinates = Enumerable.Range(0, 4).Select(_ => new object?[fields.Length]).ToArray();
    public int EnumIndex { get; private set; } = -1;
    public object? Coordinate(int axis, int index) => coordinates[axis][index];
    public void Refresh(long revision)
    {
        if (version == revision) return;
        for (var index = 0; index < Fields.Length; index++)
        {
            Values[index] = Fields[index].Node.Retrieve();
            for (var axis = 0; axis < 4; axis++) coordinates[axis][index] = AxisValue(Values[index], axis);
        }
        if (First.Type.IsEnum) EnumIndex = Array.IndexOf(First.EnumNames, Values[0]?.ToString());
        version = revision;
    }
    public bool IsMixed { get { for (var index = 1; index < Values.Length; index++) if (!Equals(Values[0], Values[index])) return true; return false; } }
    private static object? AxisValue(object? value, int axis) => value switch
    {
        Stride.Core.Mathematics.Vector2 vector => axis == 0 ? vector.X : vector.Y,
        Stride.Core.Mathematics.Vector4 vector => axis switch { 0 => vector.X, 1 => vector.Y, 2 => vector.Z, _ => vector.W },
        Stride.Core.Mathematics.Rectangle region => axis switch { 0 => region.X, 1 => region.Y, 2 => region.Width, _ => region.Height },
        Stride.Core.IO.UFile source => source.ToString(), _ => value,
    };
}
