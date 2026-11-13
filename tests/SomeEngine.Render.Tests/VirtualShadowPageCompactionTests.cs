using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[Collection(VirtualShadowGpuCollection.Name)]
public sealed class VirtualShadowPageCompactionTests
{
    [Fact]
    public async Task MultipleGroupsKeepCachedPageOrderAndAppendEmptyPages()
    {
        const uint capacity = 513;
        using var graphics = new BinningTestDevice();
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(slang);
        string directory = ShaderDirectory();
        settings.SearchPaths = [directory];
        using var shaders = new ShaderLibrary(slang, settings);
        Shader scan = await shaders.CreateAsync("Available page scan", "screen_binning.slang",
            File.ReadAllText(Path.Combine(directory, "screen_binning.slang")));
        Shader available = await shaders.CreateAsync("Available shadow pages", "vsm_available_pages.slang",
            File.ReadAllText(Path.Combine(directory, "vsm_available_pages.slang")));
        Shader inputs = await shaders.CreateAsync("Shadow page order input", "shadow_page_order_input.slang", """
            __exported import screen_binning;
            #include "vsm_page_lists.slang"
            RWStructuredBuffer<uint> CompactedPages;
            [shader("compute")][numthreads(256,1,1)]
            void InitializeShadowPageOrder(uint3 id:SV_DispatchThreadID)
            {
                uint index=ScreenBinningThreadIndex(id);
                uint capacity=513u;
                if(index>capacity) return;
                for(uint list=0u;list<VSM_PAGE_LIST_COUNT;list++)
                    ShadowPageLists[ShadowPageListStart(list,capacity)+index]=index==capacity?0u:0xffffffffu;
                uint emptyCount=(capacity+4u)/5u;
                if(index<capacity)
                    ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_LRU,capacity)+index]=
                        index%5u==0u?0xffffffffu:index*37u%capacity;
                if(index<emptyCount)
                    ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_EMPTY,capacity)+index]=
                        (emptyCount-1u-index)*5u*37u%capacity;
                if(index==capacity)
                    ShadowPageLists[ShadowPageListCountIndex(VSM_PAGE_LIST_EMPTY,capacity)]=emptyCount;
            }
            [shader("compute")][numthreads(256,1,1)]
            void ReadAvailablePageOrder(uint3 id:SV_DispatchThreadID)
            {
                uint index=ScreenBinningThreadIndex(id);
                uint capacity=513u;
                if(index>capacity) return;
                uint count=ShadowPageListCount(VSM_PAGE_LIST_AVAILABLE,capacity);
                CompactedPages[index]=index==capacity?count:index<count?
                    ShadowPageLists[ShadowPageListStart(VSM_PAGE_LIST_AVAILABLE,capacity)+index]:0xffffffffu;
            }
            """);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        LinkedComputePipeline initialize = LinkedComputePipeline.Create(graphics.Pipelines, inputs,
            "InitializeShadowPageOrder", "Initialize cached and empty page order");
        LinkedComputePipeline copy = LinkedComputePipeline.Create(graphics.Pipelines, inputs,
            "ReadAvailablePageOrder", "Read valid available shadow pages");
        var output = new UIntReadback();
        var emptyCountOutput = new UIntReadback();
        GraphBufferId pageLists, readPages;
        GraphBufferUavId listsView, readPagesView;
        GraphPassId initializePass, copyPass;
        GraphReadbackId availableReadback, emptyReadback;
        VirtualShadowPageCompactionResources compact;
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            pageLists = edit.CreatePersistentBuffer(new BufferDesc(4ul * (capacity + 1u) * sizeof(uint),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource, "Shadow page order lists"));
            listsView = edit.CreateBufferUav(pageLists, structureStride: sizeof(uint));
            readPages = edit.CreatePersistentBuffer(new BufferDesc((capacity + 1ul) * 4u,
                BufferUsages.ShaderWrite | BufferUsages.CopySource, "Read valid available pages"));
            readPagesView = edit.CreateBufferUav(readPages, structureStride: sizeof(uint));
            GraphParameterResourceBinding[] bindings = ShaderEntryBindings.Resolve(inputs, initialize,
                (name, _) => name == "ShadowPageLists" ? GraphParameterResourceBinding.WritableBufferDescriptor(listsView) : null);
            initializePass = edit.AddComputeFramePass("Initialize page request order", PassQueueSelection.AnyCompatible(), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref ComputePassCommandScope commands, in int _) => commands.Dispatch(new(3, 1, 1)));
            PassDefinition pass = edit.GetPass(initializePass);
            pass.SetPipeline(initialize.Pipeline);
            pass.SetParameterBlock(initialize.ParameterLayout, new byte[16]);
            foreach (GraphParameterResourceBinding binding in bindings) pass.Bind(binding);
            pass.Write(pageLists, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            compact = VirtualShadowPageCompactionPasses.Add(ref edit, graphics.Pipelines, scan, available,
                pageLists, capacity, initializePass);
            GraphParameterResourceBinding[] copyBindings = ShaderEntryBindings.Resolve(inputs, copy,
                (name, _) => name switch
                {
                    "ShadowPageLists" => GraphParameterResourceBinding.WritableBufferDescriptor(listsView),
                    "CompactedPages" => GraphParameterResourceBinding.WritableBufferDescriptor(readPagesView),
                    _ => null,
                });
            copyPass = edit.AddComputeFramePass("Read valid compacted pages", PassQueueSelection.AnyCompatible(), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref ComputePassCommandScope commands, in int _) => commands.Dispatch(new(3, 1, 1)));
            PassDefinition reader = edit.GetPass(copyPass);
            reader.SetPipeline(copy.Pipeline);
            reader.SetParameterBlock(copy.ParameterLayout, new byte[16]);
            foreach (GraphParameterResourceBinding binding in copyBindings) reader.Bind(binding);
            reader.Read(pageLists, new((capacity + 1ul) * 4u, capacity * 4ul), PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess, contentScope: BufferContentScope.CountBoundedPrefix);
            reader.Read(pageLists, new(((capacity + 1ul) + capacity) * 4u, 4), PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            reader.Write(readPages, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            edit.OrderAfter(copyPass, compact.CompletePass);
            availableReadback = edit.AddReadback(readPages, new(0, (capacity + 1ul) * 4u),
                output, "Read compacted available shadow pages");
            emptyReadback = edit.AddReadback(pageLists, new((2ul * (capacity + 1u) + capacity) * 4u, 4),
                emptyCountOutput, "Read consumed empty page count");
            edit.OrderAfter(availableReadback, copyPass);
            edit.OrderAfter(emptyReadback, compact.CompletePass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
        {
            ulong identity;
            RenderGraphFrame frame = graph.BeginFrame();
            try
            {
                VirtualShadowPageCompactionPasses.SetFrameParameters(ref frame, compact, capacity);
                identity = frame.FrameIdentity;
                frame.Execute(new QueueCompletion[graph.MaximumQueueCompletionCount]);
            }
            finally { frame.Dispose(); }
            Assert.Equal(WaitStatus.Completed, graph.WaitForFrame(identity, TimeSpan.FromSeconds(10)));

            uint[] order = Enumerable.Range(0, (int)capacity).Select(index => (uint)index).ToArray();
            uint[] expected = order.Where(index => index % 5u != 0u).Select(index => index * 37u % capacity)
                .Concat(order.Where(index => index % 5u == 0u).Reverse().Select(index => index * 37u % capacity)).ToArray();
            Assert.Equal(expected, output.Values.AsSpan(0, (int)capacity).ToArray());
            Assert.Equal(capacity, output.Values[capacity]);
            Assert.Equal(new uint[] { 0 }, emptyCountOutput.Values);
        }
        {
            edit = graph.BeginEdit();
            try
            {
                edit.Remove(availableReadback);
                edit.Remove(emptyReadback);
                edit.Remove(copyPass);
                VirtualShadowPageCompactionPasses.Remove(ref edit, compact);
                edit.Remove(initializePass);
                edit.Remove(listsView);
                edit.Remove(readPagesView);
                edit.Remove(pageLists);
                edit.Remove(readPages);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
    }

    private static string ShaderDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "assets", "Shaders", "vsm_available_pages.slang")))
            directory = directory.Parent!;
        return Path.Combine(directory.FullName, "assets", "Shaders");
    }

    private sealed class UIntReadback : IGraphReadbackReceiver
    {
        internal uint[] Values = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Values = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }
}
