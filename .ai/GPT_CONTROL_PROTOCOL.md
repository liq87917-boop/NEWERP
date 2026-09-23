# GPT Conversation Control Protocol

GPT is the human-facing control plane. Git, `.ai/PROJECT_STATE.json`, task JSON files, results, decisions and browser evidence are the durable source of truth.

Every conversational command that changes execution state must be translated into one or more explicit file-backed operations:

- create work: add a narrowly scoped `ERP-NNN.json` task with dependencies and browser scenarios;
- start or continue: record the intent, then run `scripts/ai_pipeline.py run`;
- pause: run `scripts/gpt_project_control.py pause` before the next task starts;
- resume: run `scripts/gpt_project_control.py resume`, then resume the queue;
- approve high risk: create the Human Gate decision through `ai_orchestrator.py approve`;
- defer or retry: use the corresponding pipeline command and preserve the audit trail.

The queue is fail-closed. The first non-terminal task controls progress; blocked, malformed, gated or failed work is never skipped implicitly. Normal development uses a rolling batch: GPT keeps a target working set of four queued tasks, replenishes when runnable work falls below two, and Cline executes them strictly one by one. Every completed task must pass engineering validation and receive its own git commit/push before the next task starts. The local agent never treats an empty batch as project completion; it enters `replenishing` and keeps watching Git for the next GPT-supplied batch.

`Cline` success means only `code_ready`. A business task becomes `completed` only when engineering validation passes and a real installed Microsoft Edge session passes the declared browser scenarios with a TRX file, browser metadata, screenshots and a SHA-256 evidence manifest. Missing browser infrastructure blocks the task; it does not downgrade acceptance.

While `completion_policy.defer_browser_during_development` is `true` (current development phase), the browser gate is deferred rather than waived: engineering validation (Release build plus the task's configured non-browser profile) is sufficient for the orchestrator to record `completed`, the browser status is recorded as `browser_deferred` in the results file, and the deferred browser scenarios stay attached to the task for the `FINAL-UI-ACCEPTANCE` phase. A deferred browser status must never be reported as `failed`, as `human_attention`, or as an accepted UI.

Only tasks explicitly marked `completion_mode: control_plane` may use non-browser completion, and only for changes to the automation control layer itself. Business logic must use `completion_mode: browser`.

## Rolling acceptance cadence

GPT reviews the GitHub repository on an hourly cadence while development is active. Each review checks task JSON state, per-task commits, CI/validation evidence, queue health and blockers. When runnable work is below the low-water mark, GPT adds the next safe batch from `.ai/FUNCTION_BACKLOG.md`. L3/L4, production database/OSS/deployment, irreversible operations and other explicit Human Gates are never auto-approved merely to keep the queue moving; GPT should schedule independent safe work around those gates and surface the approval need to the user.
