using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

internal readonly partial struct NavigationSaveCodec : IComponentCodec<NavigationSavedTable>
{
    public void Write(ref DataWriter writer, in NavigationSavedTable value)
    {
        writer.WriteInt32(value.Resources.Length);
        foreach (NavigationSavedResource resource in value.Resources) WriteResource(ref writer, resource);
        writer.WriteInt32(value.Actors.Length);
        foreach (NavigationSavedActor actor in value.Actors)
        {
            writer.WriteEntity(actor.Entity); writer.WriteInt32(actor.Profile); writer.WriteInt32(actor.Policy);
            writer.WriteSingle(actor.DesiredSpeed); writer.WriteByte((byte)actor.Kind); writer.WriteInt32(actor.Resource);
            WriteGoal(ref writer, actor.Goal); writer.WriteBoolean(actor.Enabled);
            WriteTraversal(ref writer, actor.Traversal);
            WriteControls(ref writer, actor.Controls);
        }
        WriteWorld(ref writer, value.Sources, value.Regions, value.Crowd);
    }

    public void Read(ref DataReader reader, out NavigationSavedTable value)
    {
        var resources = new NavigationSavedResource[reader.ReadInt32()];
        for (int index = 0; index < resources.Length; index++) resources[index] = ReadResource(ref reader);
        var actors = new NavigationSavedActor[reader.ReadInt32()];
        for (int index = 0; index < actors.Length; index++)
            actors[index] = new(reader.ReadEntity(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle(),
                (SavedPathKind)reader.ReadByte(), reader.ReadInt32(), ReadGoal(ref reader), reader.ReadBoolean(), ReadTraversal(ref reader), ReadControls(ref reader));
        ReadWorld(ref reader, out NavigationSavedSource[] sources, out NavigationSavedRegion[] regions, out NavigationSavedCrowd[] crowd);
        value = new(resources, actors, sources, regions, crowd);
    }

    private static void WriteResource(ref DataWriter writer, NavigationSavedResource resource)
    {
        switch (resource)
        {
            case SavedProfile value: writer.WriteByte(1); writer.WriteGuid(value.Asset.Value); break;
            case SavedPolicy value: writer.WriteByte(2); writer.WriteGuid(value.Asset.Value); break;
            case SavedMesh value:
                writer.WriteByte(3); writer.WriteGuid(value.Asset.Value); writer.WriteGuid(value.Selection.Profile.Value); writer.WriteString(value.Selection.Variant); break;
            case SavedHeadingMesh value:
                writer.WriteByte(4); writer.WriteGuid(value.Asset.Value); writer.WriteGuid(value.Selection.Profile.Value); writer.WriteString(value.Selection.Variant); break;
            case SavedGrid value:
                writer.WriteByte(5); writer.WriteGuid(value.Asset.Value); writer.WriteGuid(value.Selection.Profile.Value); writer.WriteString(value.Selection.Variant); break;
            case SavedMotions value: writer.WriteByte(6); writer.WriteGuid(value.Asset.Value); break;
            case SavedGeometry value: writer.WriteByte(7); writer.WriteGuid(value.Asset.Value); break;
            case SavedLinks value:
                writer.WriteByte(8); writer.WriteGuid(value.Asset.Value); writer.WriteInt32(value.Definitions!.Length);
                foreach (NavigationLink link in value.Definitions) WriteLink(ref writer, link);
                break;
            case SavedCosts value:
                writer.WriteByte(9); writer.WriteInt32(value.Grid); writer.WriteInt32(value.Policy); writer.WriteUInt64(value.Version);
                writer.WriteInt32(value.Cells.Length);
                foreach (SavedCellCost cell in value.Cells) { WriteCell(ref writer, cell.Cell); writer.WriteSingle(cell.Cost); }
                writer.WriteInt32(value.Edges.Length);
                foreach (SavedEdgeCost edge in value.Edges)
                {
                    WriteCell(ref writer, edge.From); WriteCell(ref writer, edge.To); writer.WriteInt32(edge.Action);
                    writer.WriteUInt64(edge.Capabilities); writer.WriteSingle(edge.Cost);
                }
                break;
            default: WriteWorldResource(ref writer, resource); break;
            case SavedVehicleSpace value:
                writer.WriteByte(10); writer.WriteInt32(value.Grid); writer.WriteInt32(value.Motions); writer.WriteInt32(value.Costs);
                WriteHybrid(ref writer, value.Settings); break;
            case SavedFlow value:
                writer.WriteByte(11); writer.WriteInt32(value.Costs); writer.WriteInt32(value.Profile);
                writer.WriteUInt64(value.GoalVersion); writer.WriteUInt64(value.Version); writer.WriteString(value.Solver);
                writer.WriteBoolean(value.Source is not null);
                if (value.Source is { } source) { writer.WriteGuid(source.Asset.Value); writer.WriteBoolean(source.Heading); }
                writer.WriteInt32(value.HeadingGraph);
                writer.WriteInt32(value.Goals.Length);
                foreach (NavigationFlowGoal goal in value.Goals)
                {
                    WriteVector(ref writer, goal.Position); writer.WriteSingle(goal.ArrivalRange);
                    WriteNullable(ref writer, goal.Heading); writer.WriteSingle(goal.HeadingTolerance);
                }
                break;
        }
    }

    private static NavigationSavedResource ReadResource(ref DataReader reader)
    {
        byte kind = reader.ReadByte();
        switch (kind)
        {
            case 1: return new SavedProfile(new(reader.ReadGuid()));
            case 2: return new SavedPolicy(new(reader.ReadGuid()));
            case 3: return new SavedMesh(new(reader.ReadGuid()), new(new(reader.ReadGuid()), reader.ReadString()!));
            case 4: return new SavedHeadingMesh(new(reader.ReadGuid()), new(new(reader.ReadGuid()), reader.ReadString()!));
            case 5: return new SavedGrid(new(reader.ReadGuid()), new(new(reader.ReadGuid()), reader.ReadString()!));
            case 6: return new SavedMotions(new(reader.ReadGuid()));
            case 7: return new SavedGeometry(new(reader.ReadGuid()));
            case 8:
                var asset = new AssetId<NavigationAsset>(reader.ReadGuid());
                var links = new NavigationLink[reader.ReadInt32()];
                for (int index = 0; index < links.Length; index++) links[index] = ReadLink(ref reader);
                return new SavedLinks(asset, links);
            case 9:
                int grid = reader.ReadInt32(), policy = reader.ReadInt32();
                ulong version = reader.ReadUInt64();
                var cells = new SavedCellCost[reader.ReadInt32()];
                for (int index = 0; index < cells.Length; index++) cells[index] = new(ReadCell(ref reader), reader.ReadSingle());
                var edges = new SavedEdgeCost[reader.ReadInt32()];
                for (int index = 0; index < edges.Length; index++) edges[index] = new(ReadCell(ref reader), ReadCell(ref reader), reader.ReadInt32(), reader.ReadUInt64(), reader.ReadSingle());
                return new SavedCosts(grid, policy, version, cells, edges);
            case 10: return new SavedVehicleSpace(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), ReadHybrid(ref reader));
            case 11:
                int costs = reader.ReadInt32(), profile = reader.ReadInt32();
                ulong goalVersion = reader.ReadUInt64(), inputVersion = reader.ReadUInt64();
                string solver = reader.ReadString()!;
                SavedFlowAsset? source = reader.ReadBoolean() ? new(new(reader.ReadGuid()), reader.ReadBoolean()) : null;
                int graph = reader.ReadInt32();
                var goals = new NavigationFlowGoal[reader.ReadInt32()];
                for (int index = 0; index < goals.Length; index++) goals[index] = new(ReadVector(ref reader), reader.ReadSingle(), ReadNullable(ref reader), reader.ReadSingle());
                return new SavedFlow(costs, profile, goals, goalVersion, inputVersion, solver, source, graph);
            default: return ReadWorldResource(kind, ref reader);
        }
    }

    private static void WriteGoal(ref DataWriter writer, NavigationGoal goal)
    {
        WriteVector(ref writer, goal.Position); writer.WriteEntity(goal.Target); writer.WriteBoolean(goal.IsEntityTarget);
        writer.WriteSingle(goal.ArrivalRange); WriteNullable(ref writer, goal.Heading); writer.WriteSingle(goal.HeadingTolerance);
    }
    private static NavigationGoal ReadGoal(ref DataReader reader)
    {
        Vector3 position = ReadVector(ref reader);
        var target = reader.ReadEntity(); bool entity = reader.ReadBoolean();
        return new(position, reader.ReadSingle(), ReadNullable(ref reader), reader.ReadSingle()) { Target = target, IsEntityTarget = entity };
    }
    private static void WriteHybrid(ref DataWriter writer, HybridAStarSettings settings)
    {
        writer.WriteInt32(settings.HeadingBins); writer.WriteInt32(settings.CurvatureSteps);
        WriteNullable(ref writer, settings.SpatialResolution); WriteNullable(ref writer, settings.StepLength); WriteNullable(ref writer, settings.AnalyticExpansionDistance);
        writer.WriteSingle(settings.TurningPenalty); writer.WriteSingle(settings.ReversePenalty); writer.WriteSingle(settings.DirectionChangeCost);
    }
    private static HybridAStarSettings ReadHybrid(ref DataReader reader) => new()
    {
        HeadingBins = reader.ReadInt32(), CurvatureSteps = reader.ReadInt32(), SpatialResolution = ReadNullable(ref reader),
        StepLength = ReadNullable(ref reader), AnalyticExpansionDistance = ReadNullable(ref reader),
        TurningPenalty = reader.ReadSingle(), ReversePenalty = reader.ReadSingle(), DirectionChangeCost = reader.ReadSingle(),
    };
    private static void WriteCell(ref DataWriter writer, SavedCell cell) { WriteVector(ref writer, cell.Position); writer.WriteInt32(cell.Layer); }
    private static SavedCell ReadCell(ref DataReader reader) => new(ReadVector(ref reader), reader.ReadInt32());
    private static void WriteVector(ref DataWriter writer, Vector3 value) { writer.WriteSingle(value.X); writer.WriteSingle(value.Y); writer.WriteSingle(value.Z); }
    private static Vector3 ReadVector(ref DataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void WriteNullable(ref DataWriter writer, float? value) { writer.WriteBoolean(value.HasValue); if (value.HasValue) writer.WriteSingle(value.Value); }
    private static float? ReadNullable(ref DataReader reader) => reader.ReadBoolean() ? reader.ReadSingle() : null;
}
