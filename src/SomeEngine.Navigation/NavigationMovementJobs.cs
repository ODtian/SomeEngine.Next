using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

internal partial struct NavigationGuidanceJob<TSpace, TState, TMotion, TReader>(float deltaTime) : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly float _deltaTime = deltaTime;

    public void Execute(
        [JobResources<NavigationAgentResources>] in NavigationAgent agent,
        [JobResources(typeof(PathNavigationResources<>))] in PathNavigation<TSpace> navigation,
        [JobResources<NavigationPathResources>] in NavigationPathResult result,
        [JobResources(typeof(NavigationRequestResources<,>))] ref NavigationRequest<TSpace, TState> request,
        in NavigationGoal goal, in TMotion movement, ref NavigationGuidance guidance)
    {
        NavigationMotion motion = TReader.Read(in movement);
        guidance = new(default, motion.Heading, result.RequestVersion, false);
        if (!result.HasTarget)
            return;
        float regionDistance = MathF.Max(0, Vector3.Distance(motion.Position, result.TargetPosition) - goal.ArrivalRange);
        if (regionDistance <= 0.0001f)
        {
            guidance = new(default, goal.Heading ?? motion.Heading, result.RequestVersion, true);
            return;
        }
        if (result.Path is not { } path || result.Status != NavigationSearchStatus.Found)
            return;

        NavigationRequestData<TSpace, TState> data = request.Data;
        ReadOnlySpan<Vector3> corners = path.Corners.Span;
        int corner = data.CornerIndex;
        while (corner < corners.Length - 1 && HorizontalDistance(motion.Position, corners[corner]) <= 0.0001f)
            corner++;
        data.CornerIndex = corner;
        Vector3 direction = corners[corner] - motion.Position;
        float distance = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (distance <= 0.0001f)
            return;
        float travel = MathF.Min(MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed) * _deltaTime, distance);
        if (corner == corners.Length - 1)
            travel = MathF.Min(travel, regionDistance);
        Vector3 next = motion.Position + direction * (travel / distance);
        if (path.TrySampleHeight(next, out float height) || TSpace.TryGetHeight(navigation.Space, next, out height))
            next.Y = height;
        Vector3 velocity = (next - motion.Position) / _deltaTime;
        guidance = new(velocity, NavigationHeading.FromDirection(direction), result.RequestVersion, true);
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        float x = first.X - second.X, z = first.Z - second.Z;
        return MathF.Sqrt(x * x + z * z);
    }
}

internal partial struct NavigationSteeringJob : IJobEntity
{
    public void Execute(in NavigationGuidance guidance, ref NavigationSteering steering) =>
        steering = new(guidance.Velocity, guidance.Heading, guidance.RequestVersion, guidance.Active);
}

internal partial struct NavigationProgressJob<TMotion, TReader>(World world) : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world = world;

    public void Execute(in NavigationGoal goal,
        [JobResources<NavigationPathAssignment>] in NavigationPathResult path,
        in TMotion movement, ref NavigationProgress progress)
    {
        if (!NavigationTargets<TMotion, TReader>.TryResolve(_world, in goal, out Vector3 target))
        {
            progress = new(NavigationStatus.TargetUnavailable, float.PositiveInfinity, 0, path.RequestVersion);
            return;
        }
        NavigationMotion motion = TReader.Read(in movement);
        float distance = MathF.Max(0, Vector3.Distance(motion.Position, target) - goal.ArrivalRange);
        float headingError = goal.Heading is { } heading
            ? MathF.Abs(NavigationHeading.Difference(heading, motion.Heading)) : 0;
        NavigationStatus status = distance <= 0.0001f
            ? headingError <= goal.HeadingTolerance ? NavigationStatus.Arrived : NavigationStatus.Aligning
            : path.Status switch
            {
                NavigationSearchStatus.Searching => NavigationStatus.Searching,
                NavigationSearchStatus.Unreachable => NavigationStatus.Unreachable,
                _ => NavigationStatus.Moving,
            };
        progress = new(status, distance, headingError, path.RequestVersion);
    }
}
