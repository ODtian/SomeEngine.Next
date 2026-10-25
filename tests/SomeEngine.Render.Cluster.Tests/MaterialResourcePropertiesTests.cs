using System.IO;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Buffer = SomeEngine.Graphics.Buffer;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class MaterialResourcePropertiesTests
{
    [Fact]
    public void LinkedResourcesUseTheFirstOrdinalPropertyMatch()
    {
        MaterialPropertyDefinition[] properties =
        [
            new(17, "Mask", typeof(SomeEngine.Resources.Texture)),
            new(42, "Color", typeof(SomeEngine.Resources.Texture)),
            new(99, "Color", typeof(SomeEngine.Resources.Texture)),
        ];
        ShaderParameterResource[] resources =
        [
            new("Color", ResourceBindingType.TextureSrv, 0, true),
            new("color", ResourceBindingType.TextureSrv, 0, true),
            new("Mask", ResourceBindingType.TextureUav, 0, true),
            new("Missing", ResourceBindingType.Sampler, 0, true),
            new("Color", ResourceBindingType.TextureUav, 2, false),
        ];

        using var material = new PropertyListMaterial(properties);
        uint?[] actual = material.ResolveResourceProperties(resources);

        uint?[] expected = [1u, null, 0u, null, 1u];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnusedResourceNamesRemainUnmapped()
    {
        MaterialPropertyDefinition[] properties =
        [
            new(42, "Color", typeof(SomeEngine.Resources.Texture)),
            new(17, "Disabled", typeof(SomeEngine.Resources.Texture)),
        ];
        ShaderParameterResource[] resources =
        [
            new("Color", ResourceBindingType.TextureSrv, 0, false),
            new("Color", ResourceBindingType.BufferSrv, 2, false),
            new("Disabled", ResourceBindingType.None, 0, true),
        ];

        using var material = new PropertyListMaterial(properties);
        uint?[] actual = material.ResolveResourceProperties(resources);

        uint?[] expected = [null, null, null];
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GeneratedMaterialsFindTheirDeclaredPropertyPositions()
    {
        using var material = new Game.Content.UnlitMaterial();

        Assert.Equal<uint?>(0u, material.FindPropertyIndex(nameof(Game.Content.UnlitMaterial.AlbedoMap)));
        Assert.Equal<uint?>(1u, material.FindPropertyIndex(nameof(Game.Content.UnlitMaterial.MaterialSampler)));
        Assert.Equal<uint?>(2u, material.FindPropertyIndex(nameof(Game.Content.UnlitMaterial.BaseColorTint)));
        Assert.Null(material.FindPropertyIndex("Missing"));
        Assert.Null(material.FindPropertyIndex("albedoMap"));
        Assert.Null(material.FindPropertyIndex(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryLinkedOccurrenceMustMatchTheActivatedResourceKind(bool conflictingIsUsed)
    {
        MaterialPropertyDefinition[] properties = [new(42, "Color", typeof(SomeEngine.Resources.Texture))];
        ShaderParameterResource[] resources =
        [
            new("Color", ResourceBindingType.TextureSrv, 0, true),
            new("Color", ResourceBindingType.BufferSrv, 2, conflictingIsUsed),
        ];

        using var material = new PropertyListMaterial(properties);
        Assert.Throws<InvalidDataException>(
            () => material.ResolveResourceProperties(resources));
    }

    [Theory]
    [InlineData(ResourceBindingType.BufferSrv)]
    [InlineData(ResourceBindingType.BufferUav)]
    [InlineData(ResourceBindingType.ConstantBuffer)]
    [InlineData(ResourceBindingType.AccelerationStructure)]
    public void MissingDeviceResourcesKeepTheirValidation(ResourceBindingType type)
    {
        using var material = new PropertyListMaterial([]) { Name = "Native material" };

#if DEBUG
        Assert.Throws<InvalidOperationException>(
            () => material.GetDeviceResourceBinding(23u, type));
#else
        var error = Assert.Throws<ArgumentNullException>(
            () => material.GetDeviceResourceBinding(23u, type));
        Assert.Equal("value", error.ParamName);
#endif
        Assert.Equal(1, material.DeviceResourceReads);
        Assert.Equal<uint?>(23u, material.LastDeviceResourceProperty);
    }

    [Theory]
    [InlineData(ResourceBindingType.None)]
    [InlineData(ResourceBindingType.TextureSrv)]
    [InlineData(ResourceBindingType.TextureUav)]
    [InlineData(ResourceBindingType.Sampler)]
    [InlineData((ResourceBindingType)byte.MaxValue)]
    public void NonDeviceBindingTypesDoNotReadMaterialResources(ResourceBindingType type)
    {
        using var material = new PropertyListMaterial([]);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => material.GetDeviceResourceBinding(23u, type));

        Assert.Equal("type", error.ParamName);
        Assert.Equal(0, material.DeviceResourceReads);
        Assert.Null(material.LastDeviceResourceProperty);
    }

    [Fact]
    public void DeviceResourceBindingsFollowPropertyReplacement()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        var options = new AdapterEnumerationOptions(AdapterPreference.Unspecified, IncludeSoftware: true);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(options, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapters.First(static adapter => !adapter.HardwareAccelerated).Id, [new(QueueType.Graphics)]));
        using Buffer firstData = backend.CreateBuffer(device,
            new BufferDesc(4096, BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
        using Buffer firstCounter = backend.CreateBuffer(device, new BufferDesc(4096, BufferUsages.ShaderWrite));
        using Buffer secondData = backend.CreateBuffer(device,
            new BufferDesc(4096, BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
        using Buffer secondCounter = backend.CreateBuffer(device, new BufferDesc(4096, BufferUsages.ShaderWrite));
        using BufferSrv firstRead = backend.CreateBufferSrv(device,
            new BufferSrvDesc(firstData, BufferRange.Whole, StructureStride: 16));
        using BufferUav firstWrite = backend.CreateBufferUav(device,
            new BufferUavDesc(firstData, BufferRange.Whole, StructureStride: 16, CounterBuffer: firstCounter));
        using BufferSrv secondRead = backend.CreateBufferSrv(device,
            new BufferSrvDesc(secondData, BufferRange.Whole, StructureStride: 16));
        using BufferUav secondWrite = backend.CreateBufferUav(device,
            new BufferUavDesc(secondData, BufferRange.Whole, StructureStride: 16, CounterBuffer: secondCounter));
        using var material = new DeviceResourceMaterial(firstRead, firstWrite);
        ShaderParameterResource[] resources =
        [
            new("WriteData", ResourceBindingType.BufferUav, 0, true),
            new("ReadData", ResourceBindingType.BufferSrv, 0, true),
        ];
        uint?[] properties = material.ResolveResourceProperties(resources);
        Assert.Equal<uint?>([1u, 0u], properties);
        ResourceBinding firstWriteBinding = material.GetDeviceResourceBinding(
            properties[0].GetValueOrDefault(), resources[0].Type);
        ResourceBinding firstReadBinding = material.GetDeviceResourceBinding(
            properties[1].GetValueOrDefault(), resources[1].Type);
        Assert.Equal(ResourceBindingType.BufferUav, firstWriteBinding.Type);
        Assert.Equal(ResourceBindingType.BufferSrv, firstReadBinding.Type);
        Assert.Same(firstWrite, firstWriteBinding.Value);
        Assert.Same(firstRead, firstReadBinding.Value);

        material.ReadData = secondRead;
        material.WriteData = secondWrite;
        ResourceBinding secondWriteBinding = material.GetDeviceResourceBinding(
            properties[0].GetValueOrDefault(), resources[0].Type);
        ResourceBinding secondReadBinding = material.GetDeviceResourceBinding(
            properties[1].GetValueOrDefault(), resources[1].Type);
        Assert.Equal(ResourceBindingType.BufferUav, secondWriteBinding.Type);
        Assert.Equal(ResourceBindingType.BufferSrv, secondReadBinding.Type);
        Assert.Same(secondWrite, secondWriteBinding.Value);
        Assert.Same(secondRead, secondReadBinding.Value);
        Assert.Same(secondCounter, ((BufferUav)secondWriteBinding.Value!).Description.CounterBuffer);
        Assert.Same(firstWrite, firstWriteBinding.Value);
        Assert.Same(firstRead, firstReadBinding.Value);
    }

    private sealed class DeviceResourceMaterial(BufferSrv readData, BufferUav writeData) : Material
    {
        public BufferSrv ReadData { get; set; } = readData;
        public BufferUav WriteData { get; set; } = writeData;
        public override Guid MaterialTypeId => Guid.Empty;
        public override IReadOnlyList<MaterialPropertyDefinition> Properties { get; } =
        [
            new(17, "ReadData", typeof(BufferSrv)),
            new(42, "WriteData", typeof(BufferUav)),
        ];
        public override DeviceResource? GetDeviceResource(uint index) => index switch
        {
            0 => ReadData,
            1 => WriteData,
            _ => null,
        };
        public override void ApplyParameter(MaterialValue parameter, SomeEngine.Resources.Texture? texture)
            => throw new NotSupportedException();
        protected override void CopyProperties(Material source) => throw new NotSupportedException();
        protected override Material CreateEmpty() => throw new NotSupportedException();
    }

    private sealed class PropertyListMaterial(IReadOnlyList<MaterialPropertyDefinition> properties) : Material
    {
        public int DeviceResourceReads { get; private set; }
        public uint? LastDeviceResourceProperty { get; private set; }
        public override DeviceResource? GetDeviceResource(uint index)
        {
            DeviceResourceReads++;
            LastDeviceResourceProperty = index;
            return null;
        }
        public override Guid MaterialTypeId => Guid.Empty;
        public override IReadOnlyList<MaterialPropertyDefinition> Properties => properties;
        public override void ApplyParameter(MaterialValue parameter, SomeEngine.Resources.Texture? texture)
            => throw new NotSupportedException();
        protected override void CopyProperties(Material source) => throw new NotSupportedException();
        protected override Material CreateEmpty() => new PropertyListMaterial(properties);
    }
}
