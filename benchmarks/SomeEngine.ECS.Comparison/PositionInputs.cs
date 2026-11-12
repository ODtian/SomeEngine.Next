namespace SomeEngine.ECS.Comparison;

internal sealed class PositionInputs
{
    private static readonly Dictionary<int, PositionInputs> Inputs = new();
    private static readonly Dictionary<int, Position[]> AppendInputs = new();
    internal readonly Position[] Plain, Team, Sleeping;

    private PositionInputs(int count)
    {
        Plain = new Position[count / 2];
        Team = new Position[count / 4];
        Sleeping = new Position[count / 4];
        for (int i = 0; i < Plain.Length; i++) Plain[i].X = i * 2 + 1;
        for (int i = 0; i < Team.Length; i++) { Team[i].X = i * 4 + 2; Sleeping[i].X = i * 4; }
    }

    internal static PositionInputs Get(int count)
    {
        if (Inputs.TryGetValue(count, out var values)) return values;
        values = new(count);
        Inputs.Add(count, values);
        return values;
    }

    internal static Position[] Append(int count)
    {
        if (AppendInputs.TryGetValue(count, out var values)) return values;
        values = new Position[count];
        for (int i = 0; i < count; i++) values[i].X = count + i;
        AppendInputs.Add(count, values);
        return values;
    }
}
