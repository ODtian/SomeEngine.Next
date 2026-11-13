using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class ScreenBinningScanTests
{
    [Fact]
    public async Task MultipleGroupsExposeEachExclusivePrefixAndTheTotal()
    {
        const uint valueCount = 513;
        using var graphics = new BinningTestDevice();
        using var shaders = CreateShaders();
        Shader sharedShader = await shaders.CreateAsync("Shared screen binning", "screen_binning.slang",
            File.ReadAllText(Path.Combine(ShaderDirectory(), "screen_binning.slang")));
        Shader shader = await shaders.CreateAsync("Prefix scan input", "prefix_scan_input.slang", """
            __exported import screen_binning;
            [shader("compute")][numthreads(256,1,1)]
            void InitializeScanValues(uint3 thread : SV_DispatchThreadID)
            {
                uint index = ScreenBinningThreadIndex(thread);
                if (index <= 513u)
                    ScreenBinningScanValues[index] = index == 513u ? 0u : index % 7u + 1u;
            }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var initialize = LinkedComputePipeline.Create(graphics.Pipelines, shader,
            "InitializeScanValues", "Initialize prefix values");
        ScreenBinningScanResources scan;
        var output = new UIntReadback();
        GraphBufferId values;
        GraphBufferUavId valuesView;
        GraphPassId initializePass;
        GraphReadbackId readback;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            values = edit.CreatePersistentBuffer(new BufferDesc((valueCount + 1u) * 4u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Prefix values"));
            valuesView = edit.CreateBufferUav(values, structureStride: 4);
            initializePass = AddCompute(ref edit, shader, initialize, new(3, 1, 1), (name, type) => name == "ScreenBinningScanValues"
                ? GraphParameterResourceBinding.WritableBufferDescriptor(valuesView) : GraphParameterResourceBinding.Null(type));
            PassDefinition initializer = edit.GetPass(initializePass);
            initializer.Write(values, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            ScreenBinningScanResources scanResources = ScreenBinningScanPasses.Add(ref edit, graphics.Pipelines, sharedShader,
                values, valueCount + 1u, initializePass);
            readback = edit.AddReadback(values, new(0, (valueCount + 1u) * 4u), output, "Read prefix sums");
            edit.OrderAfter(readback, scanResources.CompletePass);
            edit.Commit();
            scan = scanResources;
        }
        finally { edit.Dispose(); }
        try
        {
            RenderGraphFrame frame = graph.BeginFrame();
            ulong frameNumber;
            try
            {
                frameNumber = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameNumber, TimeSpan.FromSeconds(10)));

            uint expected = 0;
            Assert.Equal((int)valueCount + 1, output.Values.Length);
            for (uint index = 0; index <= valueCount; index++)
            {
                Assert.Equal(expected, output.Values[index]);
                expected += index % 7u + 1u;
            }
        }
        finally
        {
            edit = graph.BeginEdit();
            try
            {
                edit.Remove(readback);
                edit.Remove(initializePass);
                ScreenBinningScanPasses.Remove(ref edit, scan);
                edit.Remove(valuesView);
                edit.Remove(values);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
    }

    [Fact]
    public async Task AvailableShadowPagesKeepRequestOrderAndAppendEmptyPages()
    {
        const uint capacity = 1024;
        using var graphics = new BinningTestDevice();
        using var shaders = CreateShaders();
        Shader sharedShader = await shaders.CreateAsync("Shared screen binning", "screen_binning.slang",
            File.ReadAllText(Path.Combine(ShaderDirectory(), "screen_binning.slang")));
        Shader shader = await shaders.CreateAsync("Available shadow pages", "available_shadow_pages_test.slang", """
            #include "vsm_available_pages.slang"
            [shader("compute")][numthreads(256,1,1)]
            void InitializePageLists(uint3 thread : SV_DispatchThreadID)
            {
                uint index = ScreenBinningThreadIndex(thread);
                uint capacity = AvailablePages.MaxPages;
                uint emptyCount = (capacity + 4u) / 5u;
                if (index > capacity) return;
                for (uint list = 0u; list < VSM_PAGE_LIST_COUNT; list++)
                    ShadowPageLists[ShadowPageListStart(list, capacity) + index] = index == capacity ? 0u : 0xffffffffu;
                if (index < capacity)
                    ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_LRU, capacity) + index] =
                        index % 5u == 0u ? 0xffffffffu : index * 37u % capacity;
                if (index < emptyCount)
                    ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_EMPTY, capacity) + index] =
                        (emptyCount - 1u - index) * 5u * 37u % capacity;
                if (index == capacity)
                    ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_EMPTY, capacity)] = emptyCount;
            }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        var initialize = LinkedComputePipeline.Create(graphics.Pipelines, shader, "InitializePageLists", "Initialize shadow page order");
        var count = LinkedComputePipeline.Create(graphics.Pipelines, shader, "CountAvailableShadowPages", "Count available shadow pages");
        var fill = LinkedComputePipeline.Create(graphics.Pipelines, shader, "FillAvailableShadowPages", "Fill available shadow pages");
        var output = new UIntReadback();
        var emptyCountOutput = new UIntReadback();
        GraphBufferId lists, prefixes, parameters;
        GraphBufferUavId listsView, prefixesView;
        GraphBufferCbvId parametersView;
        GraphPassId initializePass, countPass, fillPass;
        GraphReadbackId readback, emptyReadback;
        ScreenBinningScanResources scan;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            lists = edit.CreatePersistentBuffer(new BufferDesc(4u * (capacity + 1u) * 4u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Shadow page lists"));
            prefixes = edit.CreatePersistentBuffer(new BufferDesc((capacity + 2u) * 4u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Available page prefixes"));
            uint alignment = Math.Max(1u, graphics.Device.Capabilities.Limits.ConstantBufferAlignment);
            uint parameterSize = (16u + alignment - 1u) / alignment * alignment;
            parameters = edit.CreatePersistentBuffer(new BufferDesc(parameterSize, BufferUsages.Constant, "Available page count"), MemoryType.Upload);
            listsView = edit.CreateBufferUav(lists, structureStride: 4);
            prefixesView = edit.CreateBufferUav(prefixes, structureStride: 4);
            parametersView = edit.CreateBufferCbv(parameters);
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "ShadowPageLists" => GraphParameterResourceBinding.WritableBufferDescriptor(listsView),
                "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(prefixesView),
                "AvailablePages" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
                _ => GraphParameterResourceBinding.Null(type),
            };
            initializePass = AddCompute(ref edit, shader, initialize, new(5, 1, 1), Resolve);
            PassDefinition initializer = edit.GetPass(initializePass);
            initializer.Write(lists, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            countPass = AddCompute(ref edit, shader, count, new(5, 1, 1), Resolve);
            PassDefinition counter = edit.GetPass(countPass);
            counter.Read(lists, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            counter.Write(prefixes, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            edit.OrderAfter(countPass, initializePass);
            ScreenBinningScanResources scanResources = ScreenBinningScanPasses.Add(ref edit, graphics.Pipelines, sharedShader,
                prefixes, capacity + 1u, countPass, resolveResource: Resolve);
            fillPass = AddCompute(ref edit, shader, fill, new(5, 1, 1), Resolve);
            PassDefinition filler = edit.GetPass(fillPass);
            filler.Read(prefixes, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            filler.ReadWrite(lists, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            edit.OrderAfter(fillPass, scanResources.CompletePass);
            readback = edit.AddReadback(lists, new((capacity + 1u) * 4u, (capacity + 1u) * 4u), output, "Read available shadow pages");
            emptyReadback = edit.AddReadback(lists, new((2u * (capacity + 1u) + capacity) * 4u, 4), emptyCountOutput, "Read remaining empty page count");
            edit.OrderAfter(readback, fillPass);
            edit.OrderAfter(emptyReadback, fillPass);
            edit.Commit();
            scan = scanResources;
        }
        finally { edit.Dispose(); }
        try
        {
            RenderGraphFrame frame = graph.BeginFrame();
            ulong frameNumber;
            try
            {
                var settings = new AvailablePageParameters(capacity);
                MemoryMarshal.Write(frame.GetBufferDataWriteSpan(parameters), in settings);
                frameNumber = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(frameNumber, TimeSpan.FromSeconds(10)));

            uint[] previousOrder = Enumerable.Range(0, (int)capacity).Select(index => (uint)index).ToArray();
            uint[] expected = previousOrder.Where(index => index % 5u != 0u).Select(index => index * 37u % capacity)
                .Concat(previousOrder.Where(index => index % 5u == 0u).Reverse().Select(index => index * 37u % capacity)).ToArray();
            Assert.Equal(capacity, output.Values[capacity]);
            Assert.Equal(expected, output.Values.AsSpan(0, (int)capacity).ToArray());
            Assert.Equal(new[] { 0u }, emptyCountOutput.Values);
        }
        finally
        {
            edit = graph.BeginEdit();
            try
            {
                edit.Remove(readback);
                edit.Remove(emptyReadback);
                edit.Remove(fillPass);
                edit.Remove(countPass);
                edit.Remove(initializePass);
                ScreenBinningScanPasses.Remove(ref edit, scan);
                edit.Remove(listsView);
                edit.Remove(prefixesView);
                edit.Remove(parametersView);
                edit.Remove(lists);
                edit.Remove(prefixes);
                edit.Remove(parameters);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
    }

    private static GraphPassId AddCompute(ref RenderGraphEdit edit, Shader shader, LinkedComputePipeline pipeline,
        DispatchArguments dispatch, Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        GraphPassId id = edit.AddComputeFramePass(pipeline.EntryPoint.Name, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        var arguments = new uint[4];
        pass.SetParameterBlock(pipeline.ParameterLayout, MemoryMarshal.AsBytes(arguments.AsSpan()));
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline, resolve)) pass.Bind(binding);
        return id;
    }

    private static ShaderLibrary CreateShaders()
    {
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        settings.SearchPaths = [ShaderDirectory()];
        return new(slang, settings);
    }

    private static string ShaderDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "screen_binning.slang"))) directory = directory.Parent!;
        return Path.Combine(directory.FullName, "assets", "Shaders");
    }

    private sealed class UIntReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct AvailablePageParameters(uint MaxPhysicalPages);
}
