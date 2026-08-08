---
name: harness-runner
description: Suspended compatibility placeholder. The scripted SomeEngine harness runner has been removed and this skill must not be invoked.
---

# Harness Runner (Suspended)

The scripted harness workflow is retired. Its PowerShell entrypoint, hard/warning buckets, run ids, and harness status artifacts no longer exist.

Do not invoke this skill and do not recreate an orchestration script. If the user explicitly requests the remaining opt-in harness checks, run the ordinary .NET test solution directly:

```shell
dotnet test SomeEngine.Harness.slnx
```

That command is an explicit user opt-in, not a default workflow or completion gate.
