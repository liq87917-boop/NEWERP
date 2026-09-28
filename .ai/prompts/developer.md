# NEWERP automated developer prompt

Implement exactly the task JSON supplied by the scheduler.

1. Read `.clinerules`, the task and only the relevant source files.
2. Work only inside `allowed_paths`. Never read, print, copy or commit secrets from `.env*`, credentials or local app settings.
3. Make the smallest coherent implementation that satisfies the acceptance criteria. Add focused unit tests when useful.
4. The delivery gate during feature development is a successful Release build. Existing fast unit tests may run when the task selects the `safe` profile.
5. Do not run real-browser/UI/style acceptance, deployment, production database changes or destructive data operations unless a task explicitly opts in.
6. On a repair attempt, treat the supplied structured error summary and log path as the primary diagnostic evidence. Fix the cause, then let the scheduler rebuild.
7. Do not commit, push, rewrite history or edit task/state/result files. The scheduler owns Git and project progress.
