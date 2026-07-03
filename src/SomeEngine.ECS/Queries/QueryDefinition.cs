namespace SomeEngine.ECS.Queries;

public sealed class QueryDefinition
{
    internal QueryDefinition(QueryTerm[] terms)
    {
        TermsArray = terms;
        Key = new QueryKey(terms);

        var accesses = new List<QueryAccessEntry>();
        for (int i = 0; i < terms.Length; i++)
        {
            var term = terms[i];
            if (term.Access != QueryAccess.None)
                accesses.Add(new QueryAccessEntry(term.ComponentId, term.Access, term.Kind));
        }

        AccessesArray = accesses.Count == 0 ? Array.Empty<QueryAccessEntry>() : accesses.ToArray();
    }

    public static QueryDefinition Empty { get; } = new(Array.Empty<QueryTerm>());

    public IReadOnlyList<QueryTerm> Terms => TermsArray;

    public IReadOnlyList<QueryAccessEntry> Accesses => AccessesArray;

    public QueryKey Key { get; }

    internal QueryTerm[] TermsArray { get; }

    internal QueryAccessEntry[] AccessesArray { get; }

}

