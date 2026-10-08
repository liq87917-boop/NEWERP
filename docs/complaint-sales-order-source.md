# 客诉单来源销售订单显式选择（ERP-389）

## 1. 目标与范围

把客诉单（`FinanceComplaint`）的「来源销售订单」从「只能手工填 Id」升级为**显式有界选择**：

- 客诉单表单（`modules-finance.js` 的 `complaint` 模块）新增可选字段 `salesOrderId`，声明式选择器
  `selector: 'complaint-sales-order-source'`；
- 服务端在**既有「客诉单」（`complaint`）菜单 + 既有「销售订单」（`sales-order`）菜单 + 实时客户数据范围**
  之内，按**精确客户**返回有界分页候选；操作员显式选定后只回填**权威来源 Id**；
- 最终保存仍由既有 ERP-388 生命周期护栏（`FinanceComplaintLifecycleRules`）按 Id 精确复核来源，
  **候选选择不等于授权**。

本功能只做只读候选 / 只读展示 + 来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写订单 / 客诉单 / 客户主数据，
不新增表 / 列 / 菜单 / 权限或用户授权，也不提供匿名 / 管理员降级。

## 2. 接口

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/finance/complaints/sales-order-candidates?customerId=&keyword=&page=&pageSize=` | 来源销售订单候选（只读、有界、分页）：`ComplaintSalesOrderCandidatePageDto` |
| GET | `/api/finance/complaints/{id}/sales-order-source` | 已存储来源只读展示（详情 / 重开）：`ComplaintSalesOrderSourceViewDto` |

两者都先做**实时授权**，再做任何计数 / 取数：

1. 实时启用身份（缺失 / 非法 → 未认证；不存在 / 已删除 → 未认证；禁用 → 权限不足）；
2. 既有「客诉单」菜单（`complaint`）；
3. 候选：既有「销售订单」菜单（`sales-order`）+ **精确客户**必须落在当前账号客户数据范围（越界 → 权限不足）；
   展示：按客诉单**库中已存储客户**复核范围（越界 fail closed）。

## 3. 候选口径（`ComplaintSalesOrderSourceService`）

- **精确客户**：`customerId` 必须为正整数，否则参数错误（绝不返回任意客户订单）；
- **未删除**：只返回 `!IsDeleted` 的销售订单；**已取消**订单保留在列表但标记 `eligible=false` +
  不可选原因（历史已记录的链接只读保留，绝不静默重绑定）；
- **有界**：关键字去首尾空白并截断到 100 字符；页码收敛到 `1..10000`；每页条数收敛到 `1..100`（默认 20）；
  **先归一化再 `CountAsync` 与分页**；
- **有界 DTO 字段**：只暴露 `Id / 单号 / 日期 / 客户 Id + 名称 / 状态 / 可用性`，绝不暴露金额 / 币种 / 条款；
- **无猜测旁路**：候选只按精确客户列出，绝不提供按猜测 Id 直取任意客户订单的接口。

## 4. 已存储来源只读展示

`ComplaintSalesOrderSourceViewDto` 复用 ERP-388 `DescribeStoredSourceAsync` 的显式状态文案，并补充结构化字段：

- 未关联（历史）→ `linked=false` + 「未关联…」；
- 已关联且来源有效 → `linked=true, available` + 订单号；
- 来源已取消 → `eligibleForNewLink=false` + 「来源已取消，只读保留」；
- 来源已删除 / 无法解析 → `unavailable=true` + 「来源不可用，原链接原样保留」。

**绝不**写库、**绝不**重绑定 / 清除链接、**绝不**因来源失效而抛异常（历史必须可读）。

## 5. 最终保存（ERP-388 生命周期规则）

创建（`POST /api/finance/complaints`）与修改（`PUT /api/finance/complaints/{id}`）继续调用
`FinanceComplaintLifecycleRules.ValidateAndAuthorizeLinkAsync`：

- 未提供来源（`null` / `<= 0`，表单留空归一化为 `null`）→ 保留历史「未关联」语义，不要求「销售订单」菜单；
- 提供来源 → 必须按 Id 精确解析到**既有、未删除、未取消、同客户**的销售订单，且当前账号具备既有「销售订单」菜单授权；
- 伪造 / 已取消 / 跨客户来源一律 fail closed（`NotFound` / `RuleConflict`），且**绝不消耗客诉单号、绝不落半成品**；
- 关系型后端在同一事务内先取**来源销售订单行锁**、再取**客诉单行锁**，锁内权威复核后才写库（锁序与 ERP-347 / ERP-383 兼容）。

表单**显式断开**：`salesOrderId` 留空提交为 `0`，服务端归一化为 `null`（真正的未关联）。

## 6. 前端选择器（`complaint-sales-order-source.js`）

- `openComplaintSalesOrderSourcePicker(complaintId)`：新建（0）与已保存单据都可用；读取 `f_customerId` 作为
  精确客户，缺失时显式提示且**保留表单状态**；
- 只接受服务端 `eligible=true` 的候选；已取消候选显式标注不可选；
- 选定后**只回填 `f_salesOrderId`**，绝不臆造订单号、绝不自动改动数量 / 金额；
- **客户变更即失效**：`f_customerId` 的 `change/input` 会作废在途请求并清除先前的来源选择；
- **陈旧响应丢弃**：候选请求带序号，客户 / 关键字变化后旧响应一律不落状态；
- **重开**：调用已存储来源接口原样回显链接与显式标注（含已取消 / 不可用历史来源）；
- **显式断开**：提供「显式断开来源链接」按钮；
- 失败（网络 / 权限 / 服务端）显式 `role="alert"` 提示并**保留表单既有输入**。

接入：`index.html` 加载 `/js/complaint-sales-order-source.js`；`modules-finance.js` 客诉单字段声明
`selector: 'complaint-sales-order-source'`；钩子只包裹既有 `fieldHtml` / `openForm`，不影响其它单据。

## 7. 安全与验证

- 复用**既有** ERP 权限（`complaint` / `sales-order` 菜单 + 实时客户数据范围），**不新增用户授权、无匿名 / 管理员兜底**；
- 单元测试：`src/ERP.UnitTests/ComplaintSalesOrderSourceTests.cs`（内存库）；
- SQL 集成测试：`src/ERP.IntegrationTests/ComplaintSalesOrderSourceSqlServerTests.cs`
  （GUID 独占 `NEWERP_AUTOTEST` 库，目标必须为 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security`，
  访问前 fail-closed 校验，绝不 drop / reset / 读取生产设置），含**两个独立连接竞态**：
  「来源取消 vs 新客诉链接」与「同一待提交客诉单链接 vs 断开」；
- 前端行为测试：`node tests/complaint-sales-order-source.test.js`
  （create / edit / reopen / customer switch / stale response / denied candidate + 接线契约）；
- 验收口径：**构建完成不等于阶段验收**，SQL 集成测试只有在受控 localdb 上真实执行通过才算验收证据。
