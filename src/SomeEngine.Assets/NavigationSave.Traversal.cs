using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

public static partial class NavigationSave
{
    private static NavigationTraversalIntent CaptureTraversal(World world, NavigationSavedActor actor) => actor.Kind switch
    {
        SavedPathKind.Flow => NavigationComponents.CaptureFlowTraversal(world, actor.Entity),
        SavedPathKind.Polygon => NavigationComponents.CapturePathTraversal<NavigationMesh, PolygonSearchState>(world, actor.Entity),
        SavedPathKind.Polyanya => NavigationComponents.CapturePathTraversal<NavigationMesh, PolyanyaSearchState>(world, actor.Entity),
        SavedPathKind.Asa => NavigationComponents.CapturePathTraversal<HeadingNavigationMesh, AsaSearchState>(world, actor.Entity),
        SavedPathKind.StateLattice => NavigationComponents.CapturePathTraversal<NavigationVehicleSpace, StateLatticeSearchState>(world, actor.Entity),
        SavedPathKind.HybridAStar => NavigationComponents.CapturePathTraversal<NavigationVehicleSpace, HybridAStarSearchState>(world, actor.Entity),
        SavedPathKind.Scene => NavigationComponents.CapturePathTraversal<NavigationScene, SceneSearchState>(world, actor.Entity),
        SavedPathKind.DStarLite => NavigationComponents.CapturePathTraversal<NavigationScene, DStarLiteSearchState>(world, actor.Entity),
        SavedPathKind.HeadingScene => NavigationComponents.CapturePathTraversal<HeadingNavigationScene, HeadingSceneSearchState>(world, actor.Entity),
        _ => default,
    };
}

internal readonly partial struct NavigationSaveCodec
{
    private static void WriteTraversal(ref DataWriter writer, NavigationTraversalIntent intent)
    {
        NavigationTraversalRequest value = intent.Request;
        writer.WriteUInt64(intent.Sequence); writer.WriteUInt64(value.RequestId); writer.WriteInt32(value.Connection); writer.WriteInt32(value.Action);
        WriteVector(ref writer, value.Entry); WriteVector(ref writer, value.Exit);
        writer.WriteSingle(value.Parameters.X); writer.WriteSingle(value.Parameters.Y); writer.WriteSingle(value.Parameters.Z); writer.WriteSingle(value.Parameters.W);
        writer.WriteByte((byte)value.Status); writer.WriteUInt64(value.PathVersion); writer.WriteUInt64(value.ConnectionVersion);
        writer.WriteInt32(value.ExitCorner); writer.WriteSingle(value.ExitRange);
    }
    private static NavigationTraversalIntent ReadTraversal(ref DataReader reader)
    {
        ulong sequence = reader.ReadUInt64();
        var request = new NavigationTraversalRequest(reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), ReadVector(ref reader), ReadVector(ref reader),
            new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), (NavigationTraversalStatus)reader.ReadByte(),
            reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadSingle());
        return new(request, sequence);
    }
}
