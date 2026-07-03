using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Commands
{
    private CommandBuffer? _buffer;

    internal CommandBuffer Get(World world)
    {
        return _buffer ??= new CommandBuffer(world);
    }

    internal void Flush()
    {
        if (_buffer is null)
            return;

        _buffer.Playback();
        _buffer.Clear();
    }
}


