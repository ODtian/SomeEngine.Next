using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

/// <summary>Runtime-owned read-only borrow of compact sparse storage.</summary>
public delegate void SparseReadExecution<T>(
    ReadOnlySpan<Entity> entities,
    ReadOnlySpan<T> values)
    where T : struct, ISparseComponent;

/// <summary>Runtime-owned read-only sparse borrow with caller-provided state.</summary>
public delegate void SparseReadExecution<T, TState>(
    ReadOnlySpan<Entity> entities,
    ReadOnlySpan<T> values,
    ref TState state)
    where T : struct, ISparseComponent;

/// <summary>Runtime-owned writable borrow of compact sparse storage.</summary>
public delegate void SparseWriteExecution<T>(
    ReadOnlySpan<Entity> entities,
    Span<T> values)
    where T : struct, ISparseComponent;

/// <summary>Runtime-owned writable sparse borrow with caller-provided state.</summary>
public delegate void SparseWriteExecution<T, TState>(
    ReadOnlySpan<Entity> entities,
    Span<T> values,
    ref TState state)
    where T : struct, ISparseComponent;
