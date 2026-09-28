# NEWERP 全自动开发体系（V2）

## 架构

- ChatGPT：总脑，读取 GitHub 中的任务和 `.ai/PROJECT_STATE.json`，生成阶段计划并补充下一批任务。
- DeepSeek：本地执行模型，由 Cline 命令行调用，负责实现与自动整改。
- GitHub：任务、提交、构建结果和进度状态的中转载体。
- 本地调度中心：`scripts/ai_pipeline.py` + `scripts/ai_orchestrator.py`，按依赖选取任务、调用 DeepSeek、验证、提交、推送并继续滚动。

## 开发期门槛

默认以 Release 编译成功为完成门槛；`safe` 档同时运行已有快速单元测试。数据库集成测试、真实浏览器、UI 样式、截图证据和人工复核不阻塞功能开发，仅在显式手动触发时运行。生产部署、不可逆数据操作和 Secret 永不自动执行。

## 自动故障闭环

每个任务最多连续整改 3 次。每次编译或测试失败都会把完整输出保存到 `.ai/logs/`，并把结构化错误摘要回送 DeepSeek。超过次数后：

1. 将任务标记为 `blocked`；
2. 把最后错误、尝试次数、模型和可恢复的 Git stash 写入任务结果；
3. 更新 `.ai/PROJECT_STATE.json`；
4. 调度器继续选择不依赖该 blocker 的任务。

## 单一状态文件

`.ai/PROJECT_STATE.json` 维护 `current_task`、`queue`、`completed`、`blocked`、`last_build`、`last_error`、`last_deepseek_fix`、`git_sync` 和 `next_recommended_tasks`。文件随任务状态和 Git 同步变化刷新并随提交推送到 GitHub。

## 运行

双击仓库根目录 `start_agent.bat`，或运行：

```powershell
py -3 scripts/ai_pipeline.py self-test
py -3 scripts/ai_pipeline.py queue
py -3 scripts/ai_pipeline.py run
```

本地 `.env.local` 继续由 `.gitignore` 排除；控制台和状态文件不得输出 Secret。

