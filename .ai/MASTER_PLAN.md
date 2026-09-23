# NEWERP AI Master Plan

The pipeline processes `ERP-NNN.json` files in lexical order. A task moves through:

`pending -> in_progress -> validation -> completed -> Git checkpoint -> next task`

Failures are returned to Cline for up to the configured maximum attempts. Exhausted attempts, protected paths, high-risk validation and ambiguous recovery stop at Human Gate. `deferred` and `skipped` tasks do not block the queue.

The queue runner is `scripts/run-pipeline.ps1`. It holds an exclusive lock, invokes the single-task orchestrator repeatedly, and stops only when the queue is empty, a Human Gate is reached, or a failure needs attention.

Business tasks must remain small, declare narrow `allowed_paths`, use measurable acceptance criteria, and default to the `safe` validation profile.
