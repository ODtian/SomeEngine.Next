# r38 Native query binding

Source: `51c8ce75949cf16d7190e14028c655f526d5697b`. Native publish queue: `09e27c3989dd4717835ef1f692127416`. Read queue: `74269a841dca45f1bd9534a164a35eab`, ExitCode 0, UTC 2026-10-19 21:58:09.3922167–21:58:13.8691502. Object before and after: UTC 21:35:48.1647902, 26,164,795 bytes. The compiled source check returned 0. Each of the five selected symbols matched once before disassembly.

The reader ran from an in-memory PowerShell command. Its selected disassembly, source check, symbol map, statistics, command receipt and output are retained here.

| Data | Generic OneWay / Mutual | Handwritten OneWay / Mutual |
| --- | --- | --- |
| QueryData address | World stores World/State/last/current at `rbp-48`; construction at `22484c` / `22552c` receives that address and forms the view at `rbp-90`; the Access pointer loads into RSI before the candidate loop | Callback receives the 8-byte QueryCursor value in RDX and moves that QueryData address to RSI |
| Point arguments | RSI to RCX, first or second Entity to RDX | RSI to RCX, first or second Entity to RDX |
| Point calls | `224883`, `22489a` / `225563`, `22557a`, all target `1db3a0` | `1604b1`, `1604c6` / `160551`, `160566`, all target `1db3a0` |
| First component reference | RAX to R13; R13 survives the second point call | RAX to R15; R15 survives the second point call |
| Output address | Accepted row loads the external output address with `mov (%r14),rcx` | Accepted row computes its output field address with `lea 10(rbx),rax` |
| Output count | One field load, LEA increment, one field store, capacity comparison | One field load, LEA increment, one field store, capacity comparison |
| Pair temporary | One 16-byte store at `rbp-a0`, one reload upon acceptance | One 16-byte store at `rsp+28`, one reload upon acceptance |
| OneWay hot region | `22486f..2248db`: 29 instructions, 108 bytes | `16049f..160503`: 29 instructions, 100 bytes |
| Mutual hot region | `22554f..2255c5`: 32 instructions, 118 bytes | `16053f..1605ac`: 32 instructions, 109 bytes |
| Permission | One load/test/conditional branch per visited direction | One load/test/conditional branch per visited direction |

Ranges use an exclusive end. The Generic OneWay and Mutual World methods each use 216 stack bytes and clear 96 bytes on entry.

The Handwritten OneWay and Mutual execution callbacks each use 104 stack bytes and clear 0 bytes on entry. These are the inner execution callbacks; the enclosing World methods form their own scopes.

The shared point read uses 120 stack bytes and clears 32 bytes on entry.

The shared point method at `1db3a0` receives QueryData in RCX and Entity in RDX. It reads World into RBX, QueryState into RSI and last version into EDI, saves Entity at `rsp+88`, and returns the borrowed component reference in RAX. The same entry and checks serve both callers.

Instruction counts describe these concrete paths. The registers, addressing operations, displacement widths and output-address load differ as shown; timings remain in the series measurement reports.

Files: `native-five-functions.raw.asm` preserves complete selected LLVM output; `native-{OneWayCaller,OneWayHand,MutualCaller,MutualHand,Point}.asm` preserve the same functions with shortened symbols; `native-symbols.json` retains full symbol names; `native-hot-loops.json` records the four candidate loops; `native-function-statistics.json` and `.log` record complete function ranges and entry statistics; `native-reader-facts.json` records source/object/commands; `native-reader-queue.json` and the stdout/stderr logs retain the queue result.
