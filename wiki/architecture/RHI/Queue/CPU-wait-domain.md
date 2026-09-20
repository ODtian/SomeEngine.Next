# RHI-QUE-003 — CPU wait domain 

CPU waits accept a nonnegative `TimeSpan`, `Timeout.InfiniteTimeSpan`, or a value representable by the
native millisecond timeout. The result is `Completed` or `Timeout`; Device Lost remains an exception.
A zero timeout is a poll and does not insert a Queue wait.
^rhi-que-003

