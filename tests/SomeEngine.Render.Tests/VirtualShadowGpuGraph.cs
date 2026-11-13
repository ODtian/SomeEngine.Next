using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

[CollectionDefinition(VirtualShadowGpuCollection.Name,DisableParallelization = true)]
public sealed class VirtualShadowGpuCollection : ICollectionFixture<VirtualShadowGpuResources>
{
    public const string Name = "Virtual shadow GPU";
}

public sealed class VirtualShadowGpuResources : IDisposable
{
    internal BinningTestDevice Graphics { get; } = new();
    internal ShaderLibrary Shaders { get; }

    public VirtualShadowGpuResources()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,"AGENTS.md"))) root = root.Parent;
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        SessionDesc settings = TestShaderLoading.CreateSessionDescription(global);
        settings.SearchPaths = [Path.Combine(root!.FullName,"assets","Shaders")];
        Shaders = new(global,settings);
    }

    public void Dispose()
    {
        Shaders.Dispose();
        Graphics.Dispose();
    }
}

internal sealed class VirtualShadowGpuGraph : IDisposable
{
    private delegate void FrameWrite(ref RenderGraphFrame frame);
    private readonly List<FrameWrite> _writes = [];
    private readonly VirtualShadowGpuResources _resources;
    internal BinningTestDevice Graphics => _resources.Graphics;
    internal ShaderLibrary Shaders => _resources.Shaders;
    internal global::SomeEngine.RenderGraph.RenderGraph Graph { get; }

    internal VirtualShadowGpuGraph(VirtualShadowGpuResources resources)
    {
        _resources = resources;
        Graph = new(Graphics.Backend,Graphics.Device,[Graphics.Backend.GetQueue(Graphics.Device,QueueType.Graphics)]);
    }

    internal Shader Load(string name,string source) => Shaders.CreateAsync(name,name + ".slang",source)
        .AsTask().GetAwaiter().GetResult();

    internal GraphBufferSrvId Input<T>(ref RenderGraphEdit edit,T[] values) where T : unmanaged
    {
        GraphBufferInput input = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _writes.Add((ref RenderGraphFrame frame) => frame.SetBufferInput(input,
            frame.Upload<T>(values,BufferUsages.ShaderRead)));
        return edit.CreateBufferSrv(input,structureStride:(uint)Marshal.SizeOf<T>());
    }

    internal GraphBufferCbvId Constants<T>(ref RenderGraphEdit edit,T value) where T : unmanaged
    {
        uint alignment = Graphics.Device.Capabilities.Limits.ConstantBufferAlignment;
        uint bytes = ((uint)Marshal.SizeOf<T>() + alignment - 1u) / alignment * alignment;
        GraphBufferId buffer = edit.CreatePersistentBuffer(new(bytes,BufferUsages.Constant),MemoryType.Upload);
        _writes.Add((ref RenderGraphFrame frame) => MemoryMarshal.Write(frame.GetBufferDataWriteSpan(buffer),in value));
        return edit.CreateBufferCbv(buffer);
    }

    internal static GraphBufferId Buffer<T>(ref RenderGraphEdit edit,uint count) where T : unmanaged =>
        edit.CreatePersistentBuffer(new((ulong)count * (uint)Marshal.SizeOf<T>(),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination));

    internal GraphBufferId Data<T>(ref RenderGraphEdit edit,T[] values) where T : unmanaged
    {
        GraphBufferId buffer = Buffer<T>(ref edit,(uint)values.Length);
        edit.AddBufferUploadPass(buffer);
        _writes.Add((ref RenderGraphFrame frame) => frame.WriteBuffer(buffer,0,MemoryMarshal.AsBytes(values.AsSpan())));
        return buffer;
    }

    internal GraphTextureId Image(ref RenderGraphEdit edit,uint width,uint height,uint layers,uint[] pixels,Format format = Format.R32UInt)
    {
        uint pitch = (width * 4u + 255u) / 256u * 256u;
        byte[] bytes = new byte[pitch * height * layers];
        for (uint row = 0; row < height * layers; row++)
            MemoryMarshal.AsBytes(pixels.AsSpan((int)(row * width),(int)width)).CopyTo(bytes.AsSpan((int)(row * pitch)));
        GraphBufferId input = edit.CreatePersistentBuffer(new((ulong)bytes.Length,BufferUsages.CopySource | BufferUsages.CopyDestination));
        edit.AddBufferUploadPass(input);
        _writes.Add((ref RenderGraphFrame frame) => frame.WriteBuffer(input,0,bytes));
        GraphTextureId image = edit.CreatePersistentTexture(new(TextureDimension.Texture2D,width,height,1,1,layers,1,
            format,TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopyDestination));
        edit.AddCopyFramePass("Upload shadow image",PassQueueSelection.AnyOfType(QueueType.Graphics),
            (Input:input,Image:image,Width:width,Height:height,Layers:layers,Pitch:pitch),
            new PassOptions(Culling:PassCullingMode.NeverCull),
            static(ref PassDefinition pass,ref (GraphBufferId Input,GraphTextureId Image,uint Width,uint Height,uint Layers,uint Pitch) data) =>
            {
                pass.Read(data.Input,BufferRange.Whole,PipelineSync.Copy,ResourceAccess.CopySource);
                pass.Write(data.Image,new(0,1,0,data.Layers,TextureAspects.Color),PipelineSync.Copy,
                    ResourceAccess.CopyDestination,TextureLayout.CopyDestination,WriteCoverage.Complete);
            },
            static(ref CopyPassCommandScope commands,in (GraphBufferId Input,GraphTextureId Image,uint Width,uint Height,uint Layers,uint Pitch) data) =>
            {
                for (uint layer = 0; layer < data.Layers; layer++)
                    commands.CopyBufferToTexture(new(commands.GetBuffer(data.Input),(ulong)layer * data.Pitch * data.Height,
                        data.Pitch,data.Height,commands.GetTexture(data.Image),0,layer,TextureAspects.Color,
                        0,0,0,data.Width,data.Height,1));
            });
        return image;
    }

    internal GraphPassId Add(ref RenderGraphEdit edit,Shader shader,string entry,DispatchArguments dispatch,
        IReadOnlyDictionary<string,GraphParameterResourceBinding> resources,GraphPassId previous = default)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(Graphics.Pipelines,shader,entry,entry);
        GraphPassId id = edit.AddComputeFramePass(entry,PassQueueSelection.AnyCompatible(),dispatch,
            new PassOptions(Culling:PassCullingMode.NeverCull),
            static(ref PassDefinition _,ref DispatchArguments _) => {},
            static(ref ComputePassCommandScope commands,in DispatchArguments args) => commands.Dispatch(args));
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader,pipeline,
            (name,_) => resources.TryGetValue(name,out GraphParameterResourceBinding value) ? value : null)) pass.Bind(binding);
        if (previous != default) edit.OrderAfter(id,previous);
        return id;
    }

    internal Readback<T> Read<T>(ref RenderGraphEdit edit,GraphBufferId buffer,uint count,GraphPassId previous)
        where T : unmanaged
    {
        var result = new Readback<T>();
        GraphReadbackId read = edit.AddReadback(buffer,new(0,(ulong)count * (uint)Marshal.SizeOf<T>()),result,"Shadow behavior result");
        edit.OrderAfter(read,previous);
        return result;
    }

    internal void Run()
    {
        RenderGraphFrame frame = Graph.BeginFrame();
        ulong identity;
        try
        {
            foreach (FrameWrite write in _writes) write(ref frame);
            identity = frame.FrameIdentity;
            frame.Execute(new QueueCompletion[Graph.MaximumQueueCompletionCount]);
        }
        finally { frame.Dispose(); }
        Assert.Equal(WaitStatus.Completed,Graph.WaitForFrame(identity,TimeSpan.FromSeconds(20)));
    }

    public void Dispose() => Graph.Dispose();

    internal sealed class Readback<T> : IGraphReadbackReceiver where T : unmanaged
    {
        internal T[] Values { get; private set; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes,ulong tag) => Values = MemoryMarshal.Cast<byte,T>(bytes).ToArray();
    }
}
