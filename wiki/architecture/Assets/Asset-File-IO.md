# 资产文件、Block 与显式 I/O

2026-09-30。资产持久化复用现有二进制文档、范围 I/O、压缩、摘要、包认证，不建立第二种容器。对象语义见 [Asset 与 Block](Asset-Objects.md)。

## 公共根和类型目录

资产根是 `AssetHeader`，记录身份、名称、资产类型和 Block 描述。每个 Block 描述包含自己的持久类型、编码信息、元数据及负载位置。Asset 的业务字段不再平铺进根，也没有独立的 Asset 字节块存储。

文档格式为 `SEBDOC04`，类型目录明确指出根类型。公共描述读取不需要安装业务类型；真正读取 Block 时由请求的强类型静态编码器核对类型和 schema。未知 Block 可以列目录，未修改内容也可以按原编码保留，不通过反射猜测实现。

普通对象解码和编码视图共用文档结构、边界及认证。来源属于签名包时，内部摘要自洽不能替代包中已经认证的描述核对。详细字节协议继续由 [二进制文档说明](../../../docs/BinarySerialization.md) 维护。

## 文本与其他内容表达

可读源格式为 `SomeEngine.AssetSource.2`。顶层身份与 Block 列表分离，每个可读 Block 使用自己的静态文本编码；大负载通过相对路径引用文件，不展开为 JSON 大数组。仅进程内 Block 不作为可重建磁盘数据写出。

一种 Asset 可以包含文本字段、二进制、外部内容和延迟取得的 Block。是否支持文本由具体 Block 决定，不是 Asset 的二选一分类。旧平铺源格式已移除；已有生成产物从真实来源重建，不增加旧 schema 回退。

`AssetText.Create(asset)` 创建持有资产分支的实际写入对象。调用 `writer.WriteBlock<TBlock>(chunkFiles)` 选择自己的强类型 Block，`writer.Write(buffer)` 取得文本编码，`writer.SaveAsync(path)` 完成文件写入。根字段和所选 Block 都来自同一份分支。

## 保存与未加载内容

保存先建立隔离的内容分支，再让各 Block 提供自己的元数据和负载。Block 直接编码普通字段、数组或已有的分段只读视图；尚未取得且未修改的 Block 或负载可以使用原文档的有界复制，不需要为了改名而解压全部内容。

公共文件流程使用同目录临时文件，完成写出、刷新和目标身份检查后替换目标。失败或取消清理临时文件，保留已有成功内容。保存返回的 `AssetEntry` 描述本次实际输出；它不会暗中更新工程目录或把原对象绑定到新文件。

二进制保存结果中的直接依赖来自实际写出的 Block 描述，与重开文件得到的依赖一致。仅进程内 Block 仍属于当前 Asset 的内存内容，可以参与分支和各自的生命周期，但其数据及专用引用不进入文件或保存返回的目录条目。未加载的持久 Block 则继续保留原文件描述中的引用，无需先执行其解码器。

跨多个输出的保存、登记和回退属于 [工程导入流程](Asset-Import-and-Storage.md)。多个独立文件不被宣称具有断电时的整体原子替换保证。单文件已经替换后，先前打开的读者仍使用自己选择的旧文件内容，不拼接新旧版本。

来源文件的通用复制限定在同一目录，复制后分配新的来源及元素身份。跨目录复制可能改变外部相对引用的含义，因此必须由认识来源格式的代码改写，不能直接复制字节并登记。拒绝请求时，来源、伴随文件、外部数据和目录均保持原样；同目录复制与强类型资产复制不受此限制影响。

`AssetProject.CopyAssetAsync(asset, destination)` 直接调用复制资产的 `SaveAsync`。跨目录移动使用 `AssetBlockPathEditor<TBlock>`；扩展通过覆盖 `Relocate` 更新自己字段中的相对路径，共同的外部 chunk 路径由项目文件方法更新。

## 字节与业务分块

文件 chunk 与业务单元的关系由具体 Block 描述。Texture tile 使用独立 chunk；Cluster 两份 Block 的持久读取以各自的元数据边界校验，渲染页在取得 Block 后由几何和不透明属性字节组合。需要独立读取的业务单元必须有可独立取得、解码和校验的存储边界。

`AssetFile.ReadBytesAsync` 在首次等待前捕获当前 Scheduler，并保持文件访问到整个读取完成。未压缩 chunk 直接读入最终实际长度数组；Brotli chunk 先取得独立编码输入，再通过该执行器的共享资产解码类别提交低优先级 Job，直接解码到最终数组。通过 `JobHandle.CompleteAsync` 完成长度与内容校验后才向调用方交付数组；协调调用在零 worker 下沿共同完成入口推进解码。该方法的输出复用和共同等待由 [资产字节缓存](Asset-Byte-Cache.md) 持有。范围读取继续使用 `IRangeSource`，按具体业务范围取得数据。

`BinaryDocument.ReadChunkInputAsync(BinaryChunkRef, token)` 根据来源中的 chunk 描述读取实际编码长度，并在读取前后核对来源代际。返回的 `BinaryChunkInput` 保存这份编码字节及描述；`DecodeInto(Span<byte>, token)` 直接填入调用方最终存储，核对解码长度与内容。输入可以在文档关闭后继续解码，计算过程只读取这份输入。资产调用方决定 Job 调度及输入使用期，序列化层提供读取与纯计算能力。直接文档读取的 `CopyChunkAsync` 和压缩 `AcquireChunkAsync` 共用此解码算法；不可变未压缩来源上的 `AcquireChunkAsync` 保持原借用分支。公共入口与验证见 `BinaryDocumentOwnershipTests.EncodedChunkCanDecodeAfterItsDocumentCloses` 和 `AssetDataSharingTests` 的压缩像素检查。

文件及映射的借用视图只在来源约定的操作范围内使用，不能作为普通使用对象的字节。WorldBlock 使用既有 ECS 编码器和普通 MemoryStream，取得完整编码数据后恢复独立 World。

相关实现：[AssetHeader](../../../src/SomeEngine.Assets/AssetHeader.cs)、[AssetText](../../../src/SomeEngine.Assets/AssetText.cs)、[保存与 Block 写出](../../../src/SomeEngine.Assets/Asset.Blocks.cs)、[文档写入器](../../../src/SomeEngine.Serialization/Containers/BinaryDocumentWriter.cs)。
