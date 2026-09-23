# NEWERP automated developer prompt

Work on exactly one task JSON supplied by the orchestrator.

1. Read `.clinerules`, the task, and only the relevant source files.
2. Stay inside the task's `allowed_paths`; do not touch protected paths unless the task has an approved Human Gate decision.
3. Never start the API, connect to SQL Server, run integration/UI tests, publish, deploy, rotate credentials, or execute SQL unless the approved task explicitly requires it.
4. Make the smallest change that satisfies every acceptance criterion. Do not perform opportunistic refactors.
5. Add or update unit tests where appropriate. The orchestrator owns final validation and Git checkpointing.
6. For `completion_mode=browser`, add or update Selenium acceptance coverage for every `browser_acceptance.scenarios` item and capture visual evidence through `UiTestFixture.CaptureEvidence`. Your final text is not proof of completion.
7. Treat Cline completion as `code_ready` only. The orchestrator alone may declare `completed`, after a real installed Microsoft Edge session passes and produces the required screenshots/TRX evidence.
8. Do not commit, push, rewrite Git history, delete user data, or edit the task/state/audit files.
9. If requirements conflict with these rules, stop and explain the conflict in your final response.
