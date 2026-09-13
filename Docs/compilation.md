# Compilation and waiting

`agentbridge compile` checks current inputs. A completed matching cycle supplies its result without acquiring an editor lease. This includes automatic Unity cycles and compiler errors without domain reload. A cache miss imports pending source changes and requests compilation only if the import did not already complete a matching cycle. Requests waiting for a running compilation reuse its completed result once Unity is idle.

`compile --fresh --note "specific diagnostic reason"` requests a new observation. A cycle completed while that request waited satisfies it. Concurrent requests can therefore share one run; a fresh request submitted after completion requests another. CLI and Editor both require the reason. Ordinary edits, a client timeout, contention, foreign errors, and unavailable or stale evidence are not reasons for forcing a run. `tests --fresh` retains its existing rerun semantics.

The compile reuse key includes source bytes and associated metadata, asmdefs/asmrefs/response files, DLL inputs, package manifests, project settings, resolved local package roots, Unity and Bridge versions, and the selected build target/group. File timestamps do not participate. Unreachable package inputs prevent reuse. Hidden and tilde-suffixed package content is ignored like Unity source imports. Old compile-cache entries are rejected by version.

Unity compilation events, rather than a domain reload timeout, confirm completion. Diagnostics and cycle identity survive reload in SessionState. An idle editor without a completion event reaches a diagnostic `runtime_error` (`compile_completion_missing` in logs), never success based on silence. A changing input cannot publish an automatic cycle into the cache. Evidence for a Bridge-owned validation is completed on both reload and no-reload paths.

`Cached` and `SourceTaskId` report reuse. An automatic run has a `UnityCompile_...` source identity, not a client task to wait on. Cache results remain a compilation reuse claim and are not relabelled as evidence-v1 input digests. Use relevant tests for behavioral acceptance.

## Waiting for another agent

`ForeignErrors` is a legacy indication of project errors outside the temporary task script. It does not identify an author. Classify responsibility using diagnostics, your own edits and available coordination scopes; an error in another file can be caused by your API change. Repair your own causes. Do not edit another agent's code without agreement.

For a confirmed external blocker, finish your terminal validation window and end any stopped edit grant so the owner can work. Use Bridge coordination first. When its information does not resolve the blocker, contact the owner through the agent host's available inter-agent channel with paths and diagnostics. Do not assume hosts can communicate across products or substitute email/chat messages to people for an agent channel.

Defer another check of known errors for about 120 seconds while continuing independent work, without holding or requesting a Unity window. This is a retry interval for an external compilation blocker, not queue polling: use event-driven `coord wait` for the queue. Check sooner when the owner signals readiness. Then read `status` and available coordination state. `CompilationState`, `LastCompileId` and `LastCompileFinishedUtc` describe the last observed compiler cycle, not proof for current disk contents. If compilation/import or Unity input writers remain active, defer the compile and continue independent work. When validation can proceed, acquire a new window if required and issue one ordinary `compile`. Unchanged errors reuse the cache. If the client timed out on an existing task, use `wait <TaskId>` instead of submitting another task. A single waiting interval is not a terminal blocker; report prolonged lack of progress with the actual unresolved dependency.

## Regression checks

- `dotnet run --project AgentBridgeCompile.Tests -c Release` compiles the production executor, fingerprint and cache code against controlled Unity callbacks. The initial five cases failed on the original implementation. Coverage includes no-reload errors, missing completion, timestamp-only changes, external packages/context, fresh overlap, automatic cycles, duplicate refresh requests, and changed/restored inputs.
- `dotnet run --project AgentBridgeCli.Tests -c Release` includes the reason requirement; it failed before the CLI change.
- `dotnet run --project AgentBridgeCoordination.Tests -c Release -- --group all` verifies coordination and evidence inputs.
- `pwsh -File scripts/verify-compilation.ps1` uses an idle Unity test host. It checks three concurrent diagnostic requests, a deliberate owned compiler error, cached repetition, repair, and cleanup. It writes a JSON report under the host's `Temp/AgentBridge/` and removes only its own source probe and metadata. Do not run it against a shared busy host.
