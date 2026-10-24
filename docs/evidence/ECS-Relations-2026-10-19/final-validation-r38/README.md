# Final source measurement batch

Source: 51c8ce75949cf16d7190e14028c655f526d5697b. Native 108 cases, H1/H2 controls 4, JIT representatives 9, independent update stages 9, fresh-process startup cases 9. The 139 case tasks and five postprocessing tasks all returned exit0.

Native equivalence is 108/108. Strict ratio limit 1.00 passes 103/126; the approved 1.01 limit passes 114/126. The 12 remaining rows are retained with full-precision ratios, ticks, allocation and GC.

- [Native full matrix](../final-native-r38/summary.md)
- [Native 12 remaining rows](../final-native-r38/approved-unmet.md)
- [H1/H2 independent controls](../final-hh-r38/control-summary.md)
- [JIT representatives](../final-jit-r38/summary.md)
- [Independent update stages](../final-phases-r38/summary.md)
- [Fresh-process startup](../final-startup-r38/report.md)
- [Actual execution and wall time](execution-summary.json)

The preparation cache correction happened before ready ACK and before the first case. Both its initial files and correction facts remain under cache-correction/. The executed Master source is preserved as MasterActual in input-scripts.json. The next source change is decided from the recorded remaining rows; this batch remains an independent measurement.
