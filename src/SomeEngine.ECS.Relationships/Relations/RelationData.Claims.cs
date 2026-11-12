using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T>
{
    private void ValidateEndpoints(in RelationEndpoints<T> endpoints)
    {
        RequireActive(endpoints.Source);
        RequireActive(endpoints.Target);
        if (!_schema.AllowSelfEdge && endpoints.Source == endpoints.Target)
            throw new InvalidOperationException("This relation requires two distinct endpoints.");
    }

    private bool Conflicts(Entity occupant, Entity edge, Dictionary<Entity, RelationEndpoints<T>>? edits)
        => occupant != edge && Active(occupant) && !(edits?.ContainsKey(occupant) ?? false);

    private void ValidateClaim(EndpointClaim[] claims, Entity endpoint, Entity edge,
        Dictionary<Entity, RelationEndpoints<T>>? edits)
    {
        EndpointClaim claim = claims[endpoint.Id];
        if (claim.Generation == endpoint.Generation && Conflicts(claim.Edge, edge, edits))
            throw new InvalidOperationException("The endpoint already has an active relation claim.");
    }

    private void ValidateClaims(Entity edge, in RelationEndpoints<T> endpoints,
        Dictionary<Entity, RelationEndpoints<T>>? edits = null)
    {
        if (_uniquePair)
        {
            var pair = EndpointPair.From(endpoints.Source, endpoints.Target, _directed);
            if (_pairs.TryGetValue(pair, out Entity occupant) && Conflicts(occupant, edge, edits))
                throw new InvalidOperationException("The endpoint pair already has an active relation claim.");
        }
        if (_uniqueSource) ValidateClaim(_sourceClaims, endpoints.Source, edge, edits);
        if (_uniqueTarget)
            ValidateClaim(_directed ? _targetClaims : _sourceClaims, endpoints.Target, edge, edits);
    }

    private void Claim(Entity edge, in RelationEndpoints<T> endpoints)
    {
        if (_uniquePair) _pairs[EndpointPair.From(endpoints.Source, endpoints.Target, _directed)] = edge;
        if (_uniqueSource) _sourceClaims[endpoints.Source.Id] = new() { Generation = endpoints.Source.Generation, Edge = edge };
        if (_uniqueTarget)
        {
            var claims = _directed ? _targetClaims : _sourceClaims;
            claims[endpoints.Target.Id] = new() { Generation = endpoints.Target.Generation, Edge = edge };
        }
    }

    private static void ReleaseClaim(EndpointClaim[] claims, Entity endpoint, Entity edge)
    {
        ref EndpointClaim claim = ref claims[endpoint.Id];
        if (claim.Generation == endpoint.Generation && claim.Edge == edge) claim = default;
    }

    private void ReleaseClaims(Entity edge, in RelationEndpoints<T> endpoints)
    {
        if (_uniquePair)
        {
            var pair = EndpointPair.From(endpoints.Source, endpoints.Target, _directed);
            if (_pairs.GetValueOrDefault(pair) == edge) _pairs.Remove(pair);
        }
        if (_uniqueSource) ReleaseClaim(_sourceClaims, endpoints.Source, edge);
        if (_uniqueTarget) ReleaseClaim(_directed ? _targetClaims : _sourceClaims, endpoints.Target, edge);
    }
}
