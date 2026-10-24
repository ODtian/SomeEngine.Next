# Final NativeAOT measurements outside the approved limit

Source 51c8ce75949cf16d7190e14028c655f526d5697b. 108/108 equivalent; strict 103/126; ApprovedRatioLimit=1.01, approved 114/126. Stopwatch frequency: 10,000,000 ticks/second. Each side has 256 actual frames per boundary; allocations and GC below use those actual frame records.

| Case | Boundary | Median E/H ticks | Median ratio | p95 E/H ticks | p95 ratio | Frame allocation E/H bytes | GC E/H (Gen0/1/2) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| finite-small-mask1-oneway | Query | 3972/3977.5 | 0.9986172218730358 | 4636.75/4575.25 | 1.0134418884213978 | 0/0 | 0/0/0 ; 0/0/0 |
| finite-small-mask1-mutual | Query | 4050/4080.5 | 0.992525425805661 | 4663.75/4573.5 | 1.0197332458729638 | 0/0 | 0/0/0 ; 0/0/0 |
| finite-small-mask8-mutual | Query | 4140/4112 | 1.0068093385214008 | 4792/4679.75 | 1.0239863240557723 | 0/0 | 0/0/0 ; 0/0/0 |
| default-small-dynamic50 | Query | 1075/1064 | 1.0103383458646618 | 1336/1361.5 | 0.9812706573632024 | 0/0 | 0/0/0 ; 0/0/0 |
| default-small-dynamic90 | Query | 1101/1092 | 1.0082417582417582 | 1263.5/1226.5 | 1.0301671422747656 | 0/0 | 0/0/0 ; 0/0/0 |
| character-small-hair16-clustered | Query | 4494/4356 | 1.0316804407713498 | 5083/5071 | 1.0023663971603234 | 249856/249856 | 0/0/0 ; 0/0/0 |
| character-small-hair16-interleaved | Query | 4328/4284.5 | 1.0101528766483836 | 4872/4846.25 | 1.005313386639154 | 249856/249856 | 0/0/0 ; 0/0/0 |
| updates-small-sparse | FrameTotal | 2157/2164 | 0.9967652495378928 | 2518.5/2485.25 | 1.0133789357207525 | 1763312/1763312 | 0/0/0 ; 0/0/0 |
| threads-small-sparse-parallel | ScheduledPairOutput | 765.5/758 | 1.0098944591029024 | 1196.25/1167.5 | 1.0246252676659529 | 946216/964808 | 0/0/0 ; 0/0/0 |
| default-middle-dynamic10 | Query | 15660.5/15637 | 1.0015028458144146 | 16962.25/16659 | 1.0182033735518339 | 0/0 | 0/0/0 ; 0/0/0 |
| default-middle-dynamic50 | Query | 16034/15858.5 | 1.0110666204243781 | 17441.25/17193.75 | 1.0143947655398038 | 0/0 | 0/0/0 ; 0/0/0 |
| default-large-dynamic90 | Query | 265827.5/260367 | 1.02097231984084 | 280268.75/272192.75 | 1.0296701510234934 | 0/0 | 0/0/0 ; 0/0/0 |

The JSON companion retains absolute microseconds, sample counts, nonzero-GC frame counts and original queue identity. H1/H2 controls use their separate same-function report and retain Required=false.
