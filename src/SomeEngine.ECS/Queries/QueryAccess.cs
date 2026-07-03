namespace SomeEngine.ECS.Queries;

/// <summary>
/// Declares the data access requested by a query term.
/// </summary>
public enum QueryAccess : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = 3,
}

internal static class QueryAccessExtensions
{
    public static bool CanRead(this QueryAccess access) =>
        access == QueryAccess.Read || access == QueryAccess.ReadWrite;

    public static bool CanWrite(this QueryAccess access) =>
        access == QueryAccess.Write || access == QueryAccess.ReadWrite;

    public static QueryAccess Merge(QueryAccess left, QueryAccess right)
    {
        bool read = left.CanRead() || right.CanRead();
        bool write = left.CanWrite() || right.CanWrite();

        return (read, write) switch
        {
            (true, true) => QueryAccess.ReadWrite,
            (true, false) => QueryAccess.Read,
            (false, true) => QueryAccess.Write,
            _ => QueryAccess.None,
        };
    }
}

