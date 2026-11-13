using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;

namespace SomeEngine.Physics;

public readonly record struct DeviceGridSettings(float CellSize, int LevelCount);
public readonly record struct DeviceGridCapacity(int Bodies, int Cells, int Buckets, int Pairs, int Bins);
public readonly record struct DeviceGridKernels(ComputePipelineDesc Clear, ComputePipelineDesc Build,
    ComputePipelineDesc Count, ComputePipelineDesc Scan, ComputePipelineDesc OffsetBlocks,
    ComputePipelineDesc OffsetBins, ComputePipelineDesc Write);

internal enum DevicePairing : uint { Within, Between, Product }
[Flags]
internal enum DevicePairMembership : uint { First = 1, Second = 2 }
[Flags]
public enum DevicePairRules : uint { None, MutualCategories = 1, OneWayCategories = 2, ExceptMatchingKeys = 4 }
public enum DeviceInputTransfer { Resident, Upload }
public enum DevicePairReadback { None, Pairs }

/// <summary>The component-held device resource and the index used by this entity's device representation.</summary>
public readonly record struct DeviceCollisionBody(DeviceCollisionGrid Grid, int Index) : IComponent;

/// <summary>One concrete component's conversion into the common device collision input.</summary>
public interface IDeviceCollisionReader<TComponent> where TComponent : struct, IComponent
{
    DeviceCollisionInput Read(Entity entity, in TComponent component);
}

[StructLayout(LayoutKind.Sequential)]
public struct DeviceCollisionInput
{
    public Vector3 Min;
    public uint EntityIndex;
    public Vector3 Max;
    public uint EntityGeneration;
    public uint GeometryType;
    public uint GeometryIndex;
    public uint Members;
    public uint Revision;
    public uint KeyLow;
    public uint KeyHigh;
    public uint CategoriesLow;
    public uint CategoriesHigh;
    public uint AllowedLow;
    public uint AllowedHigh;
    public uint HasKey;
    public uint UserValue;
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DeviceCollisionPair(Entity First, Entity Second, uint FirstGeometry,
    uint SecondGeometry, int FirstBody, int SecondBody);

/// <summary>Declares the actual mapped upload range written for one bound entity.</summary>
public readonly struct DeviceCollisionUploadResources : IJobComponentResources<DeviceCollisionBody>
{
    public static void Collect(in DeviceCollisionBody component, ref JobResourceAccessBuilder resources) =>
        resources.Write(component.Grid.Upload, (long)component.Index * DeviceCollisionGrid.InputStride,
            DeviceCollisionGrid.InputStride);
}

/// <summary>Declares one entity's actual resident device input range.</summary>
public readonly struct DeviceCollisionBodyResources : IJobComponentResources<DeviceCollisionBody>
{
    public static void Collect(in DeviceCollisionBody component, ref JobResourceAccessBuilder resources) =>
        resources.Read(component.Grid.Bodies, (long)component.Index * DeviceCollisionGrid.InputStride,
            DeviceCollisionGrid.InputStride);
}
