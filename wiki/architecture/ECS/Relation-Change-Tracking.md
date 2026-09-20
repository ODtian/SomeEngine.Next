# Relation Change Tracking

Relation topology change 直接使用 protected edge-kind/endpoints/order components 的 ECS change tracking 表达，不建立第二套 relation delta stream。

Create 对应 kind/endpoints Added，retarget 对应 endpoints Changed，destroy 对应 Removed；payload change 使用普通 component tracking。Local order/policy 变化由对应 protected component tracking 表达。