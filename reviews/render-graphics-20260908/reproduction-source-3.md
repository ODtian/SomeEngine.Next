# Render 公共 API 复现源码

使用 Release 普通生产程序集。通过公开 RenderPrepareSystems 生命周期取得批次写入能力；并发任务只写不重叠行，并在关闭写入能力前全部结束。没有反射或内部 API。临时项目及编译产物已删除。

``` xml
<Project Sdk="Microsoft.NET.Sdk">
 <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
 <ItemGroup>
  <ProjectReference Include="../src/SomeEngine.Render/SomeEngine.Render.csproj" />
  <ProjectReference Include="../src/SomeEngine.Graphics.Direct3D12/SomeEngine.Graphics.Direct3D12.csproj" />
  <ProjectReference Include="../src/SomeEngine.Graphics.Vulkan/SomeEngine.Graphics.Vulkan.csproj" />
 </ItemGroup>
</Project>

```

``` csharp
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;
using SomeEngine.ECS.Systems;

var builder=new RenderInstancePropertyLayoutBuilder();
var property=builder.Register<int>("review",new RenderInstancePropertyKey("review.value"),new RenderInstancePropertyEncoding("review.int32",4,4,4,1));
var layout=builder.Freeze();var resolved=layout.Resolve(property);
Console.WriteLine("CHECK old column scope releases current write");
using(var buffer=new RenderInstanceBuffer(layout,1)){
 buffer.Add();var first=buffer.BeginWrite(resolved,0,1);var old=first;first.Dispose();var current=buffer.BeginWrite(resolved,0,1);current.Values[0]=42;
 old.Dispose();Console.WriteLine("Get while current write scope exists="+buffer.Get(resolved,0));
 try{current.Dispose();}catch(Exception e){Console.WriteLine("current scope disposal failed="+e.GetType().Name);}
}
Console.WriteLine("CHECK scalar field order identity");
ScalarFieldLayout a=new("a",0,4,1,1,8),b=new("b",4,4,1,1,8);
var left=ScalarLayout.FromFields("same",[a,b],8);var right=ScalarLayout.FromFields("same",[b,a],8);
Console.WriteLine("same sorted fields="+left.Fields.SequenceEqual(right.Fields)+", same hash="+(left.LayoutHash==right.LayoutHash));
Console.WriteLine("CHECK orthographic temporal jitter");var projection=Matrix4x4.CreateOrthographic(10,10,.1f,100);
Console.WriteLine("nonzero jitter leaves projection unchanged="+(projection==TemporalJitter.ApplyToProjection(projection,new Vector2(.25f,.25f),100,100)));
Run("D3D12 parallel disjoint writes",false,true);
Run("Vulkan generation rewrite",true,false);

void Run(string label,bool vulkan,bool parallel){
 Console.WriteLine("CHECK "+label);
 try{
 using IGraphicsBackend backend=vulkan?VulkanGraphicsBackend.Create():D3D12GraphicsBackend.Create();
 AdapterId adapter=default;
 if(!vulkan){backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance,IncludeSoftware:true),[],out int count);var adapters=new AdapterInfo[count];backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance,IncludeSoftware:true),adapters,out _);adapter=adapters.First(a=>!a.HardwareAccelerated).Id;}
 using var device=backend.CreateDevice(new DeviceDesc(adapter,[new(QueueType.Graphics)]));
 using var render=new RenderDevice(backend,device);using var coordinator=new RenderFrameCoordinator(render);
 using var world=new RenderWorld();using var extraction=new RenderExtractionSystems(world);
 var instances=new RenderInstanceStorageSystem(backend,device,coordinator,world,layout,new RenderInstanceOptions{RowCapacity=524288,BatchCapacity=4});
 var group=new RenderPrepareSystems(world,instances,extraction);var system=new BatchWrites(layout,resolved,parallel);group.Add(system);
 try{
 for(int iteration=0;iteration<(parallel?1:2);iteration++){
  if(!coordinator.TryBeginPrepare(out var scope))throw new InvalidOperationException("prepare not granted");
  using(scope){try{group.Update(scope);scope.Commit();Console.WriteLine("update "+iteration+" completed");}catch(Exception e){Console.WriteLine("update "+iteration+" failed="+e);}}
 }
 }finally{
  if(!coordinator.TryBeginPrepare(out var scope))throw new InvalidOperationException("shutdown prepare not granted");
  using(scope){group.Shutdown(scope);instances.Shutdown(scope);scope.Commit();}
  group.Dispose();instances.Dispose();
 }
 }catch(Exception e){Console.WriteLine("ERROR "+e);}
}
sealed class BatchWrites(RenderInstancePropertyLayout layout,ResolvedRenderInstanceProperty<int> property,bool parallel):ISystem<RenderPrepareSystemContext>{
 RenderInstanceBatch? batch;
 public void OnCreate(ref RenderPrepareSystemContext context){}
 public void OnUpdate(ref RenderPrepareSystemContext context){
  if(parallel){
   for(int attempt=0;attempt<8;attempt++){
    using var handle=context.AllocateBatch(layout,524288);var destination=handle.OpenWrite(layout);destination.BindPerInstance(property);
    using var start=new ManualResetEventSlim(false);var values=new int[32768];Array.Fill(values,17);var tasks=new Task[16];
    for(int i=0;i<tasks.Length;i++){var slice=destination.Slice(i*32768,32768);tasks[i]=Task.Factory.StartNew(()=>{start.Wait();slice.Write(property,values);},TaskCreationOptions.LongRunning);}
    start.Set();try{Task.WaitAll(tasks);}catch(AggregateException e){foreach(var failure in e.Flatten().InnerExceptions)Console.WriteLine("parallel writer failed="+failure.GetType().Name+": "+failure.Message);return;}
   }
   Console.WriteLine("parallel write did not reproduce a mapping exception");return;
  }
  using var write=batch is null?context.AllocateBatch(layout,2):context.RewriteBatch(batch,layout);
  var destination2=write.OpenWrite(layout);destination2.BindPerInstance(property);destination2.Write(property,new int[]{1,2});batch=write.Publish();
 }
 public void OnDestroy(ref RenderPrepareSystemContext context){if(batch is not null){context.Retire(batch);batch=null;}}
}

```
