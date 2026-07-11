using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ShaderContractTests
{
    private static readonly ShaderArtifactKey Key = new(1, 2, 3, 4);

    [Fact]
    public void Frozen_contract_and_canonical_data_preserve_reflected_and_declared_effects_independently()
    {
        using NullDevice device = new(new NullOptions());
        FrozenGraph reflectedUnknown = FreezeStorageShader(device, ReflectedAccess.Unknown, DeclaredEffect.Write);
        FrozenGraph reflectedReadWrite = FreezeStorageShader(device, ReflectedAccess.ReadWrite, DeclaredEffect.Write);

        FrozenShaderContract contract = reflectedReadWrite.Passes[0].Shaders[0];
        Assert.Equal(Key, contract.Key);
        Assert.Equal(ShaderStage.Compute, contract.Stage);
        Assert.Equal(0x0102_0304_0506_0708UL, contract.LayoutHash);
        Assert.Single(contract.Bindings);
        Assert.Equal(ReflectedAccess.ReadWrite, contract.Bindings[0].ReflectedAccess);
        Assert.Equal(DeclaredEffect.Write, contract.Bindings[0].DeclaredEffect);
        Assert.Single(contract.Accesses);
        Assert.Equal(ShaderBindingAccessKind.BufferView, contract.Accesses[0].Kind);

        // Both contracts resolve to the same user-declared write effect. The reflected fact still
        // participates independently in exact canonical equality.
        Assert.False(reflectedUnknown.Canonical.Equals(reflectedReadWrite.Canonical));
        Assert.NotEqual(reflectedUnknown.Canonical.Bytes, reflectedReadWrite.Canonical.Bytes);
    }

    [Fact]
    public void Atomic_operation_is_preserved_in_the_frozen_contract_and_canonical_identity()
    {
        using NullDevice device = new(new NullOptions());
        FrozenGraph none = FreezeStorageShader(
            device,
            ReflectedAccess.ReadWrite,
            DeclaredEffect.ReadWrite);
        FrozenGraph atomic = FreezeStorageShader(
            device,
            ReflectedAccess.ReadWrite,
            DeclaredEffect.ReadWrite,
            DeclaredOperations.Atomic);

        Assert.Equal(
            DeclaredOperations.Atomic,
            Assert.Single(atomic.Passes[0].Shaders[0].Bindings).DeclaredOperations);
        Assert.False(none.Canonical.Equals(atomic.Canonical));
    }

    [Fact]
    public void Unsupported_operation_qualifiers_fail_closed_before_shader_mapping()
    {
        GraphRecording recording = new();
        int pass = recording.AddPass("unsupported-operation", QueueSelection.Compute);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.StorageBuffer,
                1,
                ShaderStage.Compute,
                ReflectedAccess.ReadWrite,
                DeclaredEffect.Write,
                DeclaredOperations: DeclaredOperations.Append)],
            ShaderStage.Compute);

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            recording.AddShader(pass, shader, ReadOnlySpan<ShaderBindingAccess>.Empty));
        Assert.Contains("Append", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_binding_shader_use_requires_no_mapping()
    {
        using NullDevice device = new(new NullOptions());
        GraphRecording recording = new();
        int pass = recording.AddPass("zero-bindings", QueueSelection.Graphics);
        ShaderDesc shader = Shader(Array.Empty<ShaderBinding>(), ShaderStage.Vertex);

        recording.AddShader(pass, shader, ReadOnlySpan<ShaderBindingAccess>.Empty);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        FrozenGraph frozen = recording.Freeze(device);

        Assert.Single(frozen.Passes[0].Shaders);
        Assert.Empty(frozen.Passes[0].Shaders[0].Bindings);
        Assert.Empty(frozen.Passes[0].Shaders[0].Accesses);
    }

    [Fact]
    public void Descriptor_array_requires_an_explicit_mapping_for_every_element()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferViewId view = CreateStorageBufferView(ref builder);
        PassBuilder pass = builder.AddPass("array-coverage", QueueSelection.Compute);
        BufferViewAccess access = pass.Write(view);
        ShaderBindingAccess element0 = pass.MapShaderBinding(0, 7, access, element: 0);
        ShaderDesc shader = Shader(
            [new ShaderBinding(0, 7, BindingKind.StorageBuffer, 2, ShaderStage.Compute, ReflectedAccess.WriteOnly, DeclaredEffect.Write)],
            ShaderStage.Compute);

        InvalidOperationException error = CaptureUsesShader(ref pass, shader, element0);
        Assert.Contains("requires 2 descriptor element mappings", error.Message, StringComparison.Ordinal);
        builder.Dispose();
    }

    [Fact]
    public void Graph_access_must_cover_the_resolved_shader_effect()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferViewId view = CreateStorageBufferView(ref builder);
        PassBuilder pass = builder.AddPass("effect-mismatch", QueueSelection.Compute);
        BufferViewAccess writeOnlyGraphAccess = pass.Write(view);
        ShaderBindingAccess mapping = pass.MapShaderBinding(0, 0, writeOnlyGraphAccess);
        ShaderDesc shader = Shader(
            [new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Unspecified)],
            ShaderStage.Compute);

        InvalidOperationException error = CaptureUsesShader(ref pass, shader, mapping);
        Assert.Contains("does not conservatively cover", error.Message, StringComparison.Ordinal);
        builder.Dispose();
    }

    [Fact]
    public void Declared_effect_must_fit_reflected_and_type_capability()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        TextureId texture = builder.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled));
        TextureViewId view = builder.CreateTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource);
        PassBuilder pass = builder.AddPass("declared-effect-mismatch", QueueSelection.Graphics);
        TextureViewAccess read = pass.Read(view);
        ShaderBindingAccess mapping = pass.MapShaderBinding(2, 3, read);
        ShaderDesc shader = Shader(
            [new ShaderBinding(2, 3, BindingKind.SampledTexture, 1, ShaderStage.Pixel, ReflectedAccess.ReadOnly, DeclaredEffect.Write)],
            ShaderStage.Pixel);

        InvalidOperationException error = CaptureUsesShader(ref pass, shader, mapping);
        Assert.Contains("declared effect Write exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
        builder.Dispose();
    }

    [Fact]
    public void Shader_binding_kind_must_match_the_exact_graph_view_kind()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferViewId view = CreateStorageBufferView(ref builder);
        PassBuilder pass = builder.AddPass("kind-mismatch", QueueSelection.Compute);
        BufferViewAccess access = pass.Read(view);
        ShaderBindingAccess mapping = pass.MapShaderBinding(0, 0, access);
        ShaderDesc shader = Shader(
            [new ShaderBinding(0, 0, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read)],
            ShaderStage.Compute);

        InvalidOperationException error = CaptureUsesShader(ref pass, shader, mapping);
        Assert.Contains("does not match shader binding", error.Message, StringComparison.Ordinal);
        builder.Dispose();
    }

    [Fact]
    public void Externally_managed_marker_is_restricted_to_resolved_read_only_bindings()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass("external-write", QueueSelection.Compute);
        ShaderBindingAccess external = pass.MapExternallyManagedShaderBinding(0, 0);
        ShaderDesc shader = Shader(
            [new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write)],
            ShaderStage.Compute);

        InvalidOperationException error = CaptureUsesShader(ref pass, shader, external);
        Assert.Contains("only resolved read-only", error.Message, StringComparison.Ordinal);
        builder.Dispose();
    }

    [Fact]
    public void Mapping_rejects_an_access_token_from_another_pass()
    {
        using NullDevice device = new(new NullOptions());
        using RenderGraph graph = new(device);
        GraphBuilder builder = graph.Begin();
        BufferViewId view = CreateStorageBufferView(ref builder);
        PassBuilder producer = builder.AddPass("producer", QueueSelection.Compute);
        BufferViewAccess producerAccess = producer.Write(view);
        PassBuilder consumer = builder.AddPass("consumer", QueueSelection.Compute);

        ArgumentException? error = null;
        try
        {
            _ = consumer.MapShaderBinding(0, 0, producerAccess);
        }
        catch (ArgumentException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Contains("declared by this pass", error.Message, StringComparison.Ordinal);
        builder.Dispose();
    }

    [Fact]
    public void Frozen_contract_and_canonical_preserve_texture_abi_shape_fields()
    {
        using NullDevice device = new(new NullOptions());
        FrozenGraph exact = FreezeSampledTextureShader(
            device,
            ShaderTextureDimension.Texture2DArray,
            TextureSampleType.Float);
        FrozenGraph unknown = FreezeSampledTextureShader(
            device,
            ShaderTextureDimension.Unknown,
            TextureSampleType.Unknown);

        ShaderBinding binding = Assert.Single(exact.Passes[0].Shaders[0].Bindings);
        Assert.Equal(ShaderTextureDimension.Texture2DArray, binding.TextureDimension);
        Assert.Equal(TextureSampleType.Float, binding.TextureSampleType);
        Assert.Equal(Format.Unknown, binding.StorageFormat);
        Assert.False(exact.Canonical.Equals(unknown.Canonical));
    }

    [Fact]
    public void Texture_mapping_rejects_dimension_and_sample_type_mismatches_but_unknown_skips_only_that_check()
    {
        using NullDevice device = new(new NullOptions());

        InvalidOperationException dimension = Assert.Throws<InvalidOperationException>(() =>
            FreezeSampledTextureShader(device, ShaderTextureDimension.Cube, TextureSampleType.Float));
        Assert.Contains("dimension", dimension.Message, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException sample = Assert.Throws<InvalidOperationException>(() =>
            FreezeSampledTextureShader(device, ShaderTextureDimension.Texture2DArray, TextureSampleType.UInt));
        Assert.Contains("sample type", sample.Message, StringComparison.OrdinalIgnoreCase);

        FrozenGraph partiallyUnknown = FreezeSampledTextureShader(
            device,
            ShaderTextureDimension.Unknown,
            TextureSampleType.Float);
        Assert.Single(partiallyUnknown.Passes[0].Shaders);
    }

    [Fact]
    public void Ordinary_float_texture_declaration_accepts_a_depth_srv()
    {
        using NullDevice device = new(new NullOptions());
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(4, 4, Format.D32Float, TextureUsage.Sampled | TextureUsage.DepthStencilAttachment),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Depth),
            TextureViewUsage.ShaderResource,
            Format.Unknown,
            null,
            TextureViewDimension.Texture2D);
        int pass = recording.AddPass("depth-float-srv", QueueSelection.Graphics);
        TextureViewAccess access = recording.AddTextureViewAccess(
            pass,
            view,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        ShaderBindingAccess mapping = recording.AddShaderBindingAccess(pass, 0, 0, 0, access);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.SampledTexture,
                1,
                ShaderStage.Pixel,
                ReflectedAccess.ReadOnly,
                DeclaredEffect.Read,
                ShaderTextureDimension.Texture2D,
                TextureSampleType.Float)],
            ShaderStage.Pixel);

        recording.AddShader(pass, shader, [mapping]);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        FrozenGraph frozen = recording.Freeze(device);
        Assert.Single(frozen.Passes[0].Shaders);
    }

    [Fact]
    public void Storage_texture_mapping_requires_known_storage_format_to_match_exactly()
    {
        using NullDevice device = new(new NullOptions());
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(4, 4, Format.R8G8B8A8UNorm, TextureUsage.Storage),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.Storage,
            Format.Unknown,
            null,
            TextureViewDimension.Texture2D);
        int pass = recording.AddPass("storage-format", QueueSelection.Compute);
        TextureViewAccess access = recording.AddTextureViewAccess(
            pass,
            view,
            ResourceEffect.Write,
            PriorContents.Discard,
            WriteCoverage.Full);
        ShaderBindingAccess mapping = recording.AddShaderBindingAccess(pass, 0, 0, 0, access);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.StorageTexture,
                1,
                ShaderStage.Compute,
                ReflectedAccess.WriteOnly,
                DeclaredEffect.Write,
                ShaderTextureDimension.Texture2D,
                TextureSampleType.Float,
                Format.R32Float)],
            ShaderStage.Compute);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            recording.AddShader(pass, shader, [mapping]));
        Assert.Contains("storage format", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_texture_binding_rejects_texture_shape_fields()
    {
        using NullDevice device = new(new NullOptions());
        GraphRecording recording = new();
        int pass = recording.AddPass("invalid-buffer-shape", QueueSelection.Compute);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.ReadOnlyBuffer,
                1,
                ShaderStage.Compute,
                ReflectedAccess.ReadOnly,
                DeclaredEffect.Read,
                ShaderTextureDimension.Texture2D)],
            ShaderStage.Compute);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            recording.AddShader(pass, shader, ReadOnlySpan<ShaderBindingAccess>.Empty));
        Assert.Contains("Non-texture", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FrozenGraph FreezeStorageShader(
        IDevice device,
        ReflectedAccess reflected,
        DeclaredEffect declared,
        DeclaredOperations operations = DeclaredOperations.None)
    {
        GraphRecording recording = new();
        BufferId buffer = recording.AddBuffer(new BufferDesc(64, BufferUsage.ShaderWrite), default);
        BufferViewId view = recording.AddBufferView(
            buffer,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Format.Unknown,
            16,
            null);
        int pass = recording.AddPass("preserve-effects", QueueSelection.Compute);
        bool readWrite = declared == DeclaredEffect.ReadWrite || operations == DeclaredOperations.Atomic;
        BufferViewAccess access = recording.AddBufferViewAccess(
            pass,
            view,
            readWrite ? ResourceEffect.ReadWrite : ResourceEffect.Write,
            readWrite ? PriorContents.Required : PriorContents.Discard,
            readWrite ? WriteCoverage.Partial : WriteCoverage.Full);
        ShaderBindingAccess mapping = recording.AddShaderBindingAccess(pass, 0, 0, 0, access);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.StorageBuffer,
                1,
                ShaderStage.Compute,
                reflected,
                declared,
                DeclaredOperations: operations)],
            ShaderStage.Compute);
        recording.AddShader(pass, shader, [mapping]);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        return recording.Freeze(device);
    }

    private static BufferViewId CreateStorageBufferView(ref GraphBuilder builder)
    {
        BufferId buffer = builder.CreateBuffer(new BufferDesc(64, BufferUsage.ShaderWrite));
        return builder.CreateBufferView(buffer, BufferRange.Whole, BindingKind.StorageBuffer, stride: 16);
    }

    private static FrozenGraph FreezeSampledTextureShader(
        IDevice device,
        ShaderTextureDimension dimension,
        TextureSampleType sampleType)
    {
        GraphRecording recording = new();
        TextureId texture = recording.AddTexture(
            new TextureDesc(
                4,
                4,
                Format.R8G8B8A8UNorm,
                TextureUsage.Sampled,
                ArrayLayers: 2),
            default);
        TextureViewId view = recording.AddTextureView(
            texture,
            new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
            TextureViewUsage.ShaderResource,
            Format.Unknown,
            null,
            TextureViewDimension.Texture2DArray);
        int pass = recording.AddPass("sampled-shape", QueueSelection.Graphics);
        TextureViewAccess access = recording.AddTextureViewAccess(
            pass,
            view,
            ResourceEffect.Read,
            PriorContents.Required,
            WriteCoverage.Partial);
        ShaderBindingAccess mapping = recording.AddShaderBindingAccess(pass, 0, 0, 0, access);
        ShaderDesc shader = Shader(
            [new ShaderBinding(
                0,
                0,
                BindingKind.SampledTexture,
                1,
                ShaderStage.Pixel,
                ReflectedAccess.ReadOnly,
                DeclaredEffect.Read,
                dimension,
                sampleType)],
            ShaderStage.Pixel);
        recording.AddShader(pass, shader, [mapping]);
        recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });
        return recording.Freeze(device);
    }

    private static ShaderDesc Shader(ShaderBinding[] bindings, ShaderStage stage) => new(
        Key,
        ShaderBinaryFormat.Dxil,
        stage,
        "Main",
        new byte[] { 1, 2, 3, 4 },
        new ShaderInterface(bindings, Array.Empty<PushConstantRange>(), 0x0102_0304_0506_0708UL),
        "contract-test");

    private static InvalidOperationException CaptureUsesShader(
        ref PassBuilder pass,
        in ShaderDesc shader,
        ShaderBindingAccess mapping)
    {
        try
        {
            ShaderBindingAccess[] mappings = [mapping];
            pass.UsesShader(shader, mappings);
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }
        throw new Xunit.Sdk.XunitException("Expected InvalidOperationException.");
    }
}
