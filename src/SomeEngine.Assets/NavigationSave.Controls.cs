using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Navigation;

namespace SomeEngine.Assets;

internal sealed record NavigationSavedMppi(int Space, NavigationMppiSettings Settings);
internal readonly record struct NavigationSavedControls(AvoidanceAgent? Avoidance,
    NavigationPrediction<NavigationVelocityPrediction>? Prediction, NavigationTrajectoryKind? Trajectory, NavigationSavedMppi? Mppi);

public static partial class NavigationSave
{
    private static NavigationSavedControls CaptureControls(World world, Entity entity, NavigationSaveResourceCapture capture)
    {
        AvoidanceAgent? avoidance = world.Has<AvoidanceAgent>(entity) ? world.Read<AvoidanceAgent>(entity) : null;
        NavigationPrediction<NavigationVelocityPrediction>? prediction = world.Has<NavigationPrediction<NavigationVelocityPrediction>>(entity)
            ? world.Read<NavigationPrediction<NavigationVelocityPrediction>>(entity) : null;
        NavigationTrajectoryKind? source = world.Has<NavigationTrajectorySource>(entity) ? world.Read<NavigationTrajectorySource>(entity).Kind : null;
        NavigationSavedMppi? mppi = null;
        if (world.Has<NavigationMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>>(entity))
        {
            var value = world.Read<NavigationMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>>(entity);
            mppi = new(capture.Add(value.Space), value.Settings);
        }
        return new(avoidance, prediction, source, mppi);
    }

    private static async Task RestoreControls(World world, Entity entity, NavigationSavedControls controls, NavigationSaveResourceReader reader)
    {
        if (controls.Prediction is { } prediction)
            NavigationComponents.AddPrediction<NavigationVelocityPrediction>(world, entity, prediction.Horizon, prediction.Interval);
        if (controls.Mppi is { } mppi)
            NavigationComponents.AddMppi<NavigationVehicleSpace, NavigationVehicleMppiModel>(world, entity,
                (NavigationVehicleSpace)await reader.Get(mppi.Space).ConfigureAwait(false), mppi.Settings, controls.Avoidance!.Value);
        else if (controls.Avoidance is { } avoidance) NavigationComponents.AddAvoidance(world, entity, avoidance);
        if (controls.Trajectory is { } kind) world.Replace(entity, new NavigationTrajectorySource(kind));
    }
}

internal readonly partial struct NavigationSaveCodec
{
    private static void WriteControls(ref DataWriter writer, NavigationSavedControls controls)
    {
        writer.WriteBoolean(controls.Avoidance.HasValue);
        if (controls.Avoidance is { } avoidance)
        {
            writer.WriteSingle(avoidance.Radius); writer.WriteSingle(avoidance.NeighborDistance); writer.WriteInt32(avoidance.MaximumNeighbors);
            writer.WriteSingle(avoidance.TimeHorizon); writer.WriteSingle(avoidance.BoundaryTimeHorizon); writer.WriteSingle(avoidance.Spacing);
            writer.WriteSingle(avoidance.Responsibility); writer.WriteUInt64(avoidance.InteractionMask); writer.WriteSingle(avoidance.ResponseTime);
        }
        writer.WriteBoolean(controls.Prediction.HasValue);
        if (controls.Prediction is { } prediction) { writer.WriteSingle(prediction.Horizon); writer.WriteSingle(prediction.Interval); }
        writer.WriteBoolean(controls.Trajectory.HasValue);
        if (controls.Trajectory is { } trajectory) writer.WriteByte((byte)trajectory);
        writer.WriteBoolean(controls.Mppi is not null);
        if (controls.Mppi is { } mppi)
        {
            NavigationMppiSettings settings = mppi.Settings;
            writer.WriteInt32(mppi.Space); writer.WriteInt32(settings.CandidateCount); writer.WriteInt32(settings.HorizonSteps);
            writer.WriteSingle(settings.TimeStep); writer.WriteInt32(settings.Iterations);
            writer.WriteSingle(settings.NoiseStandardDeviation.X); writer.WriteSingle(settings.NoiseStandardDeviation.Y);
            writer.WriteSingle(settings.NoiseCorrelation); writer.WriteSingle(settings.Temperature); writer.WriteSingle(settings.ControlWeight);
            writer.WriteSingle(settings.PathWeight); writer.WriteSingle(settings.GoalWeight); writer.WriteSingle(settings.HeadingWeight);
            writer.WriteSingle(settings.NeighborWeight); writer.WriteSingle(settings.TraversalWeight); writer.WriteSingle(settings.CollisionResolution);
            writer.WriteSingle(settings.ReferenceLookahead); writer.WriteUInt32(settings.Seed);
        }
    }

    private static NavigationSavedControls ReadControls(ref DataReader reader)
    {
        AvoidanceAgent? avoidance = reader.ReadBoolean() ? new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadUInt64(), reader.ReadSingle()) : null;
        NavigationPrediction<NavigationVelocityPrediction>? prediction = reader.ReadBoolean() ? new(reader.ReadSingle(), reader.ReadSingle()) : null;
        NavigationTrajectoryKind? trajectory = reader.ReadBoolean() ? (NavigationTrajectoryKind)reader.ReadByte() : null;
        NavigationSavedMppi? mppi = null;
        if (reader.ReadBoolean())
        {
            int space = reader.ReadInt32();
            var settings = new NavigationMppiSettings
            {
                CandidateCount = reader.ReadInt32(), HorizonSteps = reader.ReadInt32(), TimeStep = reader.ReadSingle(), Iterations = reader.ReadInt32(),
                NoiseStandardDeviation = new(reader.ReadSingle(), reader.ReadSingle()), NoiseCorrelation = reader.ReadSingle(), Temperature = reader.ReadSingle(),
                ControlWeight = reader.ReadSingle(), PathWeight = reader.ReadSingle(), GoalWeight = reader.ReadSingle(), HeadingWeight = reader.ReadSingle(),
                NeighborWeight = reader.ReadSingle(), TraversalWeight = reader.ReadSingle(), CollisionResolution = reader.ReadSingle(),
                ReferenceLookahead = reader.ReadSingle(), Seed = reader.ReadUInt32(),
            };
            mppi = new(space, settings);
        }
        return new(avoidance, prediction, trajectory, mppi);
    }
}
