# NEWERP autonomous development plan

The durable loop is:

`ChatGPT plan -> GitHub task/status files -> local scheduler -> DeepSeek implementation -> Release build/fast tests -> commit/push -> next dependency-safe task`.

ChatGPT owns stage planning and replenishes a small dependency-aware queue. DeepSeek is the local code executor. `.ai/PROJECT_STATE.json` is the single machine-readable project status read by ChatGPT and GitHub.

During feature development the completion threshold is a successful Release build. Fast unit tests remain in the `safe` profile. Integration, real-browser, screenshot, visual, evidence-manifest and manual-review gates do not block feature delivery. Production deployment, irreversible data operations and secrets remain outside autonomous execution.

Each task receives at most three implementation/repair attempts. Build and test output is captured in full under `.ai/logs/`; a bounded structured tail is sent back to DeepSeek. When the repair budget is exhausted, the task is marked `blocked`, its error and attempted fix are written to the task result and project status, its changes are quarantined in a recoverable Git stash, and independent tasks continue.

Successful tasks are committed and pushed automatically. GitHub outages degrade only remote synchronization; local dependency-safe development continues and the status file records the pending sync.
