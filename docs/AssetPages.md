# 资产字节与业务分块

通用 CPU 页存储已移除。普通字段和集合使用 C# 对象，发布字节使用实际长度的托管数组；纹理 tile、mip、几何页和文件 chunk 继续表达各自的业务或存储单位。

完整规则见 [数据共享与修改](../wiki/architecture/Assets/Asset-Sharing-and-Writes.md)。

## 从同一内容定义复用数据

```csharp
using TextureAsset asset = await TextureAsset.OpenAsync(path);
TextureBlock block = asset.GetBlock<TextureBlock>();
using Texture first = await block.CreateAsync();
using Texture second = await block.CreateAsync();
ulong tile = first.MipTiles[0].ChunkKey;
if (first.TryGetTile(tile, out ReadOnlyMemory<byte> firstPixels)
    && second.TryGetTile(tile, out ReadOnlyMemory<byte> secondPixels))
{
    // 对同一已加载 tile，两个视图引用同一个最终数组。
    bool shared = firstPixels.Equals(secondPixels);
}
```

同一个 Block 的数据定义及其未变分支共享取得结果。节点弱引用最终数组，只在真实读取期间保存共同任务；使用对象强引用数组。独立打开的两个 Asset 不因 ID 相同而自动合并数据定义，也没有全局语义键缓存。

`ReadBytesAsync` 返回普通 `ReadOnlyMemory<byte>`，没有配对释放操作。Mesh 使用 `TryGetPage` 和 `Bvh`，Texture 使用 `TryGetTile`。既有视图在 Asset 或来源关闭之后仍然有效。Texture 仍有设备关联的关闭操作，它不使已经返回的托管数据视图失效；Mesh 的纯托管数据不实现释放协议。

## 修改与保存

普通属性直接赋值；可编辑集合使用普通集合。`Duplicate()` 复制字段和集合结构，共享不可变元素及未变的数据定义，不承诺固定成本的分支复制。

`AssetBlock.Chunks` 是明确提供的不可变字节数据，只读公开。替换字节调用 `SetBytes`，撤掉本地覆盖调用 `RemoveBytes`；输入必须有独立 GC 生命周期，赋值后不得再修改。借用数据先复制为独立数组。修改已发布内容时建立新值，已有消费者继续使用旧引用。

```csharp
ReadOnlyMemory<byte> previous = await block.ReadBytesAsync(key);
byte[] replacement = previous.ToArray();
replacement[offset] = value;
block.SetBytes(key, replacement);
```

文件保存复用文档写入器。未加载且未修改的数据按原存储范围复制；编码使用 `IBufferWriter<byte>` 或既有 Stream 编码器。World 的完整存档使用普通托管缓冲，不增加页适配流。

## 读取与取消

文件读取直接写入或解压到最终数组，不保存长期解码缓存。独立可读取范围由格式提供，内存尺寸不决定压缩边界。单个等待者取消只结束自己的等待；真实 I/O 和其来源保持有效直到操作结束。失败不保留在节点中，下次可以重试。

读取限制约束单次大小和执行工作，不用页计数宣称全部存活 GC 数据具有精确硬预算。GPU 上传缓冲仍遵循设备完成信号，数据通过既有 Game world 到 Rendering world 的路径传递。
