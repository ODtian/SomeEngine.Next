using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Comparison;

// Component sets created by the engine-only checks of this project.

internal partial struct CounterPosition : IComponentBundle { internal SaveCounter Counter; internal Position Position; }
internal partial struct PositionExtra : IComponentBundle { internal Position Position; internal Extra Extra; }
internal partial struct OrdinalMotion : IComponentBundle { internal Motion Motion; internal JobOrdinal Ordinal; }
internal partial struct HealthTeam : IComponentBundle { internal Health Health; internal Team Team; }
internal partial struct HealthTeamGrowth : IComponentBundle { internal Health Health; internal Team Team; internal GrowthMarker Marker; }
internal partial struct NodeTeam : IComponentBundle { internal Position Position; internal SomeCopyNode Node; internal Team Team; }
internal partial struct NodeSleeping : IComponentBundle { internal Position Position; internal SomeCopyNode Node; internal Sleeping Sleeping; }
internal partial struct GeneratedRow : IComponentBundle
{ internal GeneratedCounter Counter; internal GeneratedOther Other; internal GeneratedVelocity Velocity; }
