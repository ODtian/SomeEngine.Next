# Streaming Shutdown

Shutdown 先停止接受新的 streaming work，再等待已经发出的 loader/I/O 任务结束，最后释放 source 和 device resources。

Mesh reload 时旧 source 在所有已经发出的读取真正结束前必须继续存在。