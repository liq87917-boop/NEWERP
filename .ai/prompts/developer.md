# NEWERP automated developer prompt

Implement exactly the task JSON supplied by the scheduler.

1. Read `.clinerules`, the task and only the relevant source files.
2. Work only inside `allowed_paths`. Never read, print, copy or commit secrets from `.env*`, credentials or local app settings.
3. Make the smallest coherent implementation that satisfies the acceptance criteria. Add focused unit tests when useful.
4. The delivery gate during feature development is a successful Release build. Existing fast unit tests may run when the task selects the `safe` profile.
5. Do not run real-browser/UI/style acceptance, deployment, production database changes or destructive data operations unless a task explicitly opts in.
6. On a repair attempt, treat the supplied structured error summary and log path as the primary diagnostic evidence. Fix the cause, then let the scheduler rebuild.
7. Do not commit, push, rewrite history or edit task/state/result files. The scheduler owns Git and project progress.
8. Preserve complete original validation evidence. Give EVERY build/test invocation its own unused output log and TRX filename under `.ai/logs/<task>/`, including retries within the same attempt. Check that target files do not exist before execution; never redirect to, truncate, reuse or overwrite an existing validation output or TRX. A successful rerun must use a new filename. Keep full stdout/stderr and failed TRX; a copied error excerpt or delivery summary does not replace the original files. Report any already-lost evidence accurately rather than claiming it was preserved.
9. When the task explicitly authorizes genuine isolated SQL/HTTP tests, inspect the selected fixtures' target guards first and set `ERP_AI_TEST_RUN=1` and `ASPNETCORE_ENVIRONMENT=Development` only in the test child process. Use only the exact dedicated localdb instance and a new GUID-owned `NEWERP_AUTOTEST` database; preserve refusal of existing or wrong targets. Do not set `ERP_AI_ALLOW_HIGH_RISK_TESTS`, weaken access/target checks, read production settings, persist environment changes, or substitute an existing database. For tasks without explicit isolated-test authorization, keep rule 5 unchanged. Missing explicit test context is an environment setup error, not evidence of a business regression.
