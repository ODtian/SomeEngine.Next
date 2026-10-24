namespace SomeEngine.ECS;

/// <summary>Reads and writes one typed leaf through the component's current value and references.</summary>
public interface IFieldAccess<TComponent, TValue>
    where TComponent : struct, IComponent
    where TValue : unmanaged
{
    static abstract TValue Read(in TComponent component);

    static abstract void Write(ref TComponent component, in TValue value);
}
