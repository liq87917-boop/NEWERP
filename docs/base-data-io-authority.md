# 基础资料导入导出实时授权与客户数据范围（ERP-440）

> 阶段 3 · 运营主数据权限闭环 · 复用既有功能菜单与 ERP-097 数据范围

## 1. 目标

`api/base/io/{resource}/export`、`api/base/io/{resource}/import-template`、`api/base/io/{resource}/import`
三个路由此前只有 `[Authorize]`（任意已认证账号即可全量导出客户 / 供应商 / 员工 / 商品 / 仓库，
并批量插入任意主数据），绕过了既有 `BaseDataControllers` 的菜单授权与
`SalespersonDataScopeService`（ERP-097）客户数据范围。

本改动把这三个路由纳入与其它模块一致的实时授权与数据范围护栏：**读取 / 计数 / 写入任何一行之前**
先解析 **实时身份 → 账号状态 → 该资源既有功能菜单 → 权威客户数据范围（仅客户资源）**。

只**复用**既有分层架构与既有权限模型，**不新增**任何用户授权、表 / 列 / 菜单、匿名 / 管理员兜底，
导出 / 导入模板契约、逐行成功 / 失败报告、2000 行上限与文件格式错误一律保持不变。

## 2. 口径

| 项 | 口径 |
|---|---|
| 身份 | 每次请求按 `ClaimTypes.NameIdentifier` 解析：缺失 / 非正整数 → `2000` 未认证；账号不存在或已 `IsDeleted` → `2000`；`Status != Enabled` → `2002` 权限不足 |
| 菜单 | 资源键映射到**既有**功能菜单（与 `SeedData.Menus` / `SchemaUpgrader` 同源）；**非特权账号**必须显式具备，撤销后下一次请求立即收敛；**特权账号**（超级管理员 / 系统内置角色 / 显式特权角色，与 ERP-097 同源）继承既有全部访问 |
| 数据范围 | 客户资源复用 `SalespersonDataScopeService`（ERP-097 唯一权威口径）：特权账号全量；受限账号 `AllowedCustomerIds` 为 `BaseCustomer.EmpId == 本人` 的客户 Id 集合（未映射业务员时为空集合，fail closed） |
| 导出下推 | 客户导出在读取实体时按范围过滤（`SalespersonDataScopeService.FilterByCustomer`），受限账号只看到本人客户，绝不「先查全量再内存过滤」 |
| 导入范围 | 客户资源逐行复核：受限账号新增客户行 `EmpId` 必须等于本人映射员工 Id；越界 / 未指定业务员的行按失败行报告（不改写、不新增，也不中止其他行） |
| 保持契约 | 导出列 / 导入模板列 / 逐行成功失败统计 / 2000 行上限 / `.xlsx` 与解析错误文案均不变；不支持的资源仍返回既有 `1001` 提示 |

## 3. 资源 → 既有功能菜单映射

| 资源键 | 既有功能菜单编码 | 菜单文案 | 客户数据范围 |
|---|---|---|---|
| `customers` | `customer` | 客户资料 | ✅ |
| `suppliers` | `supplier` | 供应商资料 | — |
| `employees` | `employee` | 员工资料 | — |
| `expense-accounts` | `expense-account` | 费用科目 | — |
| `warehouses` | `warehouse` | 仓库资料 | — |
| `products` | `product` | 商品资料 | — |
| `tax-refunds` | `tax-refund` | 出口退税台账 | — |
| `other-infos` | `other-info` | 其他资料 | — |

映射集中在 `BaseDataIoAuthorizationRules`；控制器与授权规则同源，**不新增任何菜单**。

## 4. 路由矩阵（全部先授权）

| 路由 | 先授权 | 客户数据范围 | 数据库写入 |
|---|---|---|---|
| `GET api/base/io/{resource}/export` | ✅ 身份 + 状态 + 菜单 | ✅ 客户资源按范围过滤 | — |
| `GET api/base/io/{resource}/import-template` | ✅ 身份 + 状态 + 菜单 | —（模板不含数据） | — |
| `POST api/base/io/{resource}/import` | ✅ 身份 + 状态 + 菜单 | ✅ 客户资源逐行复核 | 逐行写入，越界 / 无效行按失败行报告 |

授权判定严格先于任何实体读取、计数与 `dbContext.Add` / `SaveChangesAsync`：被拒绝的请求不加载、
不写入任何客户 / 供应商 / 员工 / 商品行。

## 5. 错误信息（API 直接返回）

| 场景 | 错误码 | 说明 |
|---|---|---|
| 无身份 / 非法身份 | `2000` | `BaseDataIoAuthorizationRules.UnauthorizedText` |
| 账号不存在 / 已删除 | `2000` | `UserDeletedText` |
| 账号已禁用 | `2002` | `UserDisabledText` |
| 非特权账号缺少该资源既有功能菜单 | `2002` | `MenuDeniedText`（文案含「模块授权」） |
| 客户导入行业务员越界（非本人 / 未指定） | `2002`（逐行，不计入失败统计） | `CustomerRowOutOfScopeText` |

拒绝 / 越界行一律 fail closed，不泄露任何范围外数据，也不降级为匿名 / 管理员。

## 6. 边界

- 不新增表 / 列 / 菜单 / 角色 / 用户授权，不改任何 schema，不引入生产凭据或外部调用。
- 不改变导出 / 导入模板契约、逐行报告与 2000 行上限；不改变文件格式错误文案。
- 授权只做纯判定与有界只读查询，绝不落库、绝不改写历史主数据。
- 每次请求重新解析（不缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛。
