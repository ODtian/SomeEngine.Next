# 公共 API 补充复现源码

使用当前 render 工作树 Release 生产程序集，未使用内部 API。原生队列等待通过另一条队列的合法外部时间线信号解除。临时项目与编译产物已删除。输出见 public-api-reproductions-2.txt。

``` xml
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
 <ItemGroup>
  <ProjectReference Include="../src/SomeEngine.RenderGraph/SomeEngine.RenderGraph.csproj" />
  <ProjectReference Include="../src/SomeEngine.Render/SomeEngine.Render.csproj" />
  <ProjectReference Include="../src/SomeEngine.Graphics.Direct3D12/SomeEngine.Graphics.Direct3D12.csproj" />
 </ItemGroup>
</Project>

```

``` csharp
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;
using RGraph=SomeEngine.RenderGraph.RenderGraph;
using Buffer=SomeEngine.Graphics.Buffer;

using IGraphicsBackend backend=D3D12GraphicsBackend.Create();
backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance,IncludeSoftware:true),[],out int count);
var adapters=new AdapterInfo[count];backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance,IncludeSoftware:true),adapters,out _);
using Device device=backend.CreateDevice(new DeviceDesc(adapters.First(a=>!a.HardwareAccelerated).Id,[new(QueueType.Graphics,Count:5)],requiredFeatures:DeviceFeatures.ExternalTimelines));
var queues=Enumerable.Range(0,5).Select(i=>backend.GetQueue(device,QueueType.Graphics,(uint)i)).ToArray();
var q=queues[0];
Run("synchronization failure recovery",SyncRecovery);
Run("failed sparse declaration cleanup",SparseDeclaration);
Run("region added to existing declaration",RegionEdit);
Run("persistent allocation rollback retry",AllocationRetry);
Run("bound external range completion",BoundRange);
Run("four queue observation",FourQueues);
Run("failed wait preserves prepare exclusion",FailedWait);
void Run(string label,Action body){Console.WriteLine("CHECK "+label);try{body();}catch(Exception e){Console.WriteLine("ERROR "+e);}}
void Wait(QueueCompletion c){if(backend.WaitCpu(c,TimeSpan.FromSeconds(10))!=WaitStatus.Completed)throw new TimeoutException();}
void Execute(ref RenderGraphFrame frame){var c=new QueueCompletion[1];int n=frame.Execute(c);for(int i=0;i<n;i++)Wait(c[i]);}
void Empty(ref RenderGraphFrame frame)=>frame.AddCopyPass("empty",PassQueueSelection.Exact(q),0,new PassOptions(Culling:PassCullingMode.NeverCull),static(ref PassDefinition p,ref int x)=>{},static(ref CopyPassCommandScope c,in int x)=>{});
void SyncRecovery()
{
 using var graph=new RGraph(backend,device,[q],new RenderGraphDesc(MaximumFramesInFlight:1));
 using var texture=backend.CreateTexture(device,new TextureDesc(TextureDimension.Texture2D,4,4,1,1,1,1,Format.R8G8B8A8UNorm,TextureUsages.Sampled|TextureUsages.CopySource));
 for(int iteration=0;iteration<2;iteration++){
  var frame=graph.BeginFrame();var range=new TextureSubresourceRange(0,1,0,1,TextureAspects.Color);
  var textureId=frame.Import(texture,[new(range,texture.InitialSync,texture.InitialAccess,texture.InitialLayout,ResourceContentState.Defined)]);
  bool invalid=iteration==0;
  frame.AddGeneralPass("texture reader",PassQueueSelection.Exact(q),textureId,new PassOptions(Culling:PassCullingMode.NeverCull),
   (ref PassDefinition p,ref GraphTextureId id)=>{p.Read(id,range,PipelineSync.Copy,ResourceAccess.CopySource,TextureLayout.CopySource);if(invalid)p.Read(id,range,PipelineSync.PixelShading,ResourceAccess.ShaderResource,TextureLayout.ShaderResource);},
   static(ref GeneralPassCommandScope c,in GraphTextureId id)=>{});
  try{Execute(ref frame);Console.WriteLine("iteration "+iteration+" succeeded");}catch(Exception e){Console.WriteLine("iteration "+iteration+" failed="+e.GetType().Name+": "+e.Message);}finally{frame.Dispose();}
 }
}
void SparseDeclaration()
{
 using var graph=new RGraph(backend,device,[q]);var frame=graph.BeginFrame();
 var buffer=frame.CreateBuffer(new BufferDesc(4,BufferUsages.CopySource));
 try{frame.AddCopyPass("rejected declaration",PassQueueSelection.Exact(q),buffer,default,
  static(ref PassDefinition p,ref GraphBufferId id)=>{p.ReadRanges(id,new BufferRangeSet([new BufferRange(0,4)]),PipelineSync.Copy,ResourceAccess.CopySource);throw new InvalidOperationException("injected declaration failure");},static(ref CopyPassCommandScope c,in GraphBufferId id)=>{});}
 catch(InvalidOperationException e){Console.WriteLine("declaration rejected="+e.Message);}
 Empty(ref frame);try{Execute(ref frame);Console.WriteLine("replacement succeeded");}catch(Exception e){Console.WriteLine("replacement failed="+e.Message);}finally{frame.Dispose();}
}
void RegionEdit()
{
 using var graph=new RGraph(backend,device,[q]);var holder=new RegionHolder();var edit=graph.BeginEdit();
 var pass=edit.AddGeneralFramePass("region",PassQueueSelection.Exact(q),holder,new PassOptions(Culling:PassCullingMode.NeverCull),static(ref PassDefinition p,ref RegionHolder h)=>{},
  static(ref GeneralPassCommandScope c,in RegionHolder h)=>{c.BeginRendering(h.Region);c.EndRendering();});edit.Commit();ulong before=graph.StructureVersion;
 edit=graph.BeginEdit();var definition=edit.GetPass(pass);holder.Region=definition.DefineRenderingRegion(0,0,16,16);edit.Commit();
 Console.WriteLine("version unchanged="+(before==graph.StructureVersion));var frame=graph.BeginFrame();
 try{Execute(ref frame);Console.WriteLine("new region executed");}catch(Exception e){Console.WriteLine("new region failed="+e.GetType().Name+": "+e.Message);}finally{frame.Dispose();}
}
void AllocationRetry()
{
 using var graph=new RGraph(backend,device,[q]);var edit=graph.BeginEdit();
 var buffer=edit.CreatePersistentBuffer(new BufferDesc(16,BufferUsages.ShaderRead|BufferUsages.CopyDestination));
 var invalid=edit.CreateBufferSrv(buffer,new BufferRange(0,16),Format.R32UInt,4);
 try{edit.Commit();Console.WriteLine("unexpected first commit success");return;}catch(ArgumentException e){Console.WriteLine("first commit rejected="+e.Message);}
 edit.Remove(invalid);
 edit.AddCopyFramePass("write after retry",PassQueueSelection.Exact(q),buffer,new PassOptions(Culling:PassCullingMode.NeverCull),
  static(ref PassDefinition p,ref GraphBufferId id)=>p.Write(id,BufferRange.Whole,PipelineSync.Copy,ResourceAccess.CopyDestination,WriteCoverage.Complete),
  static(ref CopyPassCommandScope c,in GraphBufferId id)=>c.ClearBuffer(c.GetBuffer(id),BufferRange.Whole,123));edit.Commit();
 var frame=graph.BeginFrame();try{Execute(ref frame);Console.WriteLine("retry succeeded");}catch(Exception e){Console.WriteLine("retry failed="+e.GetType().Name+": "+e.Message);}finally{frame.Dispose();}
}
void BoundRange()
{
 using var timeline=backend.CreateExternalTimeline(device,0);
 var blocked=backend.Submit(q,new QueueSubmitDesc([],[new TimelinePoint(timeline,1)],[],[],[]));
 try{
  using Buffer buffer=backend.CreateBuffer(device,new BufferDesc(16,BufferUsages.CopySource),MemoryType.Upload);
  using var graph=new RGraph(backend,device,[q]);var edit=graph.BeginEdit();var id=edit.DeclareExternalBuffer(new BufferDesc(16,BufferUsages.CopySource),MemoryType.Upload);
  edit.AddCopyFramePass("read bound buffer",PassQueueSelection.Exact(q),id,new PassOptions(Culling:PassCullingMode.NeverCull),
   static(ref PassDefinition p,ref GraphBufferId b)=>p.Read(b,BufferRange.Whole,PipelineSync.Copy,ResourceAccess.CopySource),static(ref CopyPassCommandScope c,in GraphBufferId b)=>{});edit.Commit();
  var frame=graph.BeginFrame();frame.BindExternalBuffer(id,buffer,[new(BufferRange.Whole,buffer.InitialSync,buffer.InitialAccess,ResourceContentState.Defined)]);var completions=new QueueCompletion[1];frame.Execute(completions);
  Console.WriteLine("submission unfinished="+!backend.IsComplete(completions[0]));Console.WriteLine("reported range completions="+graph.GetRangeCompletionCount(buffer,BufferRange.Whole));
  Wait(backend.Submit(queues[4],new QueueSubmitDesc([],[],[],[],[new TimelineSignal(timeline,1)])));Wait(completions[0]);
 }finally{Wait(backend.Submit(queues[4],new QueueSubmitDesc([],[],[],[],[new TimelineSignal(timeline,1)])));Wait(blocked);}
}
void FourQueues()
{
 using var render=new RenderDevice(backend,device);using var coordinator=new RenderFrameCoordinator(render);using var timeline=backend.CreateExternalTimeline(device,0);
 var completions=queues.Take(4).Select(queue=>backend.Submit(queue,new QueueSubmitDesc([],[new TimelinePoint(timeline,1)],[],[],[]))).ToArray();
 try{coordinator.TryBeginObservation(out var observation);observation!.Complete(completions);try{bool ok=coordinator.TryBeginPrepare(out var scope);scope?.Dispose();Console.WriteLine("prepare result="+ok);}catch(Exception e){Console.WriteLine("prepare failed="+e.GetType().Name);}}
 finally{Wait(backend.Submit(queues[4],new QueueSubmitDesc([],[],[],[],[new TimelineSignal(timeline,1)])));foreach(var c in completions)Wait(c);}
}
void FailedWait()
{
 using var render=new RenderDevice(backend,device);using var coordinator=new RenderFrameCoordinator(render,TimeSpan.FromMilliseconds(5));using var timeline=backend.CreateExternalTimeline(device,0);
 var blocked=backend.Submit(q,new QueueSubmitDesc([],[new TimelinePoint(timeline,1)],[],[],[]));
 try{coordinator.TryBeginObservation(out var observation);observation!.Complete([blocked]);try{coordinator.WaitForTrackedSubmissions();}catch(TimeoutException){Console.WriteLine("wait timed out");}
  Console.WriteLine("submission unfinished="+!backend.IsComplete(blocked));bool granted=coordinator.TryBeginPrepare(out var scope);Console.WriteLine("exclusive prepare granted="+granted);scope?.Dispose();}
 finally{Wait(backend.Submit(queues[4],new QueueSubmitDesc([],[],[],[],[new TimelineSignal(timeline,1)])));Wait(blocked);}
}
sealed class RegionHolder{public PassRenderingRegionId Region;}

```
