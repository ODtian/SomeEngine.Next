namespace SomeEngine.Job;

public interface IJobResourceProvider<TContainer, TAccess>
{
    static abstract JobResourceAccess Read(Scheduler scheduler, ref TContainer container, TAccess access);

    static abstract JobResourceAccess Write(Scheduler scheduler, ref TContainer container, TAccess access);

    static abstract JobResourceAccess Exclusive(Scheduler scheduler, ref TContainer container, TAccess access);
}



