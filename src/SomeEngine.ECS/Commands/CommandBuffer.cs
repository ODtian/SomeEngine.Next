using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Commands;

/// <summary>
/// 命令类型枚举。
/// </summary>
internal enum CommandType : byte
{
    CreateEntity,
    DestroyEntity,
    AddComponent,
    RemoveComponent,
    ReplaceComponent,
    AddTag,
    RemoveTag,
}

/// <summary>
/// 命令头。固定大小，存储命令元信息。
/// </summary>
internal struct CommandHeader
{
    public CommandType Type;
    public Entity Entity;
    public int ComponentId;
    public int DataIndex; // 指向 CommandDataList 的 index，-1 = 无数据
}

/// <summary>
/// Typed 数据列表接口。回放时根据 CommandType 分发。
/// </summary>
internal interface ICommandList
{
    void PlaybackAdd(World world, Entity entity, int dataIndex);
    void PlaybackReplace(World world, Entity entity, int dataIndex);
    void PlaybackRemove(World world, Entity entity);
    void Clear();
}

/// <summary>
/// 泛型数据列表。按类型分桶，避免 boxing。
/// </summary>
internal sealed class CommandDataList<T> : ICommandList where T : struct, IComponent
{
    private T[] _data = new T[8];
    private int _count;

    public int Append(in T value)
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _data, _count + 1, 8);
        _data[_count] = value;
        return _count++;
    }

    public void PlaybackAdd(World world, Entity entity, int dataIndex)
    {
        world.Add(entity, in _data[dataIndex]);
    }

    public void PlaybackReplace(World world, Entity entity, int dataIndex)
    {
        world.Replace(entity, in _data[dataIndex]);
    }

    public void PlaybackRemove(World world, Entity entity)
    {
        world.Remove<T>(entity);
    }

    public void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_data, 0, _count);

        _count = 0;
    }
}

/// <summary>
/// CommandBuffer：在迭代期间收集延迟的结构变更命令，迭代结束后统一回放。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md 决策 #30, #34
/// - 命令头 + typed 数据分桶，零 boxing
/// - FIFO 顺序回放
/// - CreateEntity 预分配真实 ID
/// </remarks>
public sealed class CommandBuffer : IDisposable
{
    private readonly World _world;
    private readonly List<CommandHeader> _commands = new();
    private ICommandList?[] _dataLists = new ICommandList?[8];
    private bool _disposed;

    public CommandBuffer(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <summary>已录入的命令数。</summary>
    public int CommandCount => _commands.Count;

    // ════════════════════════════════════════════════
    // 延迟命令录入
    // ════════════════════════════════════════════════

    /// <summary>Reserve a real Entity. Playback spawns the reserved entity.</summary>
    public Entity CreateEntity()
    {
        ThrowIfDisposed();
        var entity = _world.ReserveEntity();
        _commands.Add(new CommandHeader
        {
            Type = CommandType.CreateEntity,
            Entity = entity,
            ComponentId = 0,
            DataIndex = -1,
        });
        return entity;
    }

    /// <summary>延迟销毁 entity。</summary>
    public void DestroyEntity(Entity entity)
    {
        ThrowIfDisposed();
        _commands.Add(new CommandHeader
        {
            Type = CommandType.DestroyEntity,
            Entity = entity,
            ComponentId = 0,
            DataIndex = -1,
        });
    }

    /// <summary>延迟添加 Table 组件。</summary>
    public void Add<T>(Entity entity, in T value) where T : struct, IComponent
    {
        ThrowIfDisposed();
        int componentId = ComponentMetadata<T>.Id;
        var list = DataList<T>(componentId);
        int dataIndex = list.Append(in value);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.AddComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    /// <summary>延迟整值替换 Table 组件（组件必须已存在）。</summary>
    public void Replace<T>(Entity entity, in T value) where T : struct, IComponent
    {
        ThrowIfDisposed();
        int componentId = ComponentMetadata<T>.Id;
        var list = DataList<T>(componentId);
        int dataIndex = list.Append(in value);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.ReplaceComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = dataIndex,
        });
    }

    /// <summary>延迟移除 Table 组件。</summary>
    public void Remove<T>(Entity entity) where T : struct, IComponent
    {
        ThrowIfDisposed();
        int componentId = ComponentMetadata<T>.Id;
        DataList<T>(componentId);
        _commands.Add(new CommandHeader
        {
            Type = CommandType.RemoveComponent,
            Entity = entity,
            ComponentId = componentId,
            DataIndex = -1,
        });
    }

    /// <summary>延迟添加 Tag。</summary>
    public void AddTag<T>(Entity entity) where T : struct, ITag
    {
        ThrowIfDisposed();
        _commands.Add(new CommandHeader
        {
            Type = CommandType.AddTag,
            Entity = entity,
            ComponentId = ComponentMetadata<T>.Id,
            DataIndex = -1,
        });
    }

    /// <summary>延迟移除 Tag。</summary>
    public void RemoveTag<T>(Entity entity) where T : struct, ITag
    {
        ThrowIfDisposed();
        _commands.Add(new CommandHeader
        {
            Type = CommandType.RemoveTag,
            Entity = entity,
            ComponentId = ComponentMetadata<T>.Id,
            DataIndex = -1,
        });
    }

    // ════════════════════════════════════════════════
    // 回放
    // ════════════════════════════════════════════════

    /// <summary>按 FIFO 顺序回放所有命令到 World。</summary>
    public void Playback()
    {
        ThrowIfDisposed();
        if (_world.IsIterating)
            throw new InvalidOperationException(
                "Cannot playback CommandBuffer during iteration.");

        foreach (var command in _commands)
        {
            switch (command.Type)
            {
                case CommandType.CreateEntity:
                    _world.SpawnReserved(command.Entity);
                    break;

                case CommandType.DestroyEntity:
                    _world.DestroyEntity(command.Entity);
                    break;

                case CommandType.AddComponent:
                    var addList = DataList(command.ComponentId);
                    addList.PlaybackAdd(_world, command.Entity, command.DataIndex);
                    break;

                case CommandType.ReplaceComponent:
                    var replaceList = DataList(command.ComponentId);
                    replaceList.PlaybackReplace(_world, command.Entity, command.DataIndex);
                    break;

                case CommandType.RemoveComponent:
                    var removeList = DataList(command.ComponentId);
                    removeList.PlaybackRemove(_world, command.Entity);
                    break;

                case CommandType.AddTag:
                    _world.AddTagId(command.Entity, command.ComponentId);
                    break;

                case CommandType.RemoveTag:
                    _world.RemoveTagId(command.Entity, command.ComponentId);
                    break;
            }
        }
    }

    // ════════════════════════════════════════════════
    // 清理
    // ════════════════════════════════════════════════

    /// <summary>清空命令但保留内部数组容量以复用。</summary>
    public void Clear()
    {
        ReleaseReserved();
        _commands.Clear();
        ClearDataLists();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ReleaseReserved();
            _commands.Clear();
            _dataLists = Array.Empty<ICommandList?>();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════
    // 内部
    // ════════════════════════════════════════════════

    private CommandDataList<T> DataList<T>(int componentId) where T : struct, IComponent
    {
        ArrayGrowthExtensions.EnsureCapacity(ref _dataLists, componentId + 1, 8);

        var existing = _dataLists[componentId];
        if (existing != null)
            return (CommandDataList<T>)existing;

        var list = new CommandDataList<T>();
        _dataLists[componentId] = list;
        return list;
    }

    private ICommandList DataList(int componentId)
    {
        return (ICommandList)_dataLists[componentId]!;
    }

    private void ClearDataLists()
    {
        foreach (var obj in _dataLists)
            obj?.Clear();
    }

    private void ReleaseReserved()
    {
        foreach (var command in _commands)
        {
            if (command.Type == CommandType.CreateEntity)
                _world.ReleaseReserved(command.Entity);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CommandBuffer));
    }
}

