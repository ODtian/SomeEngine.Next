# Lifetime/external  Parent: [[review/ECS-Validation|ECS Validation Checklist]] 

- Closing拒绝 roots但允许 existing descendants；
- system remove/World dispose等待 pending-yet-unadmitted jobs和 external owners；
- self-dispose、child-close-parent domain和持有 B permit时 close B立即拒绝而不自锁；
- external work绝不在 prior CPU hazard结束前 submit；
- submit fault、signal race、device loss、永不 signal诊断不提前释放；
- Fast模式也不能释放 active/pinned resource。

