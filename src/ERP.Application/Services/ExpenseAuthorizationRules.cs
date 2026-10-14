using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 费用单（<see cref="FinanceExpense"/>，<c>api/finance/expenses</c>）与装柜费用分摊证据
/// （<c>api/container/expense-allocation-evidence</c>）的实时授权、权威客户范围与写入校验护栏（ERP-385）。
/// <list type="number">
/// <item><b>实时授权</b>：每一个费用 CRUD / 读取 / 列表 / 导出（若存在）/ 传统「拼柜分摊」预览与生成 /
/// ERP-042 分摊批次预览 / 生成 / 台账 / 详情 / 作废 / ERP-060 分摊证据读取路由，在读取任何计数、生成单号或写入任何数据
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、
/// 既有「费用单」（<c>expense-bill</c>）菜单授权（非特权账号必须显式具备，未映射业务员 fail closed）与权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/>）。</item>
/// <item><b>权威归属</b>：费用单只按<b>持久化 <see cref="FinanceExpense.CustomerId"/></b> 判定，绝不按
/// <see cref="FinanceExpense.RefNo"/> / <see cref="FinanceExpense.CustomerName"/> 等自由文本猜测；受限账号不能读取 / 改写
/// 缺失权威归属（CustomerId 为空）的「无主」费用单，真正不受限（特权）的授权账号保留既有历史访问。</item>
/// <item><b>共享装柜清单与分摊批次</b>：批次的权威归属由其<b>显式</b>
/// <see cref="FinanceExpenseAllocationBatch.LoadingListId"/>（复用 ERP-364 参与方 / 上游客户范围）、
/// <see cref="FinanceExpenseAllocationBatch.SourceExpenseId"/> 来源费用客户与
/// <see cref="FinanceExpenseAllocationLine"/> 逐行生成客户共同证明 —— 任一客户越界即整条拒绝（不通过共享柜泄露他人客户）。</item>
/// <item><b>写入校验</b>：改写任何字段或生成任何行之前，校验拟议 / 已存储客户的完整范围、真实启用客户、受支持币种、
/// 取整后为正的金额与为正的汇率；被拒绝的编辑 / 删除 / 预览 / 生成 / 作废绝不改写原行、状态、审计与任何关联证据
/// （分摊批次只以显式作废（状态 0 + 原因）更正，保留历史）。</item>
/// <item><b>标量声明列精度（ERP-512 / ERP-538）</b>：权威建表脚本 <c>db_owner.FinanceExpenses</c> 把
/// 金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额声明为 <c>DECIMAL(18,4)</c>、汇率声明为
/// <c>DECIMAL(18,6)</c>。<b>客户端可持久化标量</b>（费用新增 / 修改的七个标量与传统分摊总额）按**声明精度逐位原样**
/// 判定：可表示的值**不取整**、原样落库往返；负数 / 过精度（第 5 / 第 7 位小数）/ 超出量级一律在**任何已跟踪写入
/// 之前**以既有受控错误拒绝（ERP-538 之前按 <c>DECIMAL(18,2)</c> 取整，会把真实可表示的 4 位小数金额与
/// 6 位小数汇率静默改写或拒绝）；既有的**派生**金额货币精度取整、分摊余差与比例算法保持不变；传统分摊与批次
/// 生成的派生金额 / 比例 / 折人民币金额仍在单号预约与任何写入之前预计算并校验；历史 / 被外部改写的越界来源费用
/// 拒绝新的分摊生成但保持可读（绝不静默截断 / 取整，也不改写历史）。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ExpenseBillController</c> 的每一个路由动作）解析后经
/// <see cref="EnsureMenuAuthorizedAsync"/> 实时解析为 <see cref="SalespersonDataScope"/> 并以非空范围传入；</para>
/// <para><b>与请求形状无关（ERP-496）</b>：上述判定由<b>每一个路由动作无条件</b>执行 —— 与 <c>Request.Path</c>
/// 是否赋值、请求形状、以及是否绑定 <c>HttpContext</c> <b>完全无关</b>。原 HTTP 入口过滤器
/// （<c>ExpenseRequestAuthorizationFilter</c>）与它的 <c>HttpContext.Items</c> 范围通道、以及「进程内调用范围只能为
/// <c>null</c>（= 不受限）」的旁路已<b>彻底移除</b>；空路径 / 已赋值路径 / 未绑定任何请求上下文的纯进程内直调对同一身份
/// 给出完全相同的判定，缺失 / 为零 / 未知 / 已删除身份一律按未认证拒绝，禁用账号 / 缺少或被撤销既有菜单一律按权限不足拒绝。
/// 契约见 <see cref="PathIndependenceText"/>。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 权限模型，不伪造任何授权，不记账、不生成
/// 凭证 / 收款 / 付款 / 结算单，也不改写任何历史留痕。</para>
/// </summary>
public static class ExpenseAuthorizationRules
{
    /// <summary>费用单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> / <c>SchemaUpgrader</c> 同源）</summary>
    public const string RequiredMenuCode = "expense-bill";

    /// <summary>费用单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "费用单";

    /// <summary>
    /// **派生金额**的既有货币精度取整口径（固定 2 位小数、0.5 进位，不使用银行家舍入）：
    /// 传统「拼柜分摊」的折人民币金额与 ERP-042 批次的逐行分摊金额 / 折人民币金额仍按它取整。
    /// <para>ERP-538：客户端提交的**标量**不再按本口径取整 —— 它们必须逐位原样落在声明列内
    /// （金额类 <see cref="PersistedAmountScale"/> = 4 位、汇率 <see cref="PersistedExchangeRateScale"/> = 6 位）。</para>
    /// </summary>
    public const int AmountDecimals = 2;

    /// <summary>
    /// 金额类持久化列的**声明小数位**（<c>Amount</c> / <c>AmountCny</c> / <c>TaxRate</c> / <c>TaxAmount</c> /
    /// <c>AllocationRatio</c> / <c>AllocatedAmount</c> 均为 <c>DECIMAL(18,4)</c>，与权威建表脚本
    /// <c>SchemaUpgrader</c> 第 13 段及 ERP-538 的 EF 映射一字同源）。
    /// </summary>
    public const int PersistedAmountScale = FinancePersistedAmountRules.DecimalScale4;

    /// <summary>
    /// 汇率持久化列的**声明小数位**（<c>ExchangeRate</c> 为 <c>DECIMAL(18,6)</c>，与权威建表脚本
    /// <c>SchemaUpgrader</c> 第 13 段及 ERP-538 的 EF 映射一字同源）。
    /// </summary>
    public const int PersistedExchangeRateScale = FinancePersistedAmountRules.DecimalScale6;

    /// <summary>主金额标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string AmountOverflowLabel = "费用金额";

    /// <summary>汇率标量超出声明列精度（<c>DECIMAL(18,6)</c>）时的拒绝文案前缀</summary>
    public const string ExchangeRateOverflowLabel = "汇率";

    /// <summary>折人民币金额标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string AmountCnyOverflowLabel = "折人民币金额";

    /// <summary>税率标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string TaxRateOverflowLabel = "税率";

    /// <summary>税额标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string TaxAmountOverflowLabel = "税额";

    /// <summary>分摊比例标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string AllocationRatioOverflowLabel = "分摊比例";

    /// <summary>分摊金额标量超出声明列精度（<c>DECIMAL(18,4)</c>）时的拒绝文案前缀</summary>
    public const string AllocatedAmountOverflowLabel = "分摊金额";

    /// <summary>系统支持的币种（与销售订单 / 收款单 / 采购订单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = { "CNY", "USD", "EUR", "HKD", "GBP", "JPY" };

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问费用单";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问费用单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问费用单（fail closed）";

    /// <summary>缺少既有「费用单」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「费用单」（expense-bill）模块授权：拒绝访问费用单 / 分摊批次 / 分摊证据"
        + "（fail closed，不返回 / 不修改任何费用数据）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（费用单操作员），不能访问费用单（fail closed，不泄露任何范围外单据）";

    /// <summary>受限账号访问缺失权威归属（CustomerId 为空）费用单的拒绝文案</summary>
    public const string UnlinkedExpenseText =
        "该费用单没有可判定的权威客户归属（持久化 CustomerId 缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）";

    /// <summary>客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该费用单 / 分摊批次的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>装柜清单无权威归属 / 已删除时作为分摊来源的拒绝文案</summary>
    public const string UnlinkedLoadingListText =
        "分摊批次的装柜清单没有可判定的权威客户归属（不存在 / 已删除 / 无有效参与方且兼容客户字段缺失）："
        + "受限账号拒绝访问（fail closed，不通过共享柜泄露）";

    /// <summary>客户不存在 / 已删除 / 已停用的拒绝文案</summary>
    public const string CustomerUnavailableText = "归属客户不存在、已删除或已停用，不能作为费用单客户";

    /// <summary>不支持币种的拒绝文案</summary>
    public const string UnsupportedCurrencyText =
        "币种不受支持：只允许 CNY / USD / EUR / HKD / GBP / JPY（不做汇率换算，也不接受未知币种）";

    /// <summary>金额非正的拒绝文案（ERP-538：金额逐位原样判定，不做任何取整 / 截断）</summary>
    public const string NonPositiveAmountText = "费用金额必须大于 0（按声明列精度逐位原样判定，不做任何取整 / 截断）";

    /// <summary>汇率非正的拒绝文案（ERP-538：汇率逐位原样判定，不做任何取整 / 截断）</summary>
    public const string InvalidRateText = "汇率必须大于 0（按声明列精度逐位原样判定，不做任何取整 / 截断）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "费用单（CRUD / 读取 / 列表 / 导出（若存在）/ 传统分摊 / ERP-042 分摊批次 / ERP-060 分摊证据）在读取任何计数、生成单号或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）、既有「费用单」（expense-bill）菜单授权与权威客户数据范围；"
        + "费用单只按持久化 CustomerId 判定，受限账号不能访问缺失权威归属的无主费用单，特权账号保留历史访问；"
        + "共享装柜清单与分摊批次要求显式 LoadingListId 的每一个有效参与方 / 上游客户、SourceExpenseId 来源费用客户与逐行生成客户全部在范围内；"
        + "写入前校验真实启用客户、受支持币种、取整后为正的金额与为正的汇率；身份只来自已认证请求主体，客户端提交体不能指定或扩大范围。"
        + "该判定由每一个路由动作无条件执行，与请求路径 / 请求形状 / 是否绑定 HttpContext 完全无关"
        + "（ERP-496 已移除请求形状探针与 HttpContext.Items 范围通道，见 PathIndependenceText）。";

    /// <summary>边界文案（不新增权限 / 表列，不记账，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护费用单与分摊批次 / 分摊证据的授权、范围与写入校验：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不写库存 / 资金 / 会计凭证，不产生收款、付款、核销、分摊入账或对账结论；"
        + "被拒绝的编辑 / 删除 / 预览 / 生成 / 作废不改写原费用行、批次状态、审计时间戳与任何关联证据；"
        + "分摊更正仍走 ERP-042 的显式作废（状态 0 + 原因），历史留痕保持不变；"
        + "不存在任何请求路径 / 请求形状 / 空请求上下文或数据库提供程序 / 测试专用开关的免授权回退。";

    /// <summary>
    /// 与请求形状无关的授权契约（ERP-496）：费用单路由（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除 /
    /// 传统分摊预览与生成 / ERP-042 分摊上下文 / 预览 / 生成 / 台账 / 详情 / 作废）的身份 / 账号状态 /
    /// 既有「费用单」（<c>expense-bill</c>）菜单判定必须在<b>每一个路由动作内无条件</b>执行，
    /// 与 <c>Request.Path</c> 是否赋值、请求形状、以及控制器是否绑定 <c>HttpContext</c> <b>完全无关</b> ——
    /// 空路径与已赋值路径口径完全一致，完全未绑定 <c>HttpContext</c> 的纯进程内直调同样执行本护栏；
    /// 缺失 / 为零 / 未知 / 已删除身份一律按未认证拒绝，禁用账号 / 撤销或缺少既有菜单一律按权限不足拒绝。
    /// </summary>
    public const string PathIndependenceText =
        "费用单（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除 / 传统分摊预览与生成 / ERP-042 分摊上下文 / "
        + "预览 / 生成 / 台账 / 详情 / 作废）的实时身份 / 账号状态 / 既有「费用单」（expense-bill）菜单判定与请求路径 / "
        + "请求形状 / 是否绑定 HttpContext 完全无关：原 HTTP 入口过滤器 ExpenseRequestAuthorizationFilter 与它的 "
        + "HttpContext.Items 范围通道、以及「进程内调用范围只能为 null（= 不受限）」旁路已彻底移除，"
        + "每个路由动作都先经 EnsureMenuAuthorizedAsync 解析出非空权威客户范围再读取任何计数 / 行 / 金额 / 分摊基数或写入；"
        + "空路径与已赋值路径口径完全一致，未绑定任何请求上下文的纯进程内直调同样无条件执行本护栏；"
        + "缺失 / 为零 / 未知 / 已删除身份按未认证拒绝，禁用账号与撤销 / 缺少既有菜单按权限不足拒绝；"
        + "绝无匿名 / 管理员回退，不读取环境变量、不区分数据库提供程序，也不存在任何测试专用放行开关。";

    // ==================== 1. 币种 / 金额 / 汇率纯校验 ====================

    /// <summary>币种规范化（去空白 + 大写；空值按 CNY）并校验必须落在受支持币种内（未知币种一律拒绝）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"{UnsupportedCurrencyText}（收到「{(currency ?? string.Empty).Trim()}」）");
        return value;
    }

    /// <summary>
    /// 金额校验（ERP-538）：必须**逐位原样**能在声明列 <c>DECIMAL(18,4)</c> 内存储且严格大于 0。
    /// <para>不再按货币精度取整（那会把真实可存储的 3 / 4 位小数金额静默改写为 2 位）：第 5 位小数（过精度）、
    /// 超出量级与零 / 负值一律以既有受控错误 <c>InvalidParameter</c> 拒绝；返回**入参原值**。</para>
    /// </summary>
    public static decimal NormalizeAmount(decimal amount)
    {
        if (amount <= 0m)
            throw BusinessException.InvalidParameter($"{NonPositiveAmountText}：当前值 {amount}");
        return EnsureWithinDeclaredPrecision(amount, PersistedAmountScale, AmountOverflowLabel);
    }

    /// <summary>
    /// 汇率校验（ERP-538）：必须**逐位原样**能在声明列 <c>DECIMAL(18,6)</c> 内存储且严格大于 0。
    /// <para>不再按货币精度取整（那会把真实可存储的 3~6 位小数汇率静默改写为 2 位）：第 7 位小数（过精度）、
    /// 超出量级与零 / 负值一律受控拒绝；返回**入参原值**。</para>
    /// </summary>
    public static decimal NormalizeRate(decimal exchangeRate)
    {
        if (exchangeRate <= 0m)
            throw BusinessException.InvalidParameter($"{InvalidRateText}：当前值 {exchangeRate}");
        return EnsureWithinDeclaredPrecision(
            exchangeRate, PersistedExchangeRateScale, ExchangeRateOverflowLabel);
    }

    /// <summary>
    /// 标量是否能在声明列 <c>DECIMAL(18,scale)</c> 内**逐位原样**存储（小数位不超过 <paramref name="scale"/>
    /// 且绝对值有界；只读判定，不抛异常，允许负值）。
    /// <para>区间口径复用系统唯一的标量边界规则 <see cref="FinancePersistedAmountRules"/>（支持 2 / 4 / 6），
    /// 不新增第二套常量；小数位判定与 SQL Server 写入取整同向（0.5 进位）。</para>
    /// </summary>
    public static bool IsWithinDeclaredPrecision(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.AwayFromZero) == value
           && FinancePersistedAmountRules.IsWithinDecimal18(value, scale);

    /// <summary>
    /// 校验（并按原值返回）标量能在声明列 <c>DECIMAL(18,scale)</c> 内逐位原样存储；过精度 / 超出量级即受控拒绝。
    /// <para>纯判定：**绝不**取整 / 截断 / 夹取入参，也不改写任何状态；<paramref name="amountLabel"/> 只用于错误文案。</para>
    /// </summary>
    public static decimal EnsureWithinDeclaredPrecision(decimal value, int scale, string amountLabel)
    {
        ArgumentNullException.ThrowIfNull(amountLabel);
        if (IsWithinDeclaredPrecision(value, scale)) return value;
        throw BusinessException.InvalidParameter(
            $"{amountLabel}超出可存储范围（DECIMAL(18,{scale})）：已拒绝，绝不静默截断或取整（当前值 {value}）");
    }

    /// <summary>
    /// 单个**客户端可持久化标量**的声明列精度判定（ERP-538：<c>DECIMAL(18,4)</c>）：
    /// 能在列内**逐位原样**表示的值照常放行（第 5 位小数 / 超出量级受控拒绝），纯判定，不取整。
    /// <para>本方法只判定「能否原样存储」；客户端可选标量的**非负**语义由
    /// <see cref="EnsureNonNegativeScalar"/> 在费用新增 / 修改路径上追加。</para>
    /// </summary>
    public static decimal EnsureScalarRepresentable(decimal value, string amountLabel)
        => EnsureWithinDeclaredPrecision(value, PersistedAmountScale, amountLabel);

    /// <summary>标量是否能在金额声明列（<c>DECIMAL(18,4)</c>）内逐位原样表示（只读判定，不抛异常）</summary>
    public static bool IsScalarRepresentable(decimal value)
        => IsWithinDeclaredPrecision(value, PersistedAmountScale);

    /// <summary>
    /// 客户端**可选**标量（折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额）的费用新增 / 修改写入校验
    /// （ERP-538）：必须非负（零值 = 未启用税 / 未分摊照常放行）且能逐位原样落在声明列 <c>DECIMAL(18,4)</c> 内；
    /// 负数、第 5 位小数与超出量级一律在**任何已跟踪写入之前**以既有受控错误拒绝。
    /// <para>本方法不为客户端标量发明任何业务等式，也不做自动修正。</para>
    /// </summary>
    public static decimal EnsureNonNegativeScalar(decimal value, string amountLabel)
    {
        ArgumentNullException.ThrowIfNull(amountLabel);
        if (value < 0m)
            throw BusinessException.InvalidParameter($"{amountLabel}不能为负数：当前值 {value}（费用标量不做负数登记）");
        return EnsureScalarRepresentable(value, amountLabel);
    }

    /// <summary>
    /// 传统「拼柜分摊」结果行的派生标量边界（分摊比例 / 分摊金额，声明列精度 <c>DECIMAL(18,4)</c>）：
    /// 预览与生成共用同一判定，在任何单号预约与写入之前执行；超出即受控拒绝（绝不静默截断 / 取整）。
    /// <para>派生值沿用既有取整口径（比例 4 位、金额货币精度 2 位），因此照常可在声明列内原样表示；
    /// 余差承接行的极小负残差仍按既有算法与既有 decimal 列语义放行，绝不改写余差算法。</para>
    /// </summary>
    public static void EnsureAllocateResultRepresentable(decimal ratio, decimal allocatedAmount)
    {
        EnsureWithinDeclaredPrecision(ratio, PersistedAmountScale, AllocationRatioOverflowLabel);
        EnsureWithinDeclaredPrecision(allocatedAmount, PersistedAmountScale, AllocatedAmountOverflowLabel);
    }

    /// <summary>
    /// 传统分摊行的折人民币金额（与既有口径逐字一致：<c>CNY</c> 原样、其余按 <c>Math.Round(分摊金额 × 汇率, 2)</c>
    /// 计算），并在**任何单号预约 / 写入之前**按折人民币声明列精度 <c>DECIMAL(18,4)</c> 判定（含乘法溢出防护）。
    /// <para>本方法**只追加**溢出与声明列精度判定：既有取整方式（含既有 <c>Math.Round</c> 语义）、币种口径与
    /// 赋值表达式语义保持不变，绝不静默改写既有折人民币口径。</para>
    /// </summary>
    public static decimal DeriveCnyAmount(decimal allocatedAmount, string? currency, decimal exchangeRate)
    {
        var rate = exchangeRate <= 0 ? 1m : exchangeRate;
        if ((currency ?? string.Empty) == CurrencyAmountRules.DefaultCurrency)
            return EnsureScalarRepresentable(allocatedAmount, AmountCnyOverflowLabel);

        var product = FinancePersistedAmountRules.SafeMultiply(
            allocatedAmount, rate, AmountCnyOverflowLabel);
        return EnsureScalarRepresentable(Math.Round(product, AmountDecimals), AmountCnyOverflowLabel);
    }

    /// <summary>
    /// 已存储费用单**参与派生计算**的标量只读复核（ERP-512 / ERP-538）：金额 / 汇率 / 折人民币金额必须能在各自
    /// 声明列（<c>DECIMAL(18,4)</c> / <c>DECIMAL(18,6)</c> / <c>DECIMAL(18,4)</c>）内**逐位原样**表示。
    /// <para>历史 / 被外部改写的越界（过精度 / 超出量级）值一律按规则冲突受控拒绝 ——
    /// 绝不静默截断、绝不改写已存储值（更正仍需显式作废 / 人工改写），也不新增任何草稿有效性闸门。</para>
    /// </summary>
    public static void EnsureStoredExpenseScalarsRepresentable(FinanceExpense stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var identity = string.IsNullOrWhiteSpace(stored.ExpenseNo) ? $"Id={stored.Id}" : stored.ExpenseNo.Trim();
        EnsureStoredScalar(stored.Amount, PersistedAmountScale, AmountOverflowLabel, identity);
        EnsureStoredScalar(stored.ExchangeRate, PersistedExchangeRateScale, ExchangeRateOverflowLabel, identity);
        EnsureStoredScalar(stored.AmountCny, PersistedAmountScale, AmountCnyOverflowLabel, identity);
    }

    private static void EnsureStoredScalar(decimal value, int scale, string label, string identity)
    {
        if (IsWithinDeclaredPrecision(value, scale)) return;
        throw BusinessException.RuleConflict(
            $"费用单「{identity}」的已存储{label}超出声明列可存储范围（DECIMAL(18,{scale})）：当前值 {value} —— "
            + "不能继续作为分摊来源（绝不静默截断或取整已存储值，如需更正请显式改写或改选来源费用）");
    }

    // ==================== 2. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「费用单」菜单授权三重实时校验（fail closed），返回本次请求的权威客户数据范围：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>expense-bill</c> 菜单授权或未映射业务员按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛；特权账号保留既有全部访问。
    /// <para>与请求形状无关（见 <see cref="PathIndependenceText"/>，ERP-496）：调用方必须在<b>每一个路由动作内无条件</b>
    /// 调用本方法，不得因为空路径、缺少身份、未绑定 <c>HttpContext</c> 或任何请求形状而跳过；
    /// 空路径 / 无上下文与已认证真实请求的判定口径完全一致。</para>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>
    /// 受限账号按持久化客户 Id 的硬范围边界：无权威归属（缺失 / &lt;= 0）与越界一律 fail closed。
    /// <para>ERP-496：范围参数<b>不再接受 <c>null</c></b> —— 调用方必须先经 <see cref="EnsureMenuAuthorizedAsync"/>
    /// 解析出非空范围；特权账号由 <c>AllowedCustomerIds is null</c> 表示「不限制」，绝不与「未解析身份」混同。</para>
    /// </summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long? customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return;

        if (customerId is null or <= 0)
            throw new BusinessException(UnlinkedExpenseText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 按 Id 精确解析权威客户：必须真实存在、未删除且启用，否则一律拒绝（绝不按自由文本猜测客户）。
    /// </summary>
    public static async Task<BaseCustomer> EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter(CustomerUnavailableText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        if (customer is null || customer.Status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.InvalidParameter($"{CustomerUnavailableText}（客户 Id={customerId}）");
        return customer;
    }

    // ==================== 3. 费用单写入 / 读取范围校验 ====================

    /// <summary>已存储费用单的范围校验（读取 / 详情 / 修改 / 删除之前调用；特权账号保留历史访问）</summary>
    public static void EnsureStoredExpenseScopeAllowed(SalespersonDataScope scope, FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(expense);
        EnsureCustomerInScope(scope, expense.CustomerId);
    }

    /// <summary>
    /// 拟议费用单（新增 / 修改 / 传统分摊生成）的完整校验：客户范围 → 真实启用客户 → 受支持币种 →
    /// 为正且逐位原样可在声明列（<c>DECIMAL(18,4)</c>）内存储的金额 → 为正且逐位原样可在声明列
    /// （<c>DECIMAL(18,6)</c>）内存储的汇率 → 非负且逐位原样可存储的可选标量；
    /// 任一不满足即在改写任何字段或生成任何行之前拒绝。
    /// <para>受限账号不能提交无权威客户归属（CustomerId 为空）的费用单；特权账号的柜级 / 客户级既有口径保持不变。</para>
    /// </summary>
    public static async Task EnsureProposedExpenseValidAsync(
        IErpDbContext db, SalespersonDataScope scope, FinanceExpense proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposed);

        EnsureCustomerInScope(scope, proposed.CustomerId);
        if (proposed.CustomerId is > 0)
            await EnsureCustomerAvailableAsync(db, proposed.CustomerId.Value, ct);

        NormalizeCurrencyStrict(proposed.Currency);
        NormalizeAmount(proposed.Amount);
        NormalizeRate(proposed.ExchangeRate);

        // ERP-538：客户端可持久化的其余标量（折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额）同样必须能在
        // 声明列精度 DECIMAL(18,4) 内**逐位原样**存储且非负；零值（未启用税 / 未分摊）照常放行，
        // 负数 / 第 5 位小数 / 超出量级在任何已跟踪写入之前受控拒绝（既不发明税务等式，也不静默截断 / 取整）。
        EnsureNonNegativeScalar(proposed.AmountCny, AmountCnyOverflowLabel);
        EnsureNonNegativeScalar(proposed.TaxRate, TaxRateOverflowLabel);
        EnsureNonNegativeScalar(proposed.TaxAmount, TaxAmountOverflowLabel);
        EnsureNonNegativeScalar(proposed.AllocationRatio, AllocationRatioOverflowLabel);
        EnsureNonNegativeScalar(proposed.AllocatedAmount, AllocatedAmountOverflowLabel);
    }

    /// <summary>
    /// 多个拟议客户的范围与可用性校验（传统「按客户分摊并生成费用单」与批次生成前的完整范围复核）：
    /// 每一个客户都必须在当前账号范围内且真实启用；任一越界 / 不可用即整体拒绝（不生成任何行）。
    /// </summary>
    public static async Task EnsureCustomersValidAsync(
        IErpDbContext db, SalespersonDataScope scope, IEnumerable<long> customerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(customerIds);

        foreach (var id in customerIds.Where(id => id > 0).Distinct())
        {
            EnsureCustomerInScope(scope, id);
            await EnsureCustomerAvailableAsync(db, id, ct);
        }
    }

    // ==================== 4. SQL 侧范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 费用单范围谓词：特权账号（<c>AllowedCustomerIds is null</c>）不过滤；受限账号只保留持久化
    /// <see cref="FinanceExpense.CustomerId"/> 落在范围内且非空的费用行（无权威归属的无主行一律不返回）。
    /// <para>ERP-496：范围参数<b>不再接受 <c>null</c></b>（原「<c>null</c> = 进程内调用，不额外限制」旁路已移除）——
    /// 调用方必须先经 <see cref="EnsureMenuAuthorizedAsync"/> 解析出非空范围。</para>
    /// </summary>
    public static Expression<Func<FinanceExpense, bool>>? ExpenseScopeFilter(SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return null;

        var allowed = scope.AllowedCustomerIds.ToList();
        return expense => expense.CustomerId.HasValue && allowed.Contains(expense.CustomerId.Value);
    }

    /// <summary>费用单范围下推（<see cref="ExpenseScopeFilter"/> 的 IQueryable 便捷重载，范围必须为非空已知身份）</summary>
    public static IQueryable<FinanceExpense> ApplyExpenseScope(
        IQueryable<FinanceExpense> source, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        var filter = ExpenseScopeFilter(scope);
        return filter is null ? source : source.Where(filter);
    }

    /// <summary>
    /// 分摊批次范围谓词（工作台 / 批次台账共用，在计数与分页之前下推）：受限账号只保留满足下列全部条件的批次 ——
    /// ① 显式 <see cref="FinanceExpenseAllocationBatch.LoadingListId"/> 的装柜清单通过 ERP-364 权威范围
    /// （无范围外有效参与方 / 上游客户，且有有效参与方或兼容客户字段在范围内）；② 没有任何范围外的逐行生成客户；
    /// ③ <see cref="FinanceExpenseAllocationBatch.SourceExpenseId"/> 来源费用没有范围外的归属客户。
    /// 无法证明归属的行一律不返回，绝不「先查全量再内存过滤」。
    /// <para>ERP-496：范围参数<b>不再接受 <c>null</c></b>（原旁路已移除）。</para>
    /// </summary>
    public static Expression<Func<FinanceExpenseAllocationBatch, bool>>? BatchScopeFilter(
        IErpDbContext db, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return null;

        var allowed = scope.AllowedCustomerIds.ToList();
        var scopedListIds = LoadingListAuthorizationRules
            .ApplyScope(db.ContainerLoadingLists.AsNoTracking(), db, scope)
            .Select(l => l.Id);

        return batch =>
            scopedListIds.Contains(batch.LoadingListId)
            && !db.FinanceExpenseAllocationLines.Any(line =>
                !line.IsDeleted && line.BatchId == batch.Id && !allowed.Contains(line.CustomerId))
            && !db.FinanceExpenses.Any(expense =>
                expense.Id == batch.SourceExpenseId
                && expense.CustomerId.HasValue
                && !allowed.Contains(expense.CustomerId.Value));
    }

    /// <summary>分摊批次范围下推（<see cref="BatchScopeFilter"/> 的 IQueryable 便捷重载）</summary>
    /// <summary>
    /// 分摊批次范围下推（<see cref="BatchScopeFilter"/> 的 IQueryable 便捷重载）。
    /// <para>ERP-496：费用单路由（分摊上下文 / 预览 / 生成 / 台账 / 详情 / 作废）一律传入
    /// <see cref="EnsureMenuAuthorizedAsync"/> 解析出的<b>非空</b>权威范围，绝不依赖任何「未解析身份即不受限」的分支。</para>
    /// <para><b>既有已授权调用方边界</b>：分摊证据模块（ERP-060，本任务未改动其路由）在自己的控制器动作内已先经
    /// <see cref="EnsureMenuAuthorizedAsync"/> 完成实时授权；其只读派生服务在装柜清单 / 结算单维度入口以
    /// <c>dataScope = null</c> 表示「调用方已完成授权且不再额外下推」——该分支只服务该既有调用方，
    /// <b>不可由费用单路由到达</b>，也绝不代表匿名、管理员或测试专用放行。</para>
    /// </summary>
    public static IQueryable<FinanceExpenseAllocationBatch> ApplyBatchScope(
        IQueryable<FinanceExpenseAllocationBatch> source, IErpDbContext db, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (scope is null) return source;

        var filter = BatchScopeFilter(db, scope);
        return filter is null ? source : source.Where(filter);
    }

    // ==================== 5. 装柜清单 / 批次 / 结算单范围校验 ====================

    /// <summary>
    /// 装柜清单范围校验（分摊上下文 / 预览 / 生成 / 证据读取之前）：显式 Id 解析既有未删除清单，
    /// 受限账号按 ERP-364 口径复核每一个有效参与方客户与显式上游客户。
    /// <para>ERP-496：范围参数<b>不再接受 <c>null</c></b>。</para>
    /// </summary>
    public static async Task<ContainerLoadingList> EnsureLoadingListScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var loadingList = await ContainerLoadingParticipantService.EnsureLoadingListAsync(db, loadingListId);
        if (scope.AllowedCustomerIds is not null)
            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
        return loadingList;
    }

    /// <summary>
    /// 解析并复核装柜清单范围（只读派生场景，如分摊证据）：清单不存在 / 已删除时对受限账号 fail closed，
    /// 特权账号返回 <c>null</c> 以保留历史证据可读（照实标注不可用），绝不按柜号等自由文本推断。
    /// </summary>
    public static async Task<ContainerLoadingList?> ResolveLoadingListScopeAsync(
        IErpDbContext db, SalespersonDataScope? scope, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId <= 0) return null;

        var restricted = scope is not null && scope.AllowedCustomerIds is not null;
        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == loadingListId && !l.IsDeleted, ct);
        if (loadingList is null)
        {
            if (restricted)
                throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);
            return null;
        }

        if (restricted)
            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope!, loadingList, ct);
        return loadingList;
    }

    /// <summary>
    /// 装柜清单分摊证据的客户范围校验：受限账号读取某清单的分摊证据之前，必须对该清单上
    /// <b>每一个被分摊行显式引用的客户</b>都有数据范围权限（多客户共享柜 fail closed，不泄露其他客户）。
    /// </summary>
    public static async Task EnsureEvidenceCustomersInScopeAsync(
        IErpDbContext db, SalespersonDataScope? scope, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is null || scope.AllowedCustomerIds is null) return;
        if (loadingListId <= 0) return;

        var batchIds = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(b => b.LoadingListId == loadingListId)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (batchIds.Count == 0) return;

        var customerIds = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => batchIds.Contains(l.BatchId))
            .Select(l => l.CustomerId)
            .Distinct()
            .ToListAsync(ct);

        if (customerIds.Any(id => !scope.AllowsCustomer(id)))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 已存储分摊批次的完整范围校验（台账详情 / 作废之前）：受限账号要求显式装柜清单（参与方 + 上游客户）可证明、
    /// 来源费用（若已登记归属客户）在范围内、且逐行生成客户全部在范围内；任一不满足即拒绝，且不修改任何行 / 状态 / 审计。
    /// <para>ERP-496：范围参数<b>不再接受 <c>null</c></b>（原「未解析身份即不受限」旁路已移除）。</para>
    /// </summary>
    public static async Task EnsureBatchScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, FinanceExpenseAllocationBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return;

        // ① 显式 LoadingListId 的权威归属：清单不存在 / 已删除即 fail closed（不通过共享柜泄露）。
        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == batch.LoadingListId && !l.IsDeleted, ct);
        if (loadingList is null)
            throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);

        // ② SourceExpenseId 来源费用的归属客户（若已登记）必须落在范围内；无归属登记时以装柜清单为准。
        if (batch.SourceExpenseId > 0)
        {
            var sourceCustomerId = await db.FinanceExpenses.AsNoTracking()
                .Where(e => e.Id == batch.SourceExpenseId)
                .Select(e => e.CustomerId)
                .FirstOrDefaultAsync(ct);
            if (sourceCustomerId is > 0 && !scope.AllowsCustomer(sourceCustomerId.Value))
                throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
        }

        // ③ 逐行生成客户（分摊行是唯一权威登记册）全部必须在范围内。
        var lineCustomers = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => !l.IsDeleted && l.BatchId == batch.Id)
            .Select(l => l.CustomerId)
            .Distinct()
            .ToListAsync(ct);
        if (lineCustomers.Any(id => !scope.AllowsCustomer(id)))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 已存储装柜结算单的范围校验（分摊证据读取之前）：结算客户必须在范围内；显式关联的装柜清单仍可解析时，
    /// 其参与方 / 上游客户范围与链路上<b>每一个被分摊行显式引用的客户</b>也必须同时满足（共享柜 fail closed）。
    /// <para><b>既有已授权调用方边界（ERP-496）</b>：本方法只服务分摊证据模块（ERP-060）的结算单维度只读派生；
    /// 该模块的控制器动作已先经 <see cref="EnsureMenuAuthorizedAsync"/> 完成实时授权，并以非空范围调用本方法；
    /// 既有装柜清单 / 装柜结算单路由（<c>ContainerLoadingListController</c> / <c>FinanceSettlementControllers</c>）
    /// 在自己的实时授权之后调用只读派生服务时不额外传入范围（<c>dataScope = null</c>），仅表示「调用方已授权且
    /// 不再重复下推」——该分支<b>不可由费用单路由（<c>api/finance/expenses</c>）到达</b>，也绝不代表匿名、管理员
    /// 或测试专用放行。</para>
    /// </summary>
    public static async Task EnsureSettlementScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, FinanceContainerSettlement settlement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settlement);
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (settlement.CustomerId <= 0)
            throw new BusinessException(UnlinkedExpenseText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(settlement.CustomerId))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);

        if (settlement.LoadingListId is not > 0) return;

        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == settlement.LoadingListId.Value && !l.IsDeleted, ct);
        if (loadingList is null)
            throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);

        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
        await EnsureEvidenceCustomersInScopeAsync(db, scope, settlement.LoadingListId.Value, ct);
    }
}
