# Bundle Structural Writes

Bundle spawn/add/replace/batch 与普通结构命令使用同一 candidate transaction。

批处理逐行 materialize；future rows 在 callback 执行前不可被 query/hook/index 看见。World-touching write 只能通过声明过的 bundle access 或记录到后续结构命令，不能通过捕获 World 绕过访问边界。