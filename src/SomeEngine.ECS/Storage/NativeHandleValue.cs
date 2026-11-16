namespace SomeEngine.ECS;

/// <summary>
/// Declares that a value holds pointers, native-sized handles or fixed buffers, so a Job cannot borrow it by
/// reference. The ECS generator requires this declaration on every component or buffer element that holds them.
/// </summary>
public interface INativeHandleValue;
