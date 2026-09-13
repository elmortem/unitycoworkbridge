# Unity 6: cancellation remains blocked

Observed on 2026-09-13, WaterWalk, Unity 6000.4.0f1, package 0.25.0.

- City task `Task_20260913_061950_531_d9f6bc75` started at 06:40:32 UTC and was finalized as `interrupted_by_domain_reload` at 10:00:00 UTC. Its original cancellation state was lost; the exact cause cannot be established from the remaining journal.
- Next task `Task_20260913_062110_536_670ca3c4` started at 10:00:10 UTC. At 10:05:10 it entered cancellation and left PlayMode. At 10:05:40 it became `cancel_blocked`, with fresh heartbeat and no pending scene recovery file. The queue remains blocked. This reproduces a failure after a new run, independently of adopting an old task.
- Package 0.25.1 recovers the unique active Unity job ID when cancellation has no saved ID. It also records rejected cancellation requests, exceptions, and the specific busy check in the task journal. It does not discard active Unity job state or release the queue without stop confirmation.
- These changes do NOT establish or fix the exact cause of the second observed blockage. Inspect cancellation diagnostics on Unity 6 after updating the package; do not treat host-project tests as proof of Unity 6 recovery.

Validation: local Unity 2022.3.62f2 compilation passed; TestCancellationPolicyTests passed 8/8, including unique legacy job selection and refusing ambiguous active jobs. Plugin ZIP rebuilt and validated. WaterWalk was inspected read-only and still uses 0.25.0; no editor restart, process termination, or queue-file modifications were performed.
