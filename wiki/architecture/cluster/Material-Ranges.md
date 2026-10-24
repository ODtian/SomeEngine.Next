# Material Ranges

一个 cluster 可以包含多个 mesh-local material ranges，每个 range 覆盖不同数量的 triangles。

实例化时 mesh-local material slot 映射到真实 Material，再分别参与 Deform/Raster/Shade compatibility。Cluster 不建立 universal Material device-data struct。

同 cluster 中不同 ranges 可以拥有不同 Raster/Shade compatibility，但必须满足 [[Cluster-Deform-Compatibility]]。