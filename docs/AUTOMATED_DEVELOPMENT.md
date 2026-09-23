# NEWERP 自动化开发流程

## 当前状态

自动化控制层已经升级为 GPT 对话控制。项目具备任务 JSON、状态管理、Cline 执行、路径审计、失败重试、三任务滚动队列、依赖图、Git checkpoint、Human Gate、真实浏览器验收和审计记录。

完成定义已经改变：`Cline` 正常退出只表示 `code_ready`。普通业务任务只有在工程验证通过，并由本机真实 Microsoft Edge 完成任务声明的场景、生成 TRX、浏览器元数据、截图及 SHA-256 清单后，才能写入 `completed`。浏览器环境缺失时任务进入 `blocked`，不会被当作成功，也不会跳过继续执行后续任务。

已确认的技术基线：.NET 8、ASP.NET Core Web API、EF Core 8、SQL Server、xUnit 与 Selenium。Release 编译为 0 警告、0 错误，165 个单元测试通过。

源代码配置中的明文凭据已替换为空值或示例占位符。旧的数据库和 OSS 凭据仍必须在对应服务端轮换，因为本地代码修改不能撤销已经签发的外部凭据。

## 日常运行

你可以直接在 GPT 对话中说“创建任务”“继续”“暂停”“批准 ERP-NNN”或“重试 ERP-NNN”。GPT 会把意图写入 `.ai/control/`，再操作项目内的任务和队列；对话不是唯一状态来源，Git 中的控制文件才是可恢复事实源。

1. 从 `.ai/tasks/_TEMPLATE.json` 复制并建立一个 `ERP-NNN.json`，或让 GPT 创建。
2. 写明验收标准、允许修改的路径、验证 profile 与风险等级。
3. 查看状态：`scripts/status-ai.ps1`。
4. 预览发送给 Cline 的完整任务：`scripts/run-ai.ps1 -DryRun`。
5. 执行下一个任务：`scripts/run-ai.ps1`。

完整队列使用 `scripts/run-pipeline.ps1`。它会按任务编号和 `depends_on` 连续执行所有 `pending`/`retry` 任务，直到队列清空、GPT 暂停、遇到 Human Gate 或需要人工处理的失败。队首任务若处于 blocked/failed/in_progress 或元数据错误，流水线会 fail-closed，绝不会隐式跳到后续任务。中断后使用 `scripts/resume-pipeline.ps1`；运行前可用 `scripts/test-pipeline.ps1` 检查 Git、Cline、.NET、任务 JSON、依赖图、浏览器门禁和保护规则。

GPT 控制状态：`scripts/gpt-control.ps1 -Command status`。

GPT 暂停：`scripts/gpt-control.ps1 -Command pause -Summary "暂停原因"`。

GPT 恢复：`scripts/gpt-control.ps1 -Command resume -Summary "恢复原因"`。

任务延期：`py -3 scripts/ai_pipeline.py defer ERP-NNN --by "姓名" --note "原因"`。

失败任务重新入队：`py -3 scripts/ai_pipeline.py retry ERP-NNN --by "姓名" --note "处理说明"`。

仅重试 Git push：`py -3 scripts/ai_pipeline.py retry-push`。push 恢复不会重新执行已经完成的开发和测试。

新任务可直接创建，无需手工编号：

`scripts/new-ai-task.ps1 -Title "任务标题" -Description "目标和边界" -AcceptanceCriteria "验收条件1","验收条件2" -AllowedPaths "src/ERP.Application/**","src/ERP.UnitTests/**" -ValidationProfile safe -Risk low`

系统会选择下一个 `ERP-NNN`，写入任务 JSON、更新项目状态、记录审计并创建 Git checkpoint；高风险 profile 或受保护路径会自动要求 Human Gate。使用 `py -3 scripts/ai_pipeline.py queue` 查看全部任务状态。

本地启动使用根目录的 `.env.local`。填写轮换后的开发密钥后运行 `start-dev.ps1`，脚本会校验格式、必填项和 JWT 长度，再把变量加载到当前 API 子进程；变量值不会写入控制台或 Git。

Runner 每次只执行一个任务。Cline 修改完成后先跑工程验证，再跑真实 Edge 验收。浏览器失败会把证据位置反馈给 Cline，最多尝试三次；成功后才写入结果与审计记录并创建 Git commit。超过上限、违反路径边界、浏览器基础设施缺失或 checkpoint 失败时会停止并进入 Human Gate。

队列目标大小为 3：保持当前任务和最多两个后续任务，后续任务必须声明依赖。自动补充的任务不能绕过队首失败或 Human Gate。

当前尚未配置 Git remote，所以 `.ai/config.json` 中 `auto_push` 保持为 `false`。配置 remote 并确认分支保护后才能启用。

## Human Gate

高风险任务执行前需要批准：

`py -3 scripts/ai_orchestrator.py approve ERP-NNN --by "姓名" --note "批准的范围和原因"`

以下范围默认受保护：

- 真实数据库、存储过程与集成测试；
- Selenium/UI 测试及启动 API；
- SQL、SchemaUpgrader 与 SeedData；
- appsettings、环境变量和密钥；
- deploy、release、checkpoints、日志与用户输入；
- 发布、部署、生产数据修复或不可逆操作。

高风险测试还有第二层开关：当前进程必须显式设置 `ERP_AI_ALLOW_HIGH_RISK_TESTS=APPROVED`。

## CI 分层

Push 和 pull request 默认只运行 Release build 与 `ERP.UnitTests`。数据库集成测试和 UI 测试只能通过 `workflow_dispatch` 手动开启，并分别绑定 `erp-integration`、`erp-ui` GitHub Environment。

在 GitHub 中为这两个 Environment 配置 required reviewers，并使用测试环境专用 secrets；不要配置生产数据库或生产 OSS 凭据。

## 新任务建议

- 一个任务只覆盖一个清晰的 ERP 模块或基础设施目标。
- `allowed_paths` 使用尽可能窄的路径。
- 普通业务改动使用 `safe` profile。
- 需要数据库或 UI 验证时，将任务设为 Human Gate，并由人工确认测试环境和数据清理方案。
