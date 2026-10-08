# 定金申请单来源销售订单显式选择（ERP-390）

## 1. 目标与范围

把定金申请单（`FinanceDepositApply`）的「来源销售订单」从「只能手工填 Id」升级为**显式有界选择**：

- 定金申请单表单（`modules-finance.js` 的 `deposit-apply` 模块）新增可选字段 `salesOrderId`，声明式选择器
  `selector: 'finance-apply-sales-order-source'`；
- 服务端在**既有「定金申请单」（`deposit-apply`）菜单 + 既有「销售订单」（`sales-order`）菜单 + 实时客户数据范围**
  之内，按**精确客户**与**归一化币种**返回有界分页候选；操作员显式选定后只回填**权威来源 Id**；
- 最终保存仍由既有 ERP-381 生命周期护栏（`FinanceDepositApplyLifecycleRules`）按 Id 精确复核来源，
  **候选选择不等于授权**。

本功能只做只读候选 / 只读展示 + 来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写订单 / 申请单 / 客户主数据，
不做任何汇率换算或余额断言，不新增表 / 列 / 菜单 / 权限或用户授权，也不提供匿名 / 管理员降级。

## 2. 接口

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/finance/deposit-applies/sales-order-candidates?customerId=&currency=&keyword=&page=&pageSize=` | 来源销售订单候选（只读、有界、分页）：`FinanceApplySalesOrderCandidatePageDto` |
| GET | `/api/finance/deposit-applies/{id}/sales-order-source` | 已存储来源只读展示（详情 / 重开）：`FinanceApplySalesOrderSourceViewDto` |

两者都先做**实时授权**，再做任何计数 / 取数：

1. 实时启用身份（缺失 / 非法 → 未认证；不存在 / 已删除 → 未认证；禁用 → 权限不足）；
2. 既有「定金申请单」菜单（`deposit-apply`）+ 既有「销售订单」菜单（`sales-order`，非特权账号必须显式具备）；
3. 候选：**精确客户**必须落在当前账号客户数据范围（越界 → 权限不足）；展示：按申请单**库中已存储客户**
   复核范围（越界 fail closed）。

## 3. 候选口径（`FinanceApplySalesOrderSourceService`）

- **精确客户**：`customerId` 必须为正整数，否则参数错误（绝不返回任意客户订单）；
- **未删除**：只返回 `!IsDeleted` 的销售订单；
- **状态**：`FinanceDepositApplyLifecycleRules.IsEligibleNewSource` —— 已取消订单保留在列表但标记
  `eligible=false` + 不可选原因（历史已记录的链接只读保留，绝不静默重绑定）；
- **归一化币种**：`currency` 按既有系统币种口径归一化后与订单币种**精确比对**（`Ordinal`）；不一致时标记
  `eligible=false` + 币种原因，**绝不做汇率换算**；
- **有界**：关键字去首尾空白并截断到 100 字符；页码收敛到 `1..10000`；每页条数收敛到 `1..100`（默认 20）；
  **先归一化再 `CountAsync` 与分页**；
- **有界 DTO 字段**：只暴露 `Id / 单号 / 日期 / 客户 Id + 名称 / 币种 / 状态 / 可用性`，绝不暴露金额 / 条款 / 余额；
- **无猜测旁路**：候选只按精确客户列出，绝不提供按猜测 Id 直取任意客户订单的接口。

## 4. 已存储来源只读展示

`FinanceApplySalesOrderSourceViewDto` 复用 `FinanceDepositApplyLifecycleRules.DescribeStoredSourceAsync` 的显式状态文案，
并补充结构化字段（订单号 / 日期 / 币种 / 状态）：

- 未关联（历史）→ `linked=false` + 「当前未关联来源销售订单」；
- 已关联且来源有效 → `linked=true, eligibleForNewLink=true` + 订单号；
- 来源已取消 → `eligibleForNewLink=false` + 「来源销售订单已取消，链接只读保留」；
- 来源已删除 / 无法解析 → `unavailable=true` + 「来源不可用，原链接原样保留」；
- 来源币种事后不一致 → `eligibleForNewLink=false`（链接原样保留）。

**绝不**写库、**绝不**重绑定 / 清除链接、**绝不**因来源失效而抛异常（历史必须可读）。

## 5. 最终保存（ERP-381 生命周期规则）

创建（`POST /api/finance/deposit-applies`）与修改（`PUT /api/finance/deposit-applies/{id}`）继续调用
`FinanceDepositApplyLifecycleRules.ResolveSourceSalesOrderAsync`：

- 未提供来源（`null` / `<= 0`，表单留空归一化为 `null`）→ 保留历史「未关联」语义，**不要求**「销售订单」菜单；
- 提供来源 → 必须按 Id 精确解析到**既有、未删除、未取消、同客户且币种兼容**的销售订单，
  且当前账号具备既有「销售订单」菜单授权；
- 伪造 / 已取消 / 跨客户 / 币种不一致来源一律 fail closed（`NotFound` / `RuleConflict`），且**绝不消耗申请单号、
  绝不落半成品**；
- 关系型后端在同一事务内先取**来源销售订单行锁**、再取**申请单行锁**，锁内权威复核后才写库（ERP-381 锁序）。

表单**显式断开**：`salesOrderId` 留空提交为 `0`，服务端归一化为 `null`（真正的未关联）。

## 6. 前端选择器（`finance-apply-sales-order-source.js`）

- `openFinanceApplySalesOrderSourcePicker(applyId)`：新建（0）与已保存单据都可用；读取 `f_customerId` / `f_currency`
  作为精确上下文，缺失客户时显式提示且**保留表单状态**；
- 只接受服务端 `eligible=true` 的候选；已取消 / 币种不一致候选显式标注不可选；
- 选定后**只回填 `f_salesOrderId`**，绝不臆造订单号、绝不自动改动金额 / 汇率 / 币种；
- **客户 / 币种变更即失效**：`f_customerId` / `f_currency` 的 `change/input` 会作废在途请求并清除先前的来源选择；
- **陈旧响应丢弃**：候选请求带序号，客户 / 币种 / 关键字变化后旧响应一律不落状态；
- **重开**：调用已存储来源接口原样回显链接与显式标注（含已取消 / 不可用历史来源）；
- **显式断开**：提供「显式断开来源链接」按钮（仅待提交可编辑）；
- 失败（网络 / 权限 / 服务端）显式 `role="alert"` 提示并**保留表单既有输入**。

接入：`index.html` 加载 `/js/finance-apply-sales-order-source.js`；`modules-finance.js` 定金申请单字段声明
`selector: 'finance-apply-sales-order-source'`；钩子只包裹既有 `fieldHtml` / `openForm`，不影响其它单据。

## 7. 安全与验证

- 复用**既有** ERP 权限（`deposit-apply` / `sales-order` 菜单 + 实时客户数据范围），**不新增用户授权、无匿名 / 管理员兜底**；
- 单元测试：`src/ERP.UnitTests/DepositApplySalesOrderSourceTests.cs`（内存库）；
- SQL 集成测试：`src/ERP.IntegrationTests/DepositApplySalesOrderSourceSqlServerTests.cs`
  （GUID 独占 `NEWERP_AUTOTEST` 库，目标必须为 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security`，
  访问前 fail-closed 校验，绝不 drop / reset / 读取生产设置），含**两个独立连接竞态**：
  「来源销售订单取消 vs 新申请链接」与「同一待提交申请单链接 vs 断开来源」；
- 前端行为测试：`node tests/deposit-apply-sales-order-source.test.js`
  （create / edit / reopen / customer switch / currency switch / stale response / unlink / denied candidate / error + 接线契约）；
- 验收口径：**构建完成不等于阶段验收**，SQL 集成测试只有在受控 localdb 上真实执行通过才算验收证据。
