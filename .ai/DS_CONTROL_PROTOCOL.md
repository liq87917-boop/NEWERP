# DeepSeek Conversation Control Protocol

DeepSeek is the human-facing control plane. Git, `.ai/PROJECT_STATE.json`, task JSON files, results, decisions and browser evidence are the durable source of truth.

Every conversational command that changes execution state must be translated into one or more explicit file-backed operations:

- create work: add a narrowly scoped `ERP-NNN.json` task with dependencies and browser scenarios;
- start or continue: record the intent, then run `scripts/ai_pipeline.py run`;
- pause: run `scripts/ds_project_control.py pause` before the next task starts;
- resume: run `scripts/ds_project_control.py resume`, then resume the queue;
- approve high risk: create the Human Gate decision through `ai_orchestrator.py approve`;
- defer or retry: use the corresponding pipeline command and preserve the audit trail.

The queue is dependency-safe and self-healing rather than globally fail-closed. A failed, blocked, review-waiting, or Human-Gated task freezes only itself and tasks that depend on it; unrelated dependency-safe tasks continue. Exhausted task attempts are quarantined with their working copy preserved, while the controller restores a clean base and proceeds. Git/GitHub transport failures enter a remote-degraded mode and are retried later instead of stopping local development. Normal development uses a rolling batch: DeepSeek keeps a target working set of four queued tasks, replenishes when runnable work falls below two, and Cline executes them strictly one by one. Every successful task receives its own checkpoint commit; remote synchronization may catch up after a temporary outage. The local agent never treats an empty batch as project completion; it enters `replenishing` and keeps watching Git for the next DeepSeek-supplied batch.

`Cline` success means only `code_ready`. A business task becomes `completed` only when engineering validation passes and a real installed Microsoft Edge session passes the declared browser scenarios with a TRX file, browser metadata, screenshots and a SHA-256 evidence manifest. Missing browser infrastructure blocks the task; it does not downgrade acceptance.

While `completion_policy.defer_browser_during_development` is `true` (current development phase), the browser gate is deferred rather than waived: engineering validation (Release build plus the task's configured non-browser profile) is sufficient for the orchestrator to record `completed`, the browser status is recorded as `browser_deferred` in the results file, and the deferred browser scenarios stay attached to the task for the `FINAL-UI-ACCEPTANCE` phase. A deferred browser status must never be reported as `failed`, as `human_attention`, or as an accepted UI.

Only tasks explicitly marked `completion_mode: control_plane` may use non-browser completion, and only for changes to the automation control layer itself. Business logic must use `completion_mode: browser`.

## Rolling acceptance cadence

DeepSeek reviews the GitHub repository on an hourly cadence while development is active. Each review checks task JSON state, per-task commits, CI/validation evidence, queue health and blockers. When runnable work is below the low-water mark, DeepSeek adds the next safe batch from `.ai/FUNCTION_BACKLOG.md`. L3/L4, production database/OSS/deployment, irreversible operations and other explicit Human Gates are never auto-approved merely to keep the queue moving; DeepSeek should schedule independent safe work around those gates and surface the approval need to the user.


## Autonomy V2 failure policy

Routine engineering failures must not escalate to the user merely because one task cannot proceed. The controller classifies and contains failures, retries recoverable operations, quarantines exhausted task work, and keeps scanning the dependency graph for safe work. `human_attention` is reserved for conditions that cannot be resolved safely without a person: explicit L3/L4 approval, production/irreversible operations, missing credentials that require interactive authorization, unsafe merge conflicts, or material business ambiguity. Browser/UI failures are non-blocking while development browser deferral is enabled.
