using System.Numerics;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

internal readonly partial struct NavigationSaveCodec
{
    private static void WriteWorld(ref DataWriter writer, NavigationSavedSource[] sources, NavigationSavedRegion[] regions, NavigationSavedCrowd[] crowd)
    {
        writer.WriteInt32(sources.Length);
        foreach (NavigationSavedSource source in sources)
        {
            writer.WriteEntity(source.Entity); writer.WriteInt32(source.Geometry); writer.WriteBoolean(source.Obstacle);
            writer.WriteByte((byte)source.Participation); writer.WriteUInt64(source.Version); writer.WriteBoolean(source.Enabled);
        }
        writer.WriteInt32(regions.Length);
        foreach (NavigationSavedRegion region in regions)
        {
            writer.WriteInt32(region.Scene); writer.WriteInt32(region.Region); WriteVector(ref writer, region.Bounds.Minimum); WriteVector(ref writer, region.Bounds.Maximum);
            writer.WriteInt32(region.Profile); writer.WriteInt32(region.Policy);
            writer.WriteSingle(region.Surface.CellSize); writer.WriteSingle(region.Surface.CellHeight); writer.WriteInt32(region.Surface.TileSize); writer.WriteSingle(region.Surface.BorderSize);
            writer.WriteSingle(region.Mesh.MaximumContourError); writer.WriteSingle(region.Mesh.MaximumEdgeLength); writer.WriteInt32(region.Mesh.MaximumPolygonVertices);
            writer.WriteInt32(region.Mesh.MinimumRegionCells); writer.WriteInt32(region.Mesh.MergeRegionCells); writer.WriteSingle(region.Mesh.DetailSampleDistance);
            writer.WriteSingle(region.Mesh.DetailMaximumError); writer.WriteUInt64(region.Mesh.ContentVersion);
            writer.WriteInt32(region.Ports.Length);
            foreach (NavigationRegionPort port in region.Ports)
            { writer.WriteInt32(port.Id); writer.WriteInt32(port.Tile.X); writer.WriteInt32(port.Tile.Z); WriteVector(ref writer, port.Position); }
            writer.WriteString(region.TransformReader); writer.WriteBoolean(region.BuildGrid); writer.WriteByte((byte)region.FootprintMode);
            writer.WriteInt32(region.Connections.Length);
            foreach (NavigationGridConnection connection in region.Connections)
            {
                WriteVector(ref writer, connection.From); WriteVector(ref writer, connection.To); writer.WriteSingle(connection.Cost);
                writer.WriteUInt64(connection.RequiredCapabilities); writer.WriteInt32(connection.Action); writer.WriteBoolean(connection.Bidirectional);
            }
        }
        writer.WriteInt32(crowd.Length);
        foreach (NavigationSavedCrowd actor in crowd)
        { writer.WriteEntity(actor.Entity); writer.WriteSingle(actor.Weight); writer.WriteBoolean(actor.Enabled); }
    }

    private static void ReadWorld(ref DataReader reader, out NavigationSavedSource[] sources, out NavigationSavedRegion[] regions, out NavigationSavedCrowd[] crowd)
    {
        sources = new NavigationSavedSource[reader.ReadInt32()];
        for (int index = 0; index < sources.Length; index++) sources[index] = new(reader.ReadEntity(), reader.ReadInt32(), reader.ReadBoolean(),
            (NavigationObstacleParticipation)reader.ReadByte(), reader.ReadUInt64(), reader.ReadBoolean());
        regions = new NavigationSavedRegion[reader.ReadInt32()];
        for (int index = 0; index < regions.Length; index++)
        {
            int scene = reader.ReadInt32(), region = reader.ReadInt32();
            var bounds = new NavigationBounds(ReadVector(ref reader), ReadVector(ref reader));
            int profile = reader.ReadInt32(), policy = reader.ReadInt32();
            var surface = new NavigationSurfaceSettings { CellSize = reader.ReadSingle(), CellHeight = reader.ReadSingle(), TileSize = reader.ReadInt32(), BorderSize = reader.ReadSingle() };
            var mesh = new NavigationMeshSettings
            {
                MaximumContourError = reader.ReadSingle(), MaximumEdgeLength = reader.ReadSingle(), MaximumPolygonVertices = reader.ReadInt32(),
                MinimumRegionCells = reader.ReadInt32(), MergeRegionCells = reader.ReadInt32(), DetailSampleDistance = reader.ReadSingle(),
                DetailMaximumError = reader.ReadSingle(), ContentVersion = reader.ReadUInt64(),
            };
            var ports = new NavigationRegionPort[reader.ReadInt32()];
            for (int port = 0; port < ports.Length; port++) ports[port] = new(reader.ReadInt32(), new(reader.ReadInt32(), reader.ReadInt32()), ReadVector(ref reader));
            string transform = reader.ReadString()!; bool grid = reader.ReadBoolean(); var mode = (NavigationGridFootprintMode)reader.ReadByte();
            var connections = new NavigationGridConnection[reader.ReadInt32()];
            for (int connection = 0; connection < connections.Length; connection++) connections[connection] = new(ReadVector(ref reader), ReadVector(ref reader),
                reader.ReadSingle(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadBoolean());
            regions[index] = new(scene, region, bounds, profile, policy, surface, mesh, ports, transform, grid, connections, mode);
        }
        crowd = new NavigationSavedCrowd[reader.ReadInt32()];
        for (int index = 0; index < crowd.Length; index++) crowd[index] = new(reader.ReadEntity(), reader.ReadSingle(), reader.ReadBoolean());
    }

    private static void WriteWorldResource(ref DataWriter writer, NavigationSavedResource resource)
    {
        switch (resource)
        {
            case SavedScene value:
                writer.WriteByte(12); writer.WriteInt32(value.Profile); writer.WriteInt32(value.Policy); writer.WriteInt32(value.Links);
                writer.WriteInt32(value.Placements.Length);
                foreach (SavedPlacement placement in value.Placements)
                { writer.WriteInt32(placement.Id); writer.WriteInt32(placement.Mesh); WriteMatrix(ref writer, placement.Transform); }
                writer.WriteInt32(value.Connections.Length);
                foreach (NavigationSceneConnection connection in value.Connections)
                {
                    writer.WriteInt32(connection.Id); writer.WriteInt32(connection.FromRegion); writer.WriteInt32(connection.FromPort);
                    writer.WriteInt32(connection.ToRegion); writer.WriteInt32(connection.ToPort); writer.WriteSingle(connection.Cost);
                    writer.WriteUInt64(connection.RequiredCapabilities); writer.WriteInt32(connection.Action); writer.WriteBoolean(connection.Bidirectional);
                    writer.WriteUInt64(connection.ContentVersion);
                }
                break;
            case SavedHeadingScene value: writer.WriteByte(13); writer.WriteInt32(value.Mesh); break;
            case SavedBuiltGrid value: writer.WriteByte(14); writer.WriteInt32(value.Region); break;
            default: WriteHeadingResource(ref writer, resource); break;
        }
    }

    private static NavigationSavedResource ReadWorldResource(byte kind, ref DataReader reader)
    {
        switch (kind)
        {
            case 12:
                int profile = reader.ReadInt32(), policy = reader.ReadInt32(), links = reader.ReadInt32();
                var placements = new SavedPlacement[reader.ReadInt32()];
                for (int index = 0; index < placements.Length; index++) placements[index] = new(reader.ReadInt32(), reader.ReadInt32(), ReadMatrix(ref reader));
                var connections = new NavigationSceneConnection[reader.ReadInt32()];
                for (int index = 0; index < connections.Length; index++) connections[index] = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadSingle(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadUInt64());
                return new SavedScene(profile, policy, placements, connections, links);
            case 13: return new SavedHeadingScene(reader.ReadInt32());
            case 14: return new SavedBuiltGrid(reader.ReadInt32());
            default: return ReadHeadingResource(kind, ref reader);
        }
    }

    private static void WriteLink(ref DataWriter writer, NavigationLink value)
    {
        writer.WriteInt32(value.Id); writer.WriteInt32(value.From.Region); writer.WriteInt32(value.From.Port);
        writer.WriteInt32(value.To.Region); writer.WriteInt32(value.To.Port); writer.WriteInt32(value.Action);
        writer.WriteSingle(value.Cost); writer.WriteUInt64(value.RequiredCapabilities); writer.WriteBoolean(value.Bidirectional);
        writer.WriteSingle(value.Parameters.X); writer.WriteSingle(value.Parameters.Y); writer.WriteSingle(value.Parameters.Z); writer.WriteSingle(value.Parameters.W);
        writer.WriteSingle(value.EntryRange); writer.WriteSingle(value.ExitRange); writer.WriteBoolean(value.Enabled); writer.WriteInt32(value.Capacity);
    }
    private static NavigationLink ReadLink(ref DataReader reader) => new(reader.ReadInt32(), new(reader.ReadInt32(), reader.ReadInt32()),
        new(reader.ReadInt32(), reader.ReadInt32()), reader.ReadInt32(), reader.ReadSingle(), reader.ReadUInt64(), reader.ReadBoolean(),
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean(), reader.ReadInt32());
    private static void WriteMatrix(ref DataWriter writer, Matrix4x4 value)
    {
        writer.WriteSingle(value.M11); writer.WriteSingle(value.M12); writer.WriteSingle(value.M13); writer.WriteSingle(value.M14);
        writer.WriteSingle(value.M21); writer.WriteSingle(value.M22); writer.WriteSingle(value.M23); writer.WriteSingle(value.M24);
        writer.WriteSingle(value.M31); writer.WriteSingle(value.M32); writer.WriteSingle(value.M33); writer.WriteSingle(value.M34);
        writer.WriteSingle(value.M41); writer.WriteSingle(value.M42); writer.WriteSingle(value.M43); writer.WriteSingle(value.M44);
    }
    private static Matrix4x4 ReadMatrix(ref DataReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
