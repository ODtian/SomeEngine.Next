# Raster Compatibility

Raster execution key 表示执行 raster 时真正需要一致的 shader entry、fixed state、specialization 与 stage ABI。

最终 bin 另外要求 command 上的 direct binding 相容。Material identity、普通 indexed 材质值和 bindless descriptor 值本身不构成 Raster execution key，也不拆 bin；只有需要重新绑定的 direct resource/view 才拆。

参见 [[Stage-Compatibility]] · [[architecture/cluster/Raster-Execution]]。