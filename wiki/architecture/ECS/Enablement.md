# Enablement  Parent: [[Workload-Rules]] 

enable/disable不移动 component storage，但会改变 query membership，因此 `Enablement<T>` 是独立 bit-range resource。query先为所有 structural candidate chunks登记 enablement和data ranges，hazard完成后再读取 bits；`SetEnabled` 写对应 entity bit range并发布 change epoch。entity destroy仍由 topology guard保护。

