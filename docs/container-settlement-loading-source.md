# 装柜结算单来源装柜清单显式选择（ERP-392）

## 1. 目标与范围

把装柜结算单（`FinanceContainerSettlement`）的「来源装柜清单」从「只能手工填 Id」升级为**显式有界选择**：

- 装柜结算单表单（`modules-finance.js` 的 `container-settlement` 模块）新增可选字段 `loadingListId`，声明式选择器
  `selector: 'container-settlement-loading-source'`；
- 服务端在**既有「装柜结算单」（`container-settlement`）菜单 + 既有「装柜清单」（`loading-list`）菜单 + 实时客户数据范围**
  之内，按**精确客户**返回有界分页候选；操作员显式选定后只回填**权威来源 Id**；
- 最终保存仍由既有 ERP-384 生命周期护栏（`FinanceContainerSettlementLifecycleRules`）在锁内按 Id 精确复核来源，
  **候选选择不等于授权**。

本功能只做只读候选 / 只读展示 + 来源 Id 回填：不锁库、不写库存 / 流水 / 财务、不改写清单 / 明细 / 参与方 / 客户主数据，
不执行任何结算计算 / 分摊 / 记账 / 核销，也**不自动改动结算总额 / 海运费 / 其他费用**，
不新增表 / 列 / 菜单 / 权限或用户授权，也不提供匿名 / 管理员降级。

## 2. 接口

| 方法 | 路由 | 说明 |
|---|---|---|
| GET | `/api/finance/container-settlements/loading-list-candidates?customerId=&keyword=&page=&pageSize=` | 来源装柜清单候选（只读、有界、分页）：`ContainerSettlementLoadingListCandidatePageDto` |
| GET | `/api/finance/container-settlements/{id}/loading-list-source` | 已存储来源只读展示（详情 / 重开）：`ContainerSettlementLoadingSourceViewDto` |

候选接口先做**实时授权**，再做任何计数 / 取数：

1. 实时启用身份（缺失 / 非法 → 未认证；不存在 / 已删除 → 未认证；禁用 → 权限不足）；
2. 既有「装柜结算单」菜单（`container-settlement`）；
3. 既有「装柜清单」菜单（`loading-list`，非特权账号必须显式具备）；
4. 候选：**精确客户**必须落在当前账号客户数据范围（越界 → 权限不足）；展示：按结算单**库中已存储客户**
   复核范围（越界 fail closed）。

## 3. 候选口径（`ContainerSettlementLoadingSourceService`）

- **精确客户**：`customerId` 必须为正整数，否则参数错误（绝不返回任意客户清单）；
- **权威归属（绝不按文本猜测）**：候选客户必须是该清单的一个**有效（启用、未删除）参与方客户**；若清单没有任何有效参与方，
  则要求持久化兼容客户字段 `ContainerLoadingList.CustomerId` 恰好等于该客户（历史单客户视图）。
  只按显式参与方与显式兼容客户字段判定，**绝不按柜号 / 单号 / 相似度猜测链接**，也不补链接 / 不回填；
- **共享柜 / 上游 fail closed**：复用 ERP-364 `LoadingListAuthorizationRules.ApplyScope`，共享柜的**每一个有效参与方客户**
  与**显式上游（预装柜单 → 订柜信息）客户**都必须在当前账号范围内，否则整张清单不返回（不通过共享柜 / 共享出运泄露他人客户）；
- **状态**：只返回 `!IsDeleted` 且 `Status != Cancelled` 的清单（已取消 / 已删除不作为新来源候选）；
- **有界**：关键字去首尾空白并截断到 100 字符；页码收敛到 `1..10000`；每页条数收敛到 `1..100`（默认 20）；
  **范围与归一化先于 `CountAsync` 与分页**；
- **有界 DTO 字段**：只暴露 `Id / 单号 / 日期 / 柜号 / 客户 Id + 名称 / 状态 / 可用性 / 有效参与方数 / 上游归属`；
  装柜结算单没有币种字段，因此**绝不返回金额、汇率或跨币种合计**，也绝不发明任何金额公式；
- **无猜测旁路**：绝不提供按猜测 Id 直取任意客户清单的接口。

## 4. 已存储来源只读展示

`ContainerSettlementLoadingSourceViewDto` 显式标注：

- 未关联（历史）→ `linked=false` + 「当前未关联来源装柜清单」；
- 已关联且来源有效 → `linked=true, eligibleForNewLink=true` + 单号；
- 来源已取消 → `cancelled=true, eligibleForNewLink=false` + 「来源装柜清单已取消，原链接只读保留」；
- 来源已删除 / 无法解析 → `unavailable=true` + 「来源不可用，原链接原样保留」。

**绝不**写库、**绝不**重绑定 / 清除链接、**绝不**因来源失效而抛异常（历史必须可读）。

## 5. 最终保存（ERP-384 生命周期规则）

创建（`POST /api/finance/container-settlements`）与修改（`PUT /api/finance/container-settlements/{id}`）继续调用
`FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync`：

- 未提供来源（`null` / `<= 0`）→ 保留历史「未关联来源」语义；
- 提供来源 → 必须按 Id 精确解析到**既有、未删除、未取消**的清单，清单参与方 / 上游客户在范围内，且结算客户与清单权威客户一致；
- 伪造 / 已取消 / 跨客户来源一律 fail closed（`Forbidden` / `RuleConflict`），且**绝不消耗单号、绝不落半成品**；
- 关系型后端在同一事务内先取**来源装柜清单行锁**、再取**结算单行锁**，锁内权威复核后才写库（ERP-384 锁序）。

## 6. 前端选择器（`container-settlement-loading-source.js`）

- `openContainerSettlementLoadingSourcePicker(settlementId)`：新建（0）与已保存单据都可用；读取 `f_customerId` 作为精确上下文，
  缺失客户时显式提示且**保留表单状态**；
- 只接受服务端 `eligible=true` 的候选；共享柜越范围 / 已取消候选显式标注不可选；
- 选定后**只回填 `f_loadingListId`**，绝不臆造柜号 / 清单号、绝不自动改动结算总额 / 海运费 / 其他费用；
- **客户变更即失效**：`f_customerId` 的 `change/input` 会作废在途请求并清除先前的来源选择；
- **陈旧响应丢弃**：候选请求带序号，客户 / 关键字变化后旧响应一律不落状态；
- **重开**：调用已存储来源接口原样回显链接与显式标注（含已取消 / 不可用历史来源）；
- **显式断开**：提供「显式断开来源链接」按钮（仅待提交可编辑）；
- 失败（网络 / 权限 / 服务端）显式 `role="alert"` 提示并**保留表单既有输入**。

接入：`index.html` 加载 `/js/container-settlement-loading-source.js`；`modules-finance.js` 装柜结算单字段声明
`selector: 'container-settlement-loading-source'`；钩子只包裹既有 `fieldHtml` / `openForm`，不影响其它单据。

## 7. 安全与验证

- 复用**既有** ERP 权限（`container-settlement` / `loading-list` 菜单 + 实时客户数据范围），**不新增用户授权、无匿名 / 管理员兜底**；
- 保留来源单据审计与原始失败日志；本护栏不改写任何历史证据；
- 单元测试：`src/ERP.UnitTests/ContainerSettlementLoadingSourceTests.cs`（内存库）；
- SQL 集成测试：`src/ERP.IntegrationTests/ContainerSettlementLoadingSourceSqlServerTests.cs`
  （GUID 独占 `NEWERP_AUTOTEST` 库，目标必须为 `(localdb)\NEWERP_AutoAcceptance` + `Integrated Security`，
  访问前 fail-closed 校验，绝不 drop / reset / 读取生产设置），含**两个独立连接竞态**：
  「装柜清单取消 vs 结算单创建链接」与「装柜清单取消 vs 结算单修改链接」；
- 前端行为测试：`node tests/container-settlement-loading-source.test.js`
  （create / edit / reopen / customer switch / stale response / unlink / denied candidate / error + 接线契约）；
- 验收口径：**构建完成不等于阶段验收**，SQL 集成测试只有在受控 localdb 上真实执行通过才算验收证据。
