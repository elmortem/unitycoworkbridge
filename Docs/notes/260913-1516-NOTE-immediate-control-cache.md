# Immediate control and cache requests

Package 0.26.0, plugin 1.23.0, CLI 1.18.1.

`stopplay` is processed by the control scan every 250 ms, independently of the work queue and session ownership. Its journal survives an exit-related reload. It requests cancellation of the current PlayMode test, requests exit without waiting for Test Framework cleanup, and reports success only after Play Mode has ended. Active C# execution and test cleanup still retain their ownership until they actually stop. `cancel` retains its existing coordinated-window protection; `stopplay` is an explicit override of Play Mode ownership.

Cache lookup now runs before queue-blocking branches, excludes a test owner restored across reload, and defers during compilation/import. Untokened cache reads do not reserve an editor window. Explicit coordinated requests retain step validation/accounting. Cache misses and `--fresh` remain queued and are checked again when scheduled. Test input digests are local to each request and rechecked before serving; source fingerprints include content to detect edits preserving timestamps and sizes. Cache evidence describes saved inputs, not live scene objects.

Validation: `scripts/verify-immediate-commands.ps1` exercises cached tests and compile during a 45-second cooperative C# task, a queued fresh compile, immediate stopplay without losing the active executor, foreign stopplay during a responsive PlayMode test, canceled test outcome, and successful subsequent compile. Live host: Unity 2022.3.62f2. CLI build/tests and coordination tests pass. The separate Unity 6 cancellation-cleanup defect remains unproven as fixed; no WaterWalk recovery claim is made by these checks.
