# NEWERP AI Master Plan

GPT conversation is the user-facing controller; the repository is the durable source of truth. Each conversation command is recorded in `.ai/control/` and `.ai/audit.jsonl` before it changes queue execution.

The rolling work set targets three tasks. `depends_on` forms a validated acyclic graph, while lexical task order remains deterministic. The first non-terminal task is authoritative: malformed, blocked, failed, in-progress or unapproved work stops the queue. Later tasks are never skipped implicitly.

Business-task lifecycle:

`pending -> in_progress -> engineering validation -> code_ready -> real Edge acceptance -> completed -> Git checkpoint -> optional push`

`Cline` output and exit status are implementation signals only. They cannot produce `completed`. Real-browser acceptance must produce a passing TRX, browser-session metadata, screenshots and a SHA-256 manifest under `.ai/evidence/ERP-NNN/`.

Browser failures are returned to Cline within the retry budget. Missing browser infrastructure blocks the task. L3/L4 gates always require explicit human approval. Push recovery may rebase and retry the completed checkpoint, but must never rerun implementation or browser acceptance for a task already completed locally.

Only automation control-layer migrations may explicitly use `completion_mode: control_plane`. Every ERP business task defaults to `completion_mode: browser`.
