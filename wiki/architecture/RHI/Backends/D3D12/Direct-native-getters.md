# RHI-D3D12-004 — Direct native getters 

`D3D12NativeAccess` is a typed Device capability for explicitly requested native integration. Getters
return borrowed native pointers whose validity is bounded by the documented public call or owning
object lifetime. They do not transfer COM ownership.

Command-list borrow is valid only while the Context remains in the required recording state and until
the next public Context/capability call. Every public command begins a new borrow epoch, including a
command later suppressed by state caching.

Native getters are not an unchecked parallel API. The direct backend still validates backend/Device
provenance before returning a pointer.
^rhi-d3d12-004

