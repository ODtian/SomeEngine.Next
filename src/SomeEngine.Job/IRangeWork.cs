namespace SomeEngine.Job;

public interface IRangeWork<TItem>
{
    void Execute(in TItem item);
}
