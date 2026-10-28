using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanMeshInterfaceRenderingTests(VulkanTestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(1, false, false, 0)]
    [InlineData(5, false, false, 0)]
    [InlineData(5, true, false, 0)]
    [InlineData(5, false, true, 0)]
    [InlineData(5, true, true, 0)]
    [InlineData(5, false, false, 2)]
    [InlineData(5, false, true, 2)]
    public void Mesh_primitive_colors_and_flat_vertex_colors_reach_their_fragment_inputs(
        int primitiveLocation, bool explicitPrimitiveDecoration, bool interfaceBlock, int primitiveComponent)
    {
        VulkanTestShader shader = shaderCompiler.Compile(Source(primitiveLocation),
            ("meshMain", SlangStage.Mesh), ("pixelMain", SlangStage.Fragment));
        byte[] mesh = shader.GetEntryCode(0).ToArray();
        byte[] fragment = shader.GetEntryCode(1).ToArray();
        if (primitiveComponent != 0)
        {
            mesh = PrimitiveComponent(mesh, true, (uint)primitiveComponent);
            fragment = PrimitiveComponent(fragment, false, (uint)primitiveComponent);
        }
        if (explicitPrimitiveDecoration) fragment = DecoratePrimitiveInput(fragment);
        if (interfaceBlock)
        {
            mesh = PrimitiveInterfaceBlock(mesh, true);
            fragment = PrimitiveInterfaceBlock(fragment, false);
        }
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default,
            [new DeviceQueueDesc(QueueType.Graphics)], requiredFeatures: DeviceFeatures.MeshShaders));
        using Pipeline pipeline = backend.CreateMeshPipeline(device, new MeshPipelineDesc(
            shader.SlangCode, shader.Reflection, shader.Entries[0], new ReadOnlySequence<byte>(mesh),
            EntryPointReflection.Null, default, shader.Entries[1], new ReadOnlySequence<byte>(fragment),
            new RasterizerState(Cull: CullType.None), new MultisampleState(), new DepthStencilState(),
            new BlendState([new BlendAttachmentState()]), new AttachmentFormatSignature([Format.R8G8B8A8UNorm], null),
            DynamicStates.Viewport | DynamicStates.Scissor));
        using Texture target = backend.CreateTexture(device, new TextureDesc(TextureDimension.Texture2D,
            8, 8, 1, 1, 1, 1, Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(device,
            new ColorAttachmentViewDesc(target, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(8 * 8 * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(target, range, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget, TextureLayout.Undefined, TextureLayout.RenderTarget));
        backend.BeginRendering(context, new RenderingDesc([new ColorAttachmentDesc(view, LoadType.Clear, StoreType.Store, default)], null, 8, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 8, 8)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 8, 8)]);
        backend.DispatchMesh(context, new DispatchArguments(1, 1, 1));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(target, range, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource, TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(readback, 0, 32, 8, target, 0, 0,
            TextureAspects.Color, 0, 0, 0, 8, 8, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        // Mesh primitive colors have four values, while the ordinary Flat input is
        // selected from the provoking vertex. They must not be assigned the same rate.
        // RHI viewports flip clip-space Y into top-down texture coordinates.
        Check(1, 6, 32, 64);
        Check(2, 1, 64, 128);
        Check(5, 6, 64, 191);
        Check(6, 1, 159, 255);
        void Check(int x, int y, int vertex, int primitive)
        {
            int offset = (y * 8 + x) * 4;
            Assert.InRange((int)result.Bytes[offset], vertex - 1, vertex + 1);
            Assert.InRange((int)result.Bytes[offset + 1], primitive - 1, Math.Min(primitive + 1, 255));
            Assert.Equal((byte)0, result.Bytes[offset + 2]);
            Assert.Equal((byte)255, result.Bytes[offset + 3]);
        }
    }

    internal static string Source(int primitiveLocation) => """
        struct VertexOutput
        {
            float4 Position : SV_Position;
            [[vk::location(0)]] nointerpolation float VertexColor : TEXCOORD0;
        };
        struct PrimitiveOutput
        {
            [[vk::location(PRIMITIVE_LOCATION)]] nointerpolation float Color : TEXCOORD1;
            [[vk::location(12)]] nointerpolation float UnusedColor : TEXCOORD2;
        };
        struct PrimitiveInput
        {
            [[vk::location(PRIMITIVE_LOCATION)]] nointerpolation float Color : TEXCOORD1;
        };
        [shader("mesh")]
        [outputtopology("triangle")]
        [numthreads(1, 1, 1)]
        void meshMain(out vertices VertexOutput vertices[6], out indices uint3 indices[4],
            out primitives PrimitiveOutput primitives[4])
        {
            SetMeshOutputCounts(6, 4);
            const float2 positions[6] = { float2(-1,-1), float2(0,-1), float2(-1,1),
                float2(0,1), float2(1,-1), float2(1,1) };
            for (uint index = 0; index < 6; index++)
            {
                vertices[index].Position = float4(positions[index], .5, 1);
                vertices[index].VertexColor = float(index + 1) / 8.0;
            }
            indices[0] = uint3(0, 1, 2);
            indices[1] = uint3(1, 3, 2);
            indices[2] = uint3(1, 4, 3);
            indices[3] = uint3(4, 5, 3);
            for (uint index = 0; index < 4; index++)
            {
                primitives[index].Color = float(index + 1) / 4.0;
                primitives[index].UnusedColor = 0.125;
            }
        }
        [shader("fragment")]
        float4 pixelMain(VertexOutput vertex, [[vk::perprimitive]] PrimitiveInput primitive) : SV_Target0
        {
            return float4(vertex.VertexColor, primitive.Color, 0, 1);
        }
        """.Replace("PRIMITIVE_LOCATION", primitiveLocation.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // Exercise Location + Component matching independently of Slang attribute support.
    internal static byte[] PrimitiveComponent(byte[] code, bool mesh, uint component)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(code).ToArray();
        uint variable = 0;
        int annotationEnd = -1;
        for (int index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            uint op = words[index] & 0xffff;
            int count = (int)(words[index] >> 16);
            if (op == 5 && Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words.AsSpan(index + 2, count - 2))).TrimEnd('\0') ==
                (mesh ? "primitives.Color" : "primitive.Color")) variable = words[index + 1];
            if (annotationEnd < 0 && op is >= 19 and <= 39) annotationEnd = index;
        }
        if (variable == 0 || annotationEnd < 0) throw new InvalidOperationException("Primitive fixture interface was not found.");
        var result = new List<uint>(words.Length + 4);
        result.AddRange(words.AsSpan(0, annotationEnd).ToArray());
        result.AddRange([(4u << 16) | 71, variable, 31, component]);
        result.AddRange(words.AsSpan(annotationEnd).ToArray());
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(result)).ToArray();
    }

    // Supply an already conformant SPIR-V fragment through the public pipeline API.
    // This fixture construction is intentionally independent of backend normalization.
    internal static byte[] DecoratePrimitiveInput(byte[] code)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(code).ToArray();
        var inputs = new HashSet<uint>();
        var locations = new Dictionary<uint, uint>();
        var primitiveInputs = new HashSet<uint>();
        bool capability = false, extensionPresent = false;
        for (int index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            uint op = words[index] & 0xffff;
            if (op == 59 && words[index + 3] == 1) inputs.Add(words[index + 2]);
            if (op == 71 && words[index + 2] == 30) locations[words[index + 1]] = words[index + 3];
            if (op == 71 && words[index + 2] == 5271) primitiveInputs.Add(words[index + 1]);
            if (op == 17 && words[index + 1] == 5283) capability = true;
            if (op == 10 && Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words.AsSpan(index + 1, (int)(words[index] >> 16) - 1))).TrimEnd('\0') ==
                "SPV_EXT_mesh_shader") extensionPresent = true;
        }
        uint id = inputs.Single(input => locations.TryGetValue(input, out uint location) && location != 0);
        if (primitiveInputs.Contains(id)) return code;
        var output = new List<uint>(words.Length + 12);
        output.AddRange(words.AsSpan(0, 5).ToArray());
        if (!capability) { output.Add((2u << 16) | 17); output.Add(5283); }
        int cursor = 5;
        while ((words[cursor] & 0xffff) == 17)
        {
            int count = (int)(words[cursor] >> 16);
            output.AddRange(words.AsSpan(cursor, count).ToArray()); cursor += count;
        }
        uint[] extension = MemoryMarshal.Cast<byte, uint>(Encoding.UTF8.GetBytes("SPV_EXT_mesh_shader\0")).ToArray();
        if (!extensionPresent)
        {
            output.Add(((uint)extension.Length + 1) << 16 | 10); output.AddRange(extension);
        }
        for (; cursor < words.Length;)
        {
            uint op = words[cursor] & 0xffff;
            if (op is >= 19 and <= 39) break;
            int count = (int)(words[cursor] >> 16);
            output.AddRange(words.AsSpan(cursor, count).ToArray()); cursor += count;
        }
        output.Add((3u << 16) | 71); output.Add(id); output.Add(5271);
        output.AddRange(words.AsSpan(cursor).ToArray());
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(output)).ToArray();
    }

    // Package the test shader's primitive scalar as a Block/member interface.
    // The function instructions keep the same values; spirv-val verifies the fixture.
    internal static byte[] PrimitiveInterfaceBlock(byte[] code, bool mesh)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(code).ToArray();
        var instructions = new List<uint[]>();
        for (int index = 5; index < words.Length; index += (int)(words[index] >> 16))
            instructions.Add(words.AsSpan(index, (int)(words[index] >> 16)).ToArray());
        uint variableId = instructions.Single(i => (i[0] & 0xffff) == 5 &&
            Encoding.UTF8.GetString(MemoryMarshal.AsBytes(i.AsSpan(2))).TrimEnd('\0') ==
            (mesh ? "primitives.Color" : "primitive.Color"))[1];
        uint[] variable = instructions.Single(i => (i[0] & 0xffff) == 59 && i[2] == variableId);
        uint originalPointer = variable[1], storage = variable[3];
        uint[] pointer = instructions.Single(i => (i[0] & 0xffff) == 32 && i[1] == originalPointer);
        uint valueType = pointer[3], arrayLength = 0;
        if (mesh)
        {
            uint[] array = instructions.Single(i => (i[0] & 0xffff) == 28 && i[1] == valueType);
            valueType = array[2]; arrayLength = array[3];
        }
        uint nextId = words[3], block = nextId++, blockPointer = nextId++;
        uint blockArray = mesh ? nextId++ : block;
        uint integerType = instructions.FirstOrDefault(i => (i[0] & 0xffff) == 21 && i[2] == 32 && i[3] == 0)?[1] ?? 0;
        bool newInteger = integerType == 0;
        if (newInteger) integerType = nextId++;
        uint zero = instructions.FirstOrDefault(i => (i[0] & 0xffff) == 43 && i.Length == 4 && i[1] == integerType && i[3] == 0)?[2] ?? 0;
        bool newZero = zero == 0;
        if (newZero) zero = nextId++;
        var result = new List<uint>(words.Length + 40);
        result.AddRange(words.AsSpan(0, 5).ToArray());
        bool blockDecorated = false;
        foreach (uint[] original in instructions)
        {
            uint op = original[0] & 0xffff;
            if (!blockDecorated && op is >= 19 and <= 39)
            {
                result.AddRange([(3u << 16) | 71, block, 2]); blockDecorated = true;
            }
            if (op == 71 && original[1] == variableId && original[2] is 30 or 31 or 14 or 5271)
            {
                result.Add(((uint)original.Length + 1) << 16 | 72);
                result.Add(block); result.Add(0);
                result.AddRange(original.AsSpan(2).ToArray()); continue;
            }
            if (op == 59 && original[2] == variableId)
            {
                if (newInteger) result.AddRange([(4u << 16) | 21, integerType, 32, 0]);
                if (newZero) result.AddRange([(4u << 16) | 43, integerType, zero, 0]);
                result.AddRange([(3u << 16) | 30, block, valueType]);
                if (mesh) result.AddRange([(4u << 16) | 28, blockArray, block, arrayLength]);
                result.AddRange([(4u << 16) | 32, blockPointer, storage, blockArray]);
                uint[] replaced = (uint[])original.Clone(); replaced[1] = blockPointer;
                result.AddRange(replaced); continue;
            }
            if (mesh && op is 65 or 66 && original[3] == variableId)
            {
                result.Add(((uint)original.Length + 1) << 16 | op);
                result.AddRange(original.AsSpan(1).ToArray()); result.Add(zero); continue;
            }
            if (!mesh && op == 61 && original[3] == variableId)
            {
                uint access = nextId++;
                result.AddRange([(5u << 16) | 65, originalPointer, access, variableId, zero]);
                uint[] replaced = (uint[])original.Clone(); replaced[3] = access;
                result.AddRange(replaced); continue;
            }
            result.AddRange(original);
        }
        result[3] = nextId;
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(result)).ToArray();
    }
}
