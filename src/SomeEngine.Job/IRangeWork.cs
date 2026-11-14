namespace SomeEngine.Job;

internal interface IRangeWork<TItem>
{
    void Execute(in TItem item);
}
