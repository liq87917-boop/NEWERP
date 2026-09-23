# GPT Conversation Control Protocol

GPT is the human-facing control plane. Git, `.ai/PROJECT_STATE.json`, task JSON files, results, decisions and browser evidence are the durable source of truth.

Every conversational command that changes execution state must be translated into one or more explicit file-backed operations:

- create work: add a narrowly scoped `ERP-NNN.json` task with dependencies and browser scenarios;
- start or continue: record the intent, then run `scripts/ai_pipeline.py run`;
- pause: run `scripts/gpt_project_control.py pause` before the next task starts;
- resume: run `scripts/gpt_project_control.py resume`, then resume the queue;
- approve high risk: create the Human Gate decision through `ai_orchestrator.py approve`;
- defer or retry: use the corresponding pipeline command and preserve the audit trail.

The queue is fail-closed. The first non-terminal task controls progress; blocked, malformed, gated or failed work is never skipped implicitly. The target working set is three ready tasks with explicit `depends_on` edges.

`Cline` success means only `code_ready`. A business task becomes `completed` only when engineering validation passes and a real installed Microsoft Edge session passes the declared browser scenarios with a TRX file, browser metadata, screenshots and a SHA-256 evidence manifest. Missing browser infrastructure blocks the task; it does not downgrade acceptance.

Only tasks explicitly marked `completion_mode: control_plane` may use non-browser completion, and only for changes to the automation control layer itself. Business logic must use `completion_mode: browser`.
