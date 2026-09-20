# Relation Queries

Direct relation query 以 edge entity 为 row identity，一条 edge 一行。Endpoint filters 是 semi-join：一个 endpoint entity 至多产生一行，不因 parallel edges 自动展开。

需要 endpoint-edge expansion 时必须显式选择，并在可写场景证明不会从两个 endpoint 路径重复写同一 edge。