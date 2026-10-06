# #142 optional shadow geometry time budget

Pinned native commit 396c9f059903aed5fec78ecd3d437a40c6415368. New source references and GitBlob/SHA/bytes live outside the repository in `F:/SomeEngine.AgentWork/rg70-remaining/ticket-142/native-work-time-sources.json`.

`DynamicRenderScaling.h:38–52`, `DynamicRenderScaling.cpp:23–105`, `DynamicResolution.cpp:65–99,215–413`, and `VirtualShadowMapArray.cpp:831–846` define the linear VSM bucket: zero disables timing, fraction 0–1, 5% headroom, 16 frame history, .9 recursive history weights, 1% threshold, eight-frame change period, and .9 recovery blend. Two consecutive over-budget samples truncate the history. The VSM budget is independent of the primary viewport size.

The feature installs timestamp queries and a persistent readback only for a positive time budget. Query start follows shadow page preparation; query end follows the completed target geometry. Completed readbacks carry the rendering frame and its actually used fraction. The CPU budget state commits at successful graph frame completion, including cancellation handling. Time budget strength takes precedence over load heuristic strength. The first completed timing is skipped as in the native heuristic reset.

`TimeBudgetEnablesWorkFeedbackWithTheDefaultLoadBudget`: API red was CS1061/CS0117 for the absent time-budget setting. Green: one public CPU behavior row passed. Production shader bias behavior is recorded in the preceding `production-work` slice; final integrated backend and scene validation is coordinated separately.
