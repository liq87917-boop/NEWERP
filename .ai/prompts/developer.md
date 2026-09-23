# NEWERP automated developer prompt

Work on exactly one task JSON supplied by the orchestrator.

1. Read `.clinerules`, the task, and only the relevant source files.
2. Stay inside the task's `allowed_paths`; do not touch protected paths unless the task has an approved Human Gate decision.
3. Never start the API, connect to SQL Server, run integration/UI tests, publish, deploy, rotate credentials, or execute SQL unless the approved task explicitly requires it.
4. Make the smallest change that satisfies every acceptance criterion. Do not perform opportunistic refactors.
5. Add or update unit tests where appropriate. The orchestrator owns final validation and Git checkpointing.
6. For `completion_mode=browser`, add or update Selenium acceptance coverage for every `browser_acceptance.scenarios` item and capture visual evidence through `UiTestFixture.CaptureEvidence`. Your final text is not proof of completion.
7. Treat Cline completion as `code_ready` only. The orchestrator alone may declare `completed`, after a real installed Microsoft Edge session passes and produces the required screenshots/TRX evidence.
8. Browser/integration acceptance in the current development phase is test-only. The acceptance runner sets `ASPNETCORE_ENVIRONMENT=Development` and `ERP_AI_TEST_RUN=1`. Database safety guards must use this explicit test-run context (plus a non-empty valid target connection) as the approval boundary. Do not classify or reject the acceptance database merely because it matches `deploy/appsettings.Production.json`; that deploy file is not a production authority during development and must not be used as a database-identity denylist. Keep fail-closed behavior for missing/invalid connection strings or non-Development/non-test-run execution.
9. Do not commit, push, rewrite Git history, delete user data, or edit the task/state/audit files.
10. If requirements conflict with these rules, stop and explain the conflict in your final response.
