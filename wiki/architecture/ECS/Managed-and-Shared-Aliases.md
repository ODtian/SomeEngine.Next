# Managed/shared aliases  Parent: [[Workload-Rules]] 

parallel ref/span component默认只接受 unmanaged或metadata证明的 deep-value/non-alias storage。managed `Read` 只保护 component slot/reference，不证明引用对象内部 immutable；mutable object默认登记 whole family或 object-identity external resource，并在 named executor上 Execute。系统外未登记 alias不在 ECS 可证明范围内。shared/index hashing、equality、copy和potential disposal全部属于 structural root prepare/retire，不能进入 publish critical section。

