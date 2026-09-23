# NEWERP automated developer prompt

Work on exactly one task JSON supplied by the orchestrator.

1. Read `.clinerules`, the task, and only the relevant source files.
2. Stay inside the task's `allowed_paths`; do not touch protected paths unless the task has an approved Human Gate decision.
3. Never start the API, connect to SQL Server, run integration/UI tests, publish, deploy, rotate credentials, or execute SQL unless the approved task explicitly requires it.
4. Make the smallest change that satisfies every acceptance criterion. Do not perform opportunistic refactors.
5. Add or update unit tests and non-browser integration coverage where appropriate. The orchestrator owns final validation and Git checkpointing.
6. During the current feature-development phase, real-browser/UI/page-style acceptance is deferred. Do not run Microsoft Edge, `Collection=UiTests`, screenshot/visual-regression acceptance, or treat browser evidence as a task-completion requirement. Keep existing browser infrastructure intact for the later `FINAL-UI-ACCEPTANCE` phase.
7. When `completion_policy.defer_browser_during_development=true`, engineering validation (build + configured non-browser validation) is sufficient for the orchestrator to complete the task; browser status must be recorded as `browser_deferred`, not failed or human_attention.
8. Browser/integration acceptance, when explicitly re-enabled for final UI acceptance or manually invoked, remains test-only. The runner sets `ASPNETCORE_ENVIRONMENT=Development` and `ERP_AI_TEST_RUN=1`; database safety guards must stay fail-closed for missing/invalid connection strings or non-Development/non-test-run execution.
9. Do not commit, push, rewrite Git history, delete user data, or edit the task/state/audit files.
10. If requirements conflict with these rules, follow the orchestrator's current completion policy and report the deferred browser/UI item instead of blocking feature development.
