# 公共 API 复现源码

本文件为交付证据，不属于测试门禁。临时项目及编译产物已删除。将以下两段分别保存为独立目录的项目和源码即可复核；项目引用需指向 render 工作树的 src。使用 Release 普通生产程序集，无反射、内部 API 或 SOMEENGINE_TESTING。

``` xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../src/SomeEngine.RenderGraph/SomeEngine.RenderGraph.csproj" />
    <ProjectReference Include="../src/SomeEngine.Render.MaterialBindless/SomeEngine.Render.MaterialBindless.csproj" />
    <ProjectReference Include="../src/SomeEngine.Graphics.Direct3D12/SomeEngine.Graphics.Direct3D12.csproj" />
    <ProjectReference Include="../src/SomeEngine.Graphics.Vulkan/SomeEngine.Graphics.Vulkan.csproj" />
  </ItemGroup>
</Project>

```

``` csharp
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Render;
using SomeEngine.RenderGraph;
using RGraph = SomeEngine.RenderGraph.RenderGraph;

using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: true), [], out int adapterCount);
var adapters = new AdapterInfo[adapterCount];
backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: true), adapters, out _);
AdapterInfo warp = adapters.First(a => !a.HardwareAccelerated);
using Device device = backend.CreateDevice(new DeviceDesc(warp.Id, [new(QueueType.Graphics)]));
SomeEngine.Graphics.Queue queue = backend.GetQueue(device, QueueType.Graphics);
Run("frame output failure recovery", FrameOutput);
Run("copied edit after commit", CopiedEdit);
Run("old pass definition after frame reuse", StalePass);
Run("abandoned identity reuse", AbandonedIdentity);
Run("material upload cancel retry", MaterialCancel);
Run("texture upload failed execution retry", TextureFailure);
Run("Vulkan duplicate command recovery", VulkanDuplicate);

void Run(string name, Action action)
{
    Console.WriteLine("CHECK " + name);
    try { action(); } catch(Exception e) { Console.WriteLine("ERROR " + e); }
}
void Wait(ReadOnlySpan<QueueCompletion> completions)
{
    foreach(var c in completions)
        if(backend.WaitCpu(c, TimeSpan.FromSeconds(10)) != WaitStatus.Completed) throw new TimeoutException();
}
GraphPassId Empty(ref RenderGraphFrame frame, string name = "consumer") => frame.AddCopyPass(name,
    PassQueueSelection.Exact(queue), 0,
    new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
    static (ref PassDefinition p, ref int value) => {},
    static (ref CopyPassCommandScope c, in int value) => {});
void FrameOutput()
{
    var graph = new RGraph(backend, device, [queue]);
    var frame = graph.BeginFrame();
    try { frame.Execute([]); } catch(ArgumentException) { Console.WriteLine("small destination rejected"); }
    frame.Dispose();
    try { var next = graph.BeginFrame(); next.Dispose(); Console.WriteLine("RECOVERED"); }
    catch(InvalidOperationException e) { Console.WriteLine("REPRODUCED: " + e.Message); }
    try { graph.Dispose(); } catch(InvalidOperationException e) { Console.WriteLine("graph dispose blocked: " + e.Message); }
}
void CopiedEdit()
{
    using var graph = new RGraph(backend, device, [queue]);
    var edit = graph.BeginEdit();
    var pass = edit.AddCopyFramePass("before", PassQueueSelection.Exact(queue), 0,
        new PassOptions(Culling: PassCullingMode.NeverCull),
        static(ref PassDefinition p, ref int x) => {},
        static(ref CopyPassCommandScope c, in int x) => {});
    var old = edit;
    edit.Commit();
    ulong version = graph.StructureVersion;
    old.SetPassLabel(pass, "mutated through closed edit");
    using var frame = graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Observe));
    var completions = new QueueCompletion[1];
    int count=frame.Execute(completions); Wait(completions.AsSpan(0,count));
    Console.WriteLine("version unchanged=" + (version == graph.StructureVersion));
    old.Dispose();
    void Observe(in RenderGraphDiagnosticsView v) { foreach(var p in v.Passes) Console.WriteLine("observed label=" + p.Label); }
}
void AbandonedIdentity()
{
    using var graph = new RGraph(backend, device, [queue]);
    var edit=graph.BeginEdit(); var abandoned=edit.CreateTransientBuffer(new BufferDesc(4,BufferUsages.CopyDestination)); edit.Dispose();
    var second=graph.BeginEdit(); var current=second.CreateTransientBuffer(new BufferDesc(4,BufferUsages.CopyDestination)); second.Commit();
    Console.WriteLine("REPRODUCED=" + (abandoned == current));
}
void StalePass()
{
    using var graph=new RGraph(backend,device,[queue],new RenderGraphDesc(MaximumFramesInFlight:1));
    var edit=graph.BeginEdit();
    var buffer=edit.CreatePersistentBuffer(new BufferDesc(4,BufferUsages.CopyDestination));
    var pass=edit.AddCopyFramePass("pass",PassQueueSelection.Exact(queue),0,
        new PassOptions(Culling:PassCullingMode.NeverCull),
        static(ref PassDefinition p,ref int x)=>{},static(ref CopyPassCommandScope c,in int x)=>{});
    edit.Commit();
    var first=graph.BeginFrame(); var stale=first.GetPass(pass); first.Dispose();
    var next=graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics:Observe));
    stale.Write(buffer,BufferRange.Whole,PipelineSync.Copy,ResourceAccess.CopyDestination,WriteCoverage.Complete);
    var completions=new QueueCompletion[1];int n=next.Execute(completions);Wait(completions.AsSpan(0,n));
    void Observe(in RenderGraphDiagnosticsView d)=>Console.WriteLine("next frame accesses added by stale handle="+d.Accesses.Length);
}
void MaterialCancel()
{
    using var render = new RenderDevice(backend,device);
    using var materials = new MaterialBindless(render, 4);
    using var graph = new RGraph(backend,device,[queue]);
    var first=graph.BeginFrame(); materials.Upload(ref first); first.Dispose();
    var next=graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Observe)); materials.Upload(ref next);
    var completions=new QueueCompletion[1]; int n=next.Execute(completions); Wait(completions.AsSpan(0,n));
    Console.WriteLine("retry queue submissions=" + n);
    void Observe(in RenderGraphDiagnosticsView v) => Console.WriteLine("retry upload passes=" + v.Passes.Length);
}
void TextureFailure()
{
    using var render = new RenderDevice(backend,device);
    using var graph = new RGraph(backend,device,[queue]);
    var first=graph.BeginFrame(new RenderGraphFrameOptions(SubmissionMode: FrameSubmissionMode.RecordAllThenSubmit));
    var consumer = first.AddCopyPass("fail recording", PassQueueSelection.Exact(queue), 0,
        new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
        static(ref PassDefinition p, ref int x) => {},
        static(ref CopyPassCommandScope c, in int x) => throw new InvalidOperationException("injected recording failure"));
    render.PrepareMissingTexture(ref first,consumer);
    var completions=new QueueCompletion[1];
    try{first.Execute(completions);}catch(Exception e){Console.WriteLine("first failed=" + e.GetType().Name);}
    first.Dispose();
    var next=graph.BeginFrame(new RenderGraphFrameOptions(Diagnostics: Observe));
    consumer=Empty(ref next);
    render.PrepareMissingTexture(ref next,consumer);
    int n=next.Execute(completions); Wait(completions.AsSpan(0,n));
    void Observe(in RenderGraphDiagnosticsView v) { foreach(var p in v.Passes) Console.WriteLine("retry pass=" + p.Label); }
}
void VulkanDuplicate()
{
    using var vk=VulkanGraphicsBackend.Create();
    using var vd=vk.CreateDevice(new DeviceDesc(default,[new(QueueType.Graphics)]));
    var q=vk.GetQueue(vd,QueueType.Graphics);
    using var context=vk.CreateCommandContext(vd,new CommandContextDesc(QueueType.Graphics,0,1));
    vk.Begin(context); using var commands=vk.End(context);
    try{vk.Submit(q,new QueueSubmitDesc([],[],[commands,commands],[],[]));}
    catch(InvalidOperationException e){Console.WriteLine("duplicate rejected=" + e.Message);}
    Console.WriteLine("command status after rejection=" + commands.Status);
    try{var c=vk.Submit(q,new QueueSubmitDesc([],[],[commands],[],[]));Console.WriteLine("retry=" + vk.WaitCpu(c,TimeSpan.FromSeconds(5)));}
    catch(InvalidOperationException e){Console.WriteLine("REPRODUCED: retry rejected=" + e.Message);}
}

```
