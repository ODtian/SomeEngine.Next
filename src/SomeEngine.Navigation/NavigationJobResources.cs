using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

public readonly struct NavigationAgentResources : IJobComponentResources<NavigationAgent>
{
    public static void Collect(in NavigationAgent component, ref JobResourceAccessBuilder resources)
    {
        resources.Read(component.Profile.Footprint);
        if (!component.Policy.AreaCosts.IsEmpty)
            resources.Read(component.Policy.AreaCosts);
        if (!component.Policy.ActionCosts.IsEmpty)
            resources.Read(component.Policy.ActionCosts);
    }
}

public readonly struct PathNavigationResources<TSpace> : IJobComponentResources<PathNavigation<TSpace>>
    where TSpace : class, INavigationSpace<TSpace>
{
    public static void Collect(in PathNavigation<TSpace> component, ref JobResourceAccessBuilder resources) =>
        TSpace.CollectResources(component.Space, ref resources);

    public static long GetVersion(in PathNavigation<TSpace> component) => TSpace.GetVersion(component.Space);
}

public readonly struct NavigationPathResources : IJobComponentResources<NavigationPathResult>
{
    public static void Collect(in NavigationPathResult component, ref JobResourceAccessBuilder resources)
    {
        if (component.Path is not { } path)
            return;
        resources.Read(path.Corridor);
        resources.Read(path.Corners);
        if (!path.Dependencies.IsEmpty)
            resources.Read(path.Dependencies);
        if (path.RegionRoute is { } route) resources.Read(route.Entrances);
        if (!path.ConnectionDependencies.IsEmpty) resources.Read(path.ConnectionDependencies);
        if (!path.Sections.IsEmpty)
        {
            resources.Read(path.Sections);
            foreach (NavigationPathSection section in path.Sections.Span)
                NavigationMesh.CollectResources(section.Mesh, ref resources);
        }
    }
}

// Assigning a completed path changes the component reference; it reads no prior path arrays.
internal readonly struct NavigationPathAssignment : IJobComponentResources<NavigationPathResult>
{
    public static void Collect(in NavigationPathResult component, ref JobResourceAccessBuilder resources) { }
}

internal readonly struct NavigationRequestResources<TSpace, TState> : IJobComponentResources<NavigationRequest<TSpace, TState>>
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
{
    public static void Collect(in NavigationRequest<TSpace, TState> component, ref JobResourceAccessBuilder resources) =>
        resources.Exclusive(component.Data);
}
