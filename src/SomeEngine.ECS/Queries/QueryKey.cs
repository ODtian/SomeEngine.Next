namespace SomeEngine.ECS.Queries;

public readonly struct QueryKey : IEquatable<QueryKey>
{
    private readonly QueryTerm[] _terms;
    private readonly int _hash;

    internal QueryKey(QueryTerm[] ownedTerms)
    {
        _terms = ownedTerms;
        var hash = new HashCode();
        for (int i = 0; i < ownedTerms.Length; i++)
            hash.Add(ownedTerms[i]);
        _hash = hash.ToHashCode();
    }

    internal ReadOnlySpan<QueryTerm> Terms => _terms;

    public bool Equals(QueryKey other)
    {
        if (_hash != other._hash || _terms.Length != other._terms.Length)
            return false;

        for (int i = 0; i < _terms.Length; i++)
        {
            if (!_terms[i].Equals(other._terms[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is QueryKey other && Equals(other);

    public override int GetHashCode() => _hash;
}

