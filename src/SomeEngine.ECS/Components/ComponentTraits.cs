namespace SomeEngine.ECS.Components;

/// <summary>Marks values stored in an entity buffer.</summary>
public interface IBufferElement;

/// <summary>Marks values stored outside entity tables.</summary>
public interface ISparseComponent : IComponent;
