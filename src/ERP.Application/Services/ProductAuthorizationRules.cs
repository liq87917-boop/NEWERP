using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 商品资料（<see cref="BaseProduct"/>，<c>api/base/products</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-452）。商品主数据是销售订单行 / 采购订单行 / 报价单行 / 库存单据与库存查询
/// <b>共同解析</b>的权威对象，并连同商品导出与 OSS 图片上传路由一起必须 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 /
/// 批量删除 / 导出 / 单张上传 / 批量上传<b>每一个</b>路由在读取、写入或产出任何产物<b>之前</b>重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有「商品资料」（<c>product</c>）
/// 功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验商品编码与名称非空且在
/// 持久化长度上限内、可选文本字段不超持久化长度、decimal 数值落在持久化可存储范围内、
/// 可选整数形状非负、状态为已知的 <c>1</c>（启用）/ <c>0</c>（停用）；被拒绝的写入不落任何行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入 / 产物生成前的判定」，<b>不</b>静默截断 / 夹取或改写任何商品字段，
/// 也<b>不</b>改变既有商品编码唯一索引语义、分页 / 导出 / 上传响应契约与 <see cref="GenericService{TEntity}"/> 契约；
/// ERP-037 规格变体关系与 ERP-038 货源关系语义均保持不变；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，
/// 不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class ProductAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "product";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "商品资料";

    /// <summary>商品编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxProductCodeLength = 50;

    /// <summary>商品名称持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxProductNameLength = 200;

    /// <summary>英文名称持久化长度上限</summary>
    public const int MaxEnglishNameLength = 200;

    /// <summary>规格型号持久化长度上限</summary>
    public const int MaxSpecLength = 200;

    /// <summary>计量单位持久化长度上限</summary>
    public const int MaxUnitLength = 20;

    /// <summary>商品分类持久化长度上限</summary>
    public const int MaxCategoryLength = 100;

    /// <summary>海关 HS 编码持久化长度上限</summary>
    public const int MaxHsCodeLength = 50;

    /// <summary>条形码持久化长度上限</summary>
    public const int MaxBarcodeLength = 100;

    /// <summary>产品图片 OSS 地址持久化长度上限（Image1 / Image2 / Image3 均为 <c>NVARCHAR(500)</c>）</summary>
    public const int MaxImageUrlLength = 500;

    /// <summary>装箱单位持久化长度上限</summary>
    public const int MaxPackageUnitLength = 20;

    /// <summary>单位换算说明持久化长度上限</summary>
    public const int MaxUnitConversionLength = 100;

    /// <summary>英文报关品名持久化长度上限</summary>
    public const int MaxEnglishDeclareNameLength = 200;

    /// <summary>品牌持久化长度上限</summary>
    public const int MaxBrandLength = 100;

    /// <summary>认证持久化长度上限</summary>
    public const int MaxCertificationLength = 200;

    /// <summary>客户货号 / 款号持久化长度上限</summary>
    public const int MaxCustomerItemNoLength = 100;

    /// <summary>工厂货号持久化长度上限</summary>
    public const int MaxFactoryItemNoLength = 100;

    /// <summary>备注持久化长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>
    /// 商品数值列的持久化可存储上限（<c>DECIMAL(18,4)</c>：14 位整数 + 4 位小数），
    /// 超出即无法落库，必须在写入前按参数错误拒绝。适用于价格 / 尺寸 / 重量 / 体积 / 退税率 / 安全库存等全部
    /// decimal 列（与「阶段 1 补齐」建表脚本的 <c>DECIMAL(18,4)</c> 同源）。
    /// </summary>
    public const decimal MaxDecimalMagnitude = 99999999999999.9999m;

    /// <summary>已知启用状态（<c>BaseProduct.Status = 1</c>）</summary>
    public const int EnabledStatus = 1;

    /// <summary>已知停用状态（<c>BaseProduct.Status = 0</c>）</summary>
    public const int DisabledStatus = 0;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问商品资料";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问商品资料";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问商品资料（fail closed）";

    /// <summary>缺少既有「商品资料」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「商品资料」（product）模块授权：" +
        "拒绝访问商品资料（fail closed，不返回 / 不新增 / 不改写任何商品，也不生成任何导出或图片上传产物）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "商品资料（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除 / 导出 / 单张上传 / 批量上传）" +
        "在读取、写入或产出任何产物之前，都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「商品资料」（product）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入 / 产物生成前的授权与有界字段校验」：不改变商品编码唯一索引语义、" +
        "ERP-037 规格变体关系与 ERP-038 货源关系语义、分页 / 导出 / 上传响应契约与 GenericService 契约，" +
        "也不静默截断 / 夹取或改写任何商品字段；不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，" +
        "也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「商品资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「商品资料」（<c>product</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何商品读取 / 写入 / 导出 / 图片上传<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
    /// <b>不</b>新增任何菜单 / 角色 / 用户授权，也<b>不</b>因身份缺失而降级为管理员。
    /// </summary>
    public static async Task<long> EnsureAuthorizedAsync(
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

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);

        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return user.Id;
    }

    /// <summary>
    /// 商品持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>商品编码：非空且 ≤ <see cref="MaxProductCodeLength"/>；</item>
    /// <item>商品名称：非空且 ≤ <see cref="MaxProductNameLength"/>；</item>
    /// <item>可选文本字段（英文名称 / 规格 / 单位 / 分类 / 海关编码 / 条码 / 图片地址 / 装箱单位 / 换算说明 /
    /// 报关品名 / 品牌 / 认证 / 客户货号 / 工厂货号 / 备注）：不超各自持久化长度上限；</item>
    /// <item>decimal 数值字段（价格 / 尺寸 / 重量 / 体积 / 退税率 / 安全库存 / 库存上限）：落在
    /// <c>DECIMAL(18,4)</c> 可存储范围内（<see cref="MaxDecimalMagnitude"/>）；</item>
    /// <item>整数列（装箱数 <c>UnitsPerPackage</c>、起订量 <c>MinOrderQty</c>）：已知的非负形状（0 = 未指定）；</item>
    /// <item>状态：仅接受 <see cref="EnabledStatus"/>（启用）或 <see cref="DisabledStatus"/>（停用）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>BaseProducts</c> 行（也<b>不</b>改写任何既有行），且不产出任何导出 / 图片上传产物。
    /// </summary>
    public static void Validate(BaseProduct? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("商品数据不能为空");

        EnsureRequiredText(entity.ProductCode, MaxProductCodeLength, "编码");
        EnsureRequiredText(entity.ProductName, MaxProductNameLength, "名称");

        EnsureOptionalText(entity.EnglishName, MaxEnglishNameLength, "英文名称");
        EnsureOptionalText(entity.Spec, MaxSpecLength, "规格型号");
        EnsureOptionalText(entity.Unit, MaxUnitLength, "计量单位");
        EnsureOptionalText(entity.Category, MaxCategoryLength, "商品分类");
        EnsureOptionalText(entity.HsCode, MaxHsCodeLength, "海关 HS 编码");
        EnsureOptionalText(entity.Barcode, MaxBarcodeLength, "条形码");
        EnsureOptionalText(entity.Image1, MaxImageUrlLength, "图片1地址");
        EnsureOptionalText(entity.Image2, MaxImageUrlLength, "图片2地址");
        EnsureOptionalText(entity.Image3, MaxImageUrlLength, "图片3地址");
        EnsureOptionalText(entity.PackageUnit, MaxPackageUnitLength, "装箱单位");
        EnsureOptionalText(entity.UnitConversion, MaxUnitConversionLength, "单位换算说明");
        EnsureOptionalText(entity.EnglishDeclareName, MaxEnglishDeclareNameLength, "英文报关品名");
        EnsureOptionalText(entity.Brand, MaxBrandLength, "品牌");
        EnsureOptionalText(entity.Certification, MaxCertificationLength, "认证");
        EnsureOptionalText(entity.CustomerItemNo, MaxCustomerItemNoLength, "客户货号");
        EnsureOptionalText(entity.FactoryItemNo, MaxFactoryItemNoLength, "工厂货号");
        EnsureOptionalText(entity.Remark, MaxRemarkLength, "备注");

        EnsurePersistedDecimal(entity.PurchasePrice, nameof(BaseProduct.PurchasePrice), "采购价");
        EnsurePersistedDecimal(entity.SalePrice, nameof(BaseProduct.SalePrice), "销售价");
        EnsurePersistedDecimal(entity.CostPrice, nameof(BaseProduct.CostPrice), "成本价");
        EnsurePersistedDecimal(entity.Weight, nameof(BaseProduct.Weight), "单件毛重");
        EnsurePersistedDecimal(entity.Volume, nameof(BaseProduct.Volume), "单件体积");
        EnsurePersistedDecimal(entity.Length, nameof(BaseProduct.Length), "长");
        EnsurePersistedDecimal(entity.Width, nameof(BaseProduct.Width), "宽");
        EnsurePersistedDecimal(entity.Height, nameof(BaseProduct.Height), "高");
        EnsurePersistedDecimal(entity.OuterLength, nameof(BaseProduct.OuterLength), "外箱长");
        EnsurePersistedDecimal(entity.OuterWidth, nameof(BaseProduct.OuterWidth), "外箱宽");
        EnsurePersistedDecimal(entity.OuterHeight, nameof(BaseProduct.OuterHeight), "外箱高");
        EnsurePersistedDecimal(entity.OuterWeight, nameof(BaseProduct.OuterWeight), "外箱毛重");
        EnsurePersistedDecimal(entity.VolumeWeight, nameof(BaseProduct.VolumeWeight), "体积重");
        EnsurePersistedDecimal(entity.RefundRate, nameof(BaseProduct.RefundRate), "出口退税率");
        EnsurePersistedDecimal(entity.MinStock, nameof(BaseProduct.MinStock), "安全库存");
        EnsurePersistedDecimal(entity.MaxStock, nameof(BaseProduct.MaxStock), "库存上限");

        EnsureNonNegative(entity.UnitsPerPackage, nameof(BaseProduct.UnitsPerPackage), "每箱数量");
        EnsureNonNegative(entity.MinOrderQty, nameof(BaseProduct.MinOrderQty), "起订量");

        if (entity.Status != EnabledStatus && entity.Status != DisabledStatus)
            throw BusinessException.InvalidParameter("商品状态只能是 1（启用）或 0（停用）");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"商品{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"商品{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"商品{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>数值列校验：数值必须能落在持久化 <c>DECIMAL(18,4)</c> 范围内（不做静默夹取）。</summary>
    private static void EnsurePersistedDecimal(decimal value, string column, string fieldText)
    {
        if (value > MaxDecimalMagnitude || value < -MaxDecimalMagnitude)
            throw BusinessException.InvalidParameter(
                $"商品{fieldText}（{column}）超出可存储范围（DECIMAL(18,4)），请填写 {MaxDecimalMagnitude} 以内的数值");
    }

    /// <summary>整数列校验：必须为已知的非负形状（0 = 未指定），负数即拒绝（不做静默夹取 / 改写）。</summary>
    private static void EnsureNonNegative(int value, string column, string fieldText)
    {
        if (value < 0)
            throw BusinessException.InvalidParameter($"商品{fieldText}（{column}）不能为负数（0 表示未指定）");
    }
}
