using SomeEngine.ECS.Components;

namespace SomeEngine.ECS;

/// <summary>Runtime-owned read-only buffer borrow.</summary>
public delegate void BufferReadExecution<T>(BufferView<T> buffer)
    where T : struct, IBufferElement;

/// <summary>Runtime-owned read-only buffer borrow with caller-provided state.</summary>
public delegate void BufferReadExecution<T, TState>(BufferView<T> buffer, ref TState state)
    where T : struct, IBufferElement;

/// <summary>Runtime-owned writable buffer borrow.</summary>
public delegate void BufferWriteExecution<T>(DynamicBuffer<T> buffer)
    where T : struct, IBufferElement;

/// <summary>Runtime-owned writable buffer borrow with caller-provided state.</summary>
public delegate void BufferWriteExecution<T, TState>(DynamicBuffer<T> buffer, ref TState state)
    where T : struct, IBufferElement;
