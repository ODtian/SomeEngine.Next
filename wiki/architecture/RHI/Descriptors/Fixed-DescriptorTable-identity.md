# RHI-DSC-001 — Fixed DescriptorTable identity 

`DescriptorTable` owns a fixed nonempty array of `DescriptorSlotDesc`. The table is either Resource or
Sampler storage, has one Device node index, and exposes a stable `Count`. There is no public slot
allocate/free API and slots are not recycled within the table lifetime.

`GetDescriptorIndex(table, slot)` returns `DescriptorIndex`. Its identity is exactly the table
reference plus numeric `Value`. Equal numeric values from different tables are not equal. The numeric
value is the value shader data may carry; the table reference remains host provenance.

`DescriptorSlotDesc` describes only the native storage shape needed by a free-standing mutable table:
resource/sampler kind, typed or structured Buffer facts, Texture view dimension/format/aspects and UAV
counter form. Pipeline-owned bounded slots are derived directly from S# and do not ask the caller to
repeat shader layout.
^rhi-dsc-001

