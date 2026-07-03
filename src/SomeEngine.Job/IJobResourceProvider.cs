namespace SomeEngine.Job;

public interface IJobResourceProvider<TContainer, TAccess>
{
    static abstract JobResourceAccess Read(ref TContainer container, TAccess access);

    static abstract JobResourceAccess Write(ref TContainer container, TAccess access);

    static abstract JobResourceAccess Exclusive(ref TContainer container, TAccess access);
}



