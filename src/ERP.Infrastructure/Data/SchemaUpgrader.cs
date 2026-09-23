using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 数据库结构升级器：启动时执行幂等的结构补齐（新增表 / 新增列 / 索引）
/// 说明：EF Core 的 EnsureCreated 只负责「空库建表」，不会为已存在的库补齐新增结构，
///       因此新增表与新增列统一在此以「IF NOT EXISTS」方式补齐，保证升级无需人工介入。
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>执行结构升级（可重复执行）</summary>
    public static async Task EnsureUpgradedAsync(ErpDbContext db)
    {
        // 1. 打印设计模板表（第四阶段新增；表名与 DbSet 属性名一致，EF 默认表名取 DbSet 名）
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SysPrintTemplates') IS NULL
BEGIN
    CREATE TABLE db_owner.SysPrintTemplates (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BillType NVARCHAR(50) NOT NULL,
        TemplateName NVARCHAR(100) NOT NULL,
        Title NVARCHAR(200) NOT NULL DEFAULT '',
        CompanyName NVARCHAR(200) NOT NULL DEFAULT '',
        CompanyAddress NVARCHAR(300) NOT NULL DEFAULT '',
        CompanyPhone NVARCHAR(100) NOT NULL DEFAULT '',
        ShowCompanyHeader BIT NOT NULL DEFAULT 1,
        ShowDetailTable BIT NOT NULL DEFAULT 1,
        ShowRemark BIT NOT NULL DEFAULT 1,
        PaperSize NVARCHAR(20) NOT NULL DEFAULT 'A4',
        FontSize INT NOT NULL DEFAULT 12,
        FieldKeys NVARCHAR(2000) NOT NULL DEFAULT '',
        FooterText NVARCHAR(500) NOT NULL DEFAULT '',
        IsDefault BIT NOT NULL DEFAULT 0,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 2. 历史版本遗留的旧表名（单数）自动迁移，避免出现同名空表
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SysPrintTemplate') IS NOT NULL AND OBJECT_ID('db_owner.SysPrintTemplates') IS NULL
    EXEC sp_rename 'db_owner.SysPrintTemplate', 'SysPrintTemplates';");

        // 3. 打印模板唯一索引（软删除的记录不占用唯一键，与 SysMenu 索引策略一致）
        await db.Database.ExecuteSqlRawAsync(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SysPrintTemplates_BillType_Name' AND has_filter = 0)
    DROP INDEX UX_SysPrintTemplates_BillType_Name ON db_owner.SysPrintTemplates;");
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SysPrintTemplates_BillType_Name')
    CREATE UNIQUE INDEX UX_SysPrintTemplates_BillType_Name
        ON db_owner.SysPrintTemplates(BillType, TemplateName) WHERE IsDeleted = 0;");

        // 4. 操作日志增加单据号列（第四阶段新增，用于按单据追溯）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.SysOperationLogs', 'BillNo') IS NULL
    ALTER TABLE db_owner.SysOperationLogs ADD BillNo NVARCHAR(100) NOT NULL DEFAULT '';");

        // 5. 「样式设计」独立菜单（打印模板集中管理入口：挂在「系统设置」分组下）
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'print-design' AND IsDeleted = 0)
BEGIN
    DECLARE @parentId BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'system' AND IsDeleted = 0);
    IF @parentId IS NULL SET @parentId = 0;
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (@parentId, N'样式设计', N'print-design', N'/print-design', N'palette', 90, 2, N'sys:print-design', GETDATE(), 0);
END");

        // 6. 菜单授权：已具备「系统设置」访问权的角色自动获得「样式设计」（幂等）
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'print-design' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND (rm.MenuId = m.ParentId
       OR rm.MenuId IN (SELECT Id FROM db_owner.SysMenus WHERE ParentId = m.ParentId AND IsDeleted = 0))
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 7. 打印模板外观样式列（字体 / 字号 / 颜色 / 单元格尺寸，供可视化设计器调节）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.SysPrintTemplates', 'FontFamily') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD FontFamily NVARCHAR(50) NOT NULL DEFAULT N'Microsoft YaHei';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'TitleFontSize') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD TitleFontSize INT NOT NULL DEFAULT 16;
IF COL_LENGTH('db_owner.SysPrintTemplates', 'TitleColor') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD TitleColor NVARCHAR(20) NOT NULL DEFAULT N'#000000';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'TitleAlign') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD TitleAlign NVARCHAR(10) NOT NULL DEFAULT N'center';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'CompanyFontSize') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD CompanyFontSize INT NOT NULL DEFAULT 18;
IF COL_LENGTH('db_owner.SysPrintTemplates', 'CompanyColor') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD CompanyColor NVARCHAR(20) NOT NULL DEFAULT N'#000000';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'TextColor') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD TextColor NVARCHAR(20) NOT NULL DEFAULT N'#000000';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'HeaderBgColor') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD HeaderBgColor NVARCHAR(20) NOT NULL DEFAULT N'#f2f2f2';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'BorderColor') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD BorderColor NVARCHAR(20) NOT NULL DEFAULT N'#999999';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'BorderStyle') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD BorderStyle NVARCHAR(20) NOT NULL DEFAULT N'solid';
IF COL_LENGTH('db_owner.SysPrintTemplates', 'RowHeight') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD RowHeight INT NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.SysPrintTemplates', 'CellPadding') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD CellPadding INT NOT NULL DEFAULT 6;
IF COL_LENGTH('db_owner.SysPrintTemplates', 'LayoutJson') IS NULL
    ALTER TABLE db_owner.SysPrintTemplates ADD LayoutJson NVARCHAR(MAX) NULL;");

        // 8. 钉钉通知发送记录表
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SysDingTalkLogs') IS NULL
BEGIN
    CREATE TABLE db_owner.SysDingTalkLogs (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BillType NVARCHAR(50) NOT NULL DEFAULT '',
        BillTypeName NVARCHAR(50) NOT NULL DEFAULT '',
        BillNo NVARCHAR(100) NOT NULL DEFAULT '',
        ActionCode NVARCHAR(30) NOT NULL DEFAULT '',
        ActionName NVARCHAR(30) NOT NULL DEFAULT '',
        Operator NVARCHAR(50) NOT NULL DEFAULT '',
        Title NVARCHAR(200) NOT NULL DEFAULT '',
        Content NVARCHAR(2000) NOT NULL DEFAULT '',
        MsgType NVARCHAR(20) NOT NULL DEFAULT 'text',
        Webhook NVARCHAR(300) NOT NULL DEFAULT '',
        Success BIT NOT NULL DEFAULT 0,
        ErrorMessage NVARCHAR(500) NOT NULL DEFAULT '',
        RetryCount INT NOT NULL DEFAULT 0,
        SentAt DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 9. 钉钉通知菜单（配置 + 发送记录，挂在「系统设置」下）
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'dingtalk-config' AND IsDeleted = 0)
BEGIN
    DECLARE @p1 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'system' AND IsDeleted = 0);
    IF @p1 IS NULL SET @p1 = 0;
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (@p1, N'钉钉通知配置', N'dingtalk-config', N'/dingtalk-config', N'bell-ring', 91, 2, N'sys:dingtalk-config', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'dingtalk-log' AND IsDeleted = 0)
BEGIN
    DECLARE @p2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'system' AND IsDeleted = 0);
    IF @p2 IS NULL SET @p2 = 0;
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (@p2, N'钉钉发送记录', N'dingtalk-log', N'/dingtalk-log', N'send', 92, 2, N'sys:dingtalk-log', GETDATE(), 0);
END");

        // 10. 授权：已具备「系统设置」访问权的角色自动获得上述菜单（幂等）
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN (N'dingtalk-config', N'dingtalk-log') AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND (rm.MenuId = m.ParentId
       OR rm.MenuId IN (SELECT Id FROM db_owner.SysMenus WHERE ParentId = m.ParentId AND IsDeleted = 0))
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 11. 菜单导航重构（阶段 0 · 2026-09-18）：按义乌外贸业务流程重排一级/二级菜单
        //     对应脚本 deploy/init6.sql；只改名称/归属/排序，不修改任何 MenuCode（权限码与打印模板不受影响）
        // 11.1 新增一级分组：工作台 / 客户与市场 / 供应商与采购 / 商品中心
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'workbench' AND IsDeleted = 0)
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (0, N'工作台', N'workbench', N'/workbench', N'layers', 10, 1, N'workbench', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'crm' AND IsDeleted = 0)
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (0, N'客户与市场', N'crm', N'/crm', N'users', 20, 1, N'crm', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'purchase' AND IsDeleted = 0)
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (0, N'供应商与采购', N'purchase', N'/purchase', N'factory', 30, 1, N'purchase', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'goods' AND IsDeleted = 0)
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (0, N'商品中心', N'goods', N'/goods', N'boxes', 60, 1, N'goods', GETDATE(), 0);");

        // 11.2 一级菜单改名（物流管理→库存管理、装柜管理→出运管理、账务管理→财务结算、询价管理→询报价、订单管理→订单中心）
        await db.Database.ExecuteSqlRawAsync(@"
UPDATE db_owner.SysMenus SET MenuName = N'库存管理', UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'logistics' AND MenuName <> N'库存管理';
UPDATE db_owner.SysMenus SET MenuName = N'出运管理', UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'container' AND MenuName <> N'出运管理';
UPDATE db_owner.SysMenus SET MenuName = N'财务结算', UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'finance'   AND MenuName <> N'财务结算';
UPDATE db_owner.SysMenus SET MenuName = N'询报价',   UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'inquiry'   AND MenuName <> N'询报价';
UPDATE db_owner.SysMenus SET MenuName = N'订单中心', UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'order'     AND MenuName <> N'订单中心';");

        // 11.3 一级排序 + 二级归位 + 新增「我的工作台」+ 游离菜单修正
        await db.Database.ExecuteSqlRawAsync(@"
DECLARE @mWorkbench BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'workbench' AND IsDeleted = 0);
DECLARE @mCrm       BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'crm'       AND IsDeleted = 0);
DECLARE @mPurchase  BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'purchase'  AND IsDeleted = 0);
DECLARE @mGoods     BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'goods'     AND IsDeleted = 0);
DECLARE @mSystem    BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'system'    AND IsDeleted = 0);

UPDATE db_owner.SysMenus SET SortOrder = 40,  UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'inquiry'   AND SortOrder <> 40;
UPDATE db_owner.SysMenus SET SortOrder = 50,  UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'order'     AND SortOrder <> 50;
UPDATE db_owner.SysMenus SET SortOrder = 70,  UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'logistics' AND SortOrder <> 70;
UPDATE db_owner.SysMenus SET SortOrder = 80,  UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'container' AND SortOrder <> 80;
UPDATE db_owner.SysMenus SET SortOrder = 90,  UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'finance'   AND SortOrder <> 90;
UPDATE db_owner.SysMenus SET SortOrder = 100, UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'report'    AND SortOrder <> 100;
UPDATE db_owner.SysMenus SET SortOrder = 110, UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'base'      AND SortOrder <> 110;
UPDATE db_owner.SysMenus SET SortOrder = 120, UpdatedAt = GETDATE() WHERE ParentId = 0 AND IsDeleted = 0 AND MenuCode = N'system'    AND SortOrder <> 120;

IF @mCrm IS NOT NULL
    UPDATE db_owner.SysMenus SET ParentId = @mCrm, SortOrder = 10, UpdatedAt = GETDATE()
    WHERE MenuCode = N'customer' AND IsDeleted = 0 AND (ParentId <> @mCrm OR SortOrder <> 10);

IF @mPurchase IS NOT NULL
BEGIN
    UPDATE db_owner.SysMenus SET ParentId = @mPurchase, SortOrder = 10, UpdatedAt = GETDATE() WHERE MenuCode = N'supplier'              AND IsDeleted = 0 AND (ParentId <> @mPurchase OR SortOrder <> 10);
    UPDATE db_owner.SysMenus SET ParentId = @mPurchase, SortOrder = 20, UpdatedAt = GETDATE() WHERE MenuCode = N'purchase-order'        AND IsDeleted = 0 AND (ParentId <> @mPurchase OR SortOrder <> 20);
    UPDATE db_owner.SysMenus SET ParentId = @mPurchase, SortOrder = 30, UpdatedAt = GETDATE() WHERE MenuCode = N'purchase-order-export' AND IsDeleted = 0 AND (ParentId <> @mPurchase OR SortOrder <> 30);
    UPDATE db_owner.SysMenus SET ParentId = @mPurchase, SortOrder = 40, UpdatedAt = GETDATE() WHERE MenuCode = N'stock-in'              AND IsDeleted = 0 AND (ParentId <> @mPurchase OR SortOrder <> 40);
END

IF @mGoods IS NOT NULL
    UPDATE db_owner.SysMenus SET ParentId = @mGoods, SortOrder = 10, UpdatedAt = GETDATE()
    WHERE MenuCode = N'product' AND IsDeleted = 0 AND (ParentId <> @mGoods OR SortOrder <> 10);

IF @mWorkbench IS NOT NULL AND NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'home' AND IsDeleted = 0)
    INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
    VALUES (@mWorkbench, N'我的工作台', N'home', N'/home', N'landmark', 10, 2, N'home', GETDATE(), 0);

IF @mSystem IS NOT NULL
    UPDATE db_owner.SysMenus SET ParentId = @mSystem, UpdatedAt = GETDATE()
    WHERE IsDeleted = 0 AND ParentId = 0 AND MenuType > 1 AND MenuCode IN (N'print-design', N'dingtalk-config', N'dingtalk-log');");
        // 11.4 组内排序：按业务顺序统一设置所有二级菜单
        await db.Database.ExecuteSqlRawAsync(@"
UPDATE m SET m.SortOrder = v.NewSort, m.UpdatedAt = GETDATE()
FROM db_owner.SysMenus m
JOIN (VALUES
    (N'home', 10), (N'customer', 10),
    (N'supplier', 10), (N'purchase-order', 20), (N'purchase-order-export', 30), (N'stock-in', 40),
    (N'inquiry-new', 10), (N'inquiry-export', 20),
    (N'sales-order', 10), (N'sales-order-export', 20),
    (N'product', 10),
    (N'stock-out', 10), (N'stock-query', 20),
    (N'receiving-plan', 10), (N'booking', 20), (N'pre-loading', 30), (N'loading-list', 40),
    (N'deposit-apply', 10), (N'payment-apply', 20), (N'payment', 30), (N'receipt', 40),
    (N'container-settlement', 50), (N'bulk-settlement', 60), (N'complaint', 70),
    (N'product-sales-ranking', 10), (N'order-profit', 20), (N'customer-shipment', 30), (N'salesman-output', 40),
    (N'balance-sheet', 50), (N'income-statement', 60), (N'cash-flow', 70),
    (N'employee', 10), (N'expense-account', 20), (N'warehouse', 30), (N'other-info', 40),
    (N'user', 10), (N'role', 20), (N'user-permission', 30), (N'sys-parameter', 40),
    (N'user-parameter', 50), (N'doc-rule', 60), (N'client-limit', 70), (N'sys-log', 80),
    (N'print-design', 90), (N'dingtalk-config', 91), (N'dingtalk-log', 92)
) v(MenuCode, NewSort) ON v.MenuCode = m.MenuCode
WHERE m.IsDeleted = 0 AND m.SortOrder <> v.NewSort;");

        // 11.5 幂等授权：已拥有新分组下任一子菜单权限的角色自动获得该分组（否则菜单会以「游离」形式出现）
        await db.Database.ExecuteSqlRawAsync(@"
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, g.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus g ON g.MenuCode IN (N'workbench', N'crm', N'purchase', N'goods') AND g.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId IN (SELECT Id FROM db_owner.SysMenus WHERE ParentId = g.Id AND IsDeleted = 0)
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = g.Id AND x.IsDeleted = 0);");

        // 11.6 业务模式系统参数（预留：Trade=工贸一体 / Agency=纯代理采购 / Hybrid=两种都有）
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysParameters WHERE ParamKey = N'BizMode' AND IsDeleted = 0)
    INSERT INTO db_owner.SysParameters (ParamKey, ParamValue, ParamName, Description, IsSystem, CreatedAt, IsDeleted)
    VALUES (N'BizMode', N'Hybrid', N'业务模式', N'Trade=工贸一体 / Agency=纯代理采购 / Hybrid=两种都有（默认）', 0, GETDATE(), 0);");

        // 12. 阶段 1：主数据字段补齐 + 出口退税台账（幂等；新列可空或带默认值，不影响历史数据）
        // 12.1 客户资料（外贸单据与单证所需信息）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.BaseCustomers', 'BusinessNature') IS NULL     ALTER TABLE db_owner.BaseCustomers ADD BusinessNature NVARCHAR(20) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'Currency') IS NULL           ALTER TABLE db_owner.BaseCustomers ADD Currency NVARCHAR(20) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'SettlementMethod') IS NULL   ALTER TABLE db_owner.BaseCustomers ADD SettlementMethod NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'TradeTerms') IS NULL         ALTER TABLE db_owner.BaseCustomers ADD TradeTerms NVARCHAR(50) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'DestinationPort') IS NULL    ALTER TABLE db_owner.BaseCustomers ADD DestinationPort NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'Consignee') IS NULL          ALTER TABLE db_owner.BaseCustomers ADD Consignee NVARCHAR(300) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'NotifyParty') IS NULL        ALTER TABLE db_owner.BaseCustomers ADD NotifyParty NVARCHAR(300) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'DefaultShippingMark') IS NULL ALTER TABLE db_owner.BaseCustomers ADD DefaultShippingMark NVARCHAR(300) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'CreditDays') IS NULL         ALTER TABLE db_owner.BaseCustomers ADD CreditDays INT NULL;
IF COL_LENGTH('db_owner.BaseCustomers', 'CommissionRatio') IS NULL    ALTER TABLE db_owner.BaseCustomers ADD CommissionRatio DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseCustomers', 'CustomerLevel') IS NULL      ALTER TABLE db_owner.BaseCustomers ADD CustomerLevel NVARCHAR(20) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseCustomers', 'CreditStatus') IS NULL       ALTER TABLE db_owner.BaseCustomers ADD CreditStatus NVARCHAR(20) NOT NULL DEFAULT N'正常';
IF COL_LENGTH('db_owner.BaseCustomers', 'Source') IS NULL             ALTER TABLE db_owner.BaseCustomers ADD Source NVARCHAR(50) NOT NULL DEFAULT N'';");

        // 12.2 供应商资料（档口采购与结算；发票字段预留，当前不强制）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.BaseSuppliers', 'SupplierType') IS NULL     ALTER TABLE db_owner.BaseSuppliers ADD SupplierType NVARCHAR(20) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseSuppliers', 'BoothLocation') IS NULL    ALTER TABLE db_owner.BaseSuppliers ADD BoothLocation NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseSuppliers', 'MainCategory') IS NULL     ALTER TABLE db_owner.BaseSuppliers ADD MainCategory NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseSuppliers', 'SettlementMethod') IS NULL ALTER TABLE db_owner.BaseSuppliers ADD SettlementMethod NVARCHAR(50) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseSuppliers', 'InvoiceAbility') IS NULL   ALTER TABLE db_owner.BaseSuppliers ADD InvoiceAbility NVARCHAR(20) NOT NULL DEFAULT N'不票';
IF COL_LENGTH('db_owner.BaseSuppliers', 'TaxRate') IS NULL          ALTER TABLE db_owner.BaseSuppliers ADD TaxRate DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseSuppliers', 'DeliveryDays') IS NULL     ALTER TABLE db_owner.BaseSuppliers ADD DeliveryDays INT NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseSuppliers', 'RebateRatio') IS NULL      ALTER TABLE db_owner.BaseSuppliers ADD RebateRatio DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseSuppliers', 'WeChat') IS NULL           ALTER TABLE db_owner.BaseSuppliers ADD WeChat NVARCHAR(50) NOT NULL DEFAULT N'';");
        // 12.3 商品资料（多单位/箱规、报关品名、退税率、认证）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.BaseProducts', 'PackageUnit') IS NULL        ALTER TABLE db_owner.BaseProducts ADD PackageUnit NVARCHAR(20) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'UnitsPerPackage') IS NULL    ALTER TABLE db_owner.BaseProducts ADD UnitsPerPackage INT NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'UnitConversion') IS NULL     ALTER TABLE db_owner.BaseProducts ADD UnitConversion NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'OuterLength') IS NULL        ALTER TABLE db_owner.BaseProducts ADD OuterLength DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'OuterWidth') IS NULL         ALTER TABLE db_owner.BaseProducts ADD OuterWidth DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'OuterHeight') IS NULL        ALTER TABLE db_owner.BaseProducts ADD OuterHeight DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'OuterWeight') IS NULL        ALTER TABLE db_owner.BaseProducts ADD OuterWeight DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'VolumeWeight') IS NULL       ALTER TABLE db_owner.BaseProducts ADD VolumeWeight DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'EnglishDeclareName') IS NULL ALTER TABLE db_owner.BaseProducts ADD EnglishDeclareName NVARCHAR(200) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'RefundRate') IS NULL         ALTER TABLE db_owner.BaseProducts ADD RefundRate DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'Brand') IS NULL              ALTER TABLE db_owner.BaseProducts ADD Brand NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'Certification') IS NULL      ALTER TABLE db_owner.BaseProducts ADD Certification NVARCHAR(200) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'CustomerItemNo') IS NULL     ALTER TABLE db_owner.BaseProducts ADD CustomerItemNo NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'FactoryItemNo') IS NULL      ALTER TABLE db_owner.BaseProducts ADD FactoryItemNo NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.BaseProducts', 'MinOrderQty') IS NULL        ALTER TABLE db_owner.BaseProducts ADD MinOrderQty INT NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'TaxIncluded') IS NULL        ALTER TABLE db_owner.BaseProducts ADD TaxIncluded BIT NOT NULL DEFAULT 0;");

        // 12.4 出口退税台账表
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.BaseTaxRefunds') IS NULL
BEGIN
    CREATE TABLE db_owner.BaseTaxRefunds (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RefundNo NVARCHAR(50) NOT NULL,
        RefundPeriod NVARCHAR(20) NOT NULL DEFAULT N'',
        DeclareDate DATETIME2 NULL,
        DeclareNo NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SalesOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        ExportAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'USD',
        ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
        RefundRate DECIMAL(18,4) NOT NULL DEFAULT 0,
        RefundableAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        RefundedAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        RefundDate DATETIME2 NULL,
        Status NVARCHAR(20) NOT NULL DEFAULT N'待申报',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 12.5 「出口退税台账」菜单（挂在「财务结算」分组下）+ 幂等授权
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'tax-refund' AND IsDeleted = 0)
BEGIN
    DECLARE @pFinance BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'finance' AND IsDeleted = 0);
    IF @pFinance IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pFinance, N'出口退税台账', N'tax-refund', N'/finance/tax-refund', N'banknote', 55, 2, N'finance:tax-refund', GETDATE(), 0);
END
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'tax-refund' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 13. 阶段 2：费用单（出口杂费台账与分摊）
        //     分摊基数可选「体积 / 重量 / 金额 / 箱数 / 手工 / 不分摊」，不写死单一规则
        // 13.1 建表
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.FinanceExpenses') IS NULL
BEGIN
    CREATE TABLE db_owner.FinanceExpenses (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ExpenseNo NVARCHAR(50) NOT NULL,
        ExpenseDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        ExpenseType NVARCHAR(30) NOT NULL DEFAULT N'',
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
        AmountCny DECIMAL(18,4) NOT NULL DEFAULT 0,
        TaxRate DECIMAL(18,4) NOT NULL DEFAULT 0,
        TaxAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Payee NVARCHAR(200) NOT NULL DEFAULT N'',
        RefType NVARCHAR(30) NOT NULL DEFAULT N'',
        RefNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocationBase NVARCHAR(30) NOT NULL DEFAULT N'不分摊',
        AllocationRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        AllocatedAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        PaymentStatus NVARCHAR(20) NOT NULL DEFAULT N'未付',
        PayDate DATETIME2 NULL,
        PaymentMethod NVARCHAR(30) NOT NULL DEFAULT N'',
        BillNo NVARCHAR(50) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 13.2 「费用单」菜单（挂「财务结算」分组下）+ 幂等授权
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'expense-bill' AND IsDeleted = 0)
BEGIN
    DECLARE @pFin2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'finance' AND IsDeleted = 0);
    IF @pFin2 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pFin2, N'费用单', N'expense-bill', N'/finance/expense-bill', N'file-text', 45, 2, N'finance:expense-bill', GETDATE(), 0);
END
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'expense-bill' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 14. 阶段 2：「应收账龄分析表」菜单（挂「报表管理」分组下）+ 幂等授权
        //     报表数据由 /api/reports/ar-aging 提供：按销售订单逐笔 + 收款先入先出冲抵，按客户账期判断逾期
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'ar-aging' AND IsDeleted = 0)
BEGIN
    DECLARE @pReport BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pReport IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pReport, N'应收账龄分析表', N'ar-aging', N'/report/ar-aging', N'calendar', 80, 2, N'report:ar-aging', GETDATE(), 0);
END
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'ar-aging' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 15. 阶段 2：供应商报价比价（同一需求多家报价，横向比价并标记选中）+ 菜单
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.PurchaseQuotes') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseQuotes (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        QuoteNo NVARCHAR(50) NOT NULL,
        QuoteDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        ProductId BIGINT NULL,
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        SupplierId BIGINT NULL,
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        SupplierType NVARCHAR(20) NOT NULL DEFAULT N'',
        QuotePrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        TaxIncluded BIT NOT NULL DEFAULT 0,
        DeliveryDays INT NOT NULL DEFAULT 0,
        MinOrderQty INT NOT NULL DEFAULT 0,
        PaymentTerms NVARCHAR(100) NOT NULL DEFAULT N'',
        IsSelected BIT NOT NULL DEFAULT 0,
        Status NVARCHAR(20) NOT NULL DEFAULT N'待比较',
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        RefOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'purchase-quote' AND IsDeleted = 0)
BEGIN
    DECLARE @pPurchase BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'purchase' AND IsDeleted = 0);
    IF @pPurchase IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pPurchase, N'供应商比价', N'purchase-quote', N'/purchase/quote', N'scale', 15, 2, N'purchase:purchase-quote', GETDATE(), 0);
END
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'purchase-quote' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 16. 阶段 2：出口单证台账（单证中心）+ 菜单（挂「出运管理」分组下）
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.TradeDocuments') IS NULL
BEGIN
    CREATE TABLE db_owner.TradeDocuments (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        DocNo NVARCHAR(50) NOT NULL,
        DocType NVARCHAR(30) NOT NULL DEFAULT N'',
        IssueDate DATETIME2 NULL,
        DeclareNo NVARCHAR(50) NOT NULL DEFAULT N'',
        RefNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SalesOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'USD',
        DeparturePort NVARCHAR(100) NOT NULL DEFAULT N'',
        DestinationPort NVARCHAR(100) NOT NULL DEFAULT N'',
        IssuedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        Copies INT NOT NULL DEFAULT 0,
        Status NVARCHAR(20) NOT NULL DEFAULT N'待制作',
        FileNote NVARCHAR(300) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'doc-center' AND IsDeleted = 0)
BEGIN
    DECLARE @pShip BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'container' AND IsDeleted = 0);
    IF @pShip IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pShip, N'单证中心', N'doc-center', N'/container/doc-center', N'scroll', 50, 2, N'container:doc-center', GETDATE(), 0);
END
INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode = N'doc-center' AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 17. 阶段 2：商品安全库存字段 + 4 张运营报表菜单（挂「报表管理」下）
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.BaseProducts', 'MinStock') IS NULL ALTER TABLE db_owner.BaseProducts ADD MinStock DECIMAL(18,4) NOT NULL DEFAULT 0;
IF COL_LENGTH('db_owner.BaseProducts', 'MaxStock') IS NULL ALTER TABLE db_owner.BaseProducts ADD MaxStock DECIMAL(18,4) NOT NULL DEFAULT 0;

IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'container-stats' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep1 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep1 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep1, N'柜量与装柜利用率统计', N'container-stats', N'/report/container-stats', N'ship', 85, 2, N'report:container-stats', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'purchase-cost' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep2 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep2, N'采购成本分析表', N'purchase-cost', N'/report/purchase-cost', N'factory', 86, 2, N'report:purchase-cost', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'tax-refund-summary' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep3 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep3 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep3, N'退税汇总表', N'tax-refund-summary', N'/report/tax-refund-summary', N'banknote', 87, 2, N'report:tax-refund-summary', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'stock-alert' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep4 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep4 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep4, N'库存预警表', N'stock-alert', N'/report/stock-alert', N'alert', 88, 2, N'report:stock-alert', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN (N'container-stats', N'purchase-cost', N'tax-refund-summary', N'stock-alert') AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");

        // 18. 阶段 2：客户跟进记录（CRM）+ 业务员提成表 + 提成比例系统参数
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.CustomerFollowUps') IS NULL
BEGIN
    CREATE TABLE db_owner.CustomerFollowUps (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        FollowNo NVARCHAR(50) NOT NULL,
        FollowDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        FollowType NVARCHAR(30) NOT NULL DEFAULT N'',
        ContactPerson NVARCHAR(100) NOT NULL DEFAULT N'',
        SalesmanId BIGINT NULL,
        SalesmanName NVARCHAR(50) NOT NULL DEFAULT N'',
        Subject NVARCHAR(100) NOT NULL DEFAULT N'',
        Content NVARCHAR(1000) NOT NULL DEFAULT N'',
        Result NVARCHAR(30) NOT NULL DEFAULT N'',
        NextFollowDate DATETIME2 NULL,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'customer-follow' AND IsDeleted = 0)
BEGIN
    DECLARE @pCrm BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'crm' AND IsDeleted = 0);
    IF @pCrm IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pCrm, N'客户跟进记录', N'customer-follow', N'/crm/follow-up', N'user-round', 20, 2, N'crm:customer-follow', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'sales-commission' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep5 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep5 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep5, N'业务员提成表', N'sales-commission', N'/report/sales-commission', N'trending-up', 89, 2, N'report:sales-commission', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN (N'customer-follow', N'sales-commission') AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);

IF NOT EXISTS (SELECT 1 FROM db_owner.SysParameters WHERE ParamKey = N'SalesCommissionRate' AND IsDeleted = 0)
    INSERT INTO db_owner.SysParameters (ParamKey, ParamValue, ParamName, Description, IsSystem, CreatedAt, IsDeleted)
    VALUES (N'SalesCommissionRate', N'0', N'业务员提成比例(%)', N'按毛利计提的提成比例，取值 0~100；0 表示暂不计提（提成表仍输出销售额与毛利）', 0, GETDATE(), 0);");

        // 19. 阶段 2：样品管理 + 跟进提醒报表
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.Samples') IS NULL
BEGIN
    CREATE TABLE db_owner.Samples (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SampleNo NVARCHAR(50) NOT NULL,
        SampleDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        ProductId BIGINT NULL,
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        SampleType NVARCHAR(30) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        SampleFee DECIMAL(18,4) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        FeeSettled BIT NOT NULL DEFAULT 0,
        SendDate DATETIME2 NULL,
        Express NVARCHAR(50) NOT NULL DEFAULT N'',
        TrackingNo NVARCHAR(50) NOT NULL DEFAULT N'',
        Result NVARCHAR(30) NOT NULL DEFAULT N'待反馈',
        SalesmanId BIGINT NULL,
        SalesmanName NVARCHAR(50) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'sample' AND IsDeleted = 0)
BEGIN
    DECLARE @pCrm2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'crm' AND IsDeleted = 0);
    IF @pCrm2 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pCrm2, N'样品管理', N'sample', N'/crm/sample', N'package', 30, 2, N'crm:sample', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'follow-up-due' AND IsDeleted = 0)
BEGIN
    DECLARE @pRep6 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'report' AND IsDeleted = 0);
    IF @pRep6 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pRep6, N'跟进提醒', N'follow-up-due', N'/report/follow-up-due', N'calendar', 90, 2, N'report:follow-up-due', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN (N'sample', N'follow-up-due') AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);");
        // 20. 阶段 3：报价单（询价单 → 报价单 → 形式发票 PI → 销售订单）
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.Quotations') IS NULL
BEGIN
    CREATE TABLE db_owner.Quotations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        QuotationNo NVARCHAR(50) NOT NULL,
        QuotationDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        ValidUntil DATETIME2 NULL,
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        ContactPerson NVARCHAR(50) NOT NULL DEFAULT N'',
        ContactPhone NVARCHAR(50) NOT NULL DEFAULT N'',
        ContactEmail NVARCHAR(100) NOT NULL DEFAULT N'',
        InquiryId BIGINT NULL,
        InquiryNo NVARCHAR(50) NOT NULL DEFAULT N'',
        TradeTerms NVARCHAR(50) NOT NULL DEFAULT N'',
        PortOfLoading NVARCHAR(100) NOT NULL DEFAULT N'',
        PortOfDestination NVARCHAR(100) NOT NULL DEFAULT N'',
        PaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        LeadTime NVARCHAR(100) NOT NULL DEFAULT N'',
        Currency INT NOT NULL DEFAULT 2,
        ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmountCny DECIMAL(18,4) NOT NULL DEFAULT 0,
        SalesmanId BIGINT NULL,
        SalesmanName NVARCHAR(50) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF OBJECT_ID('db_owner.QuotationDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.QuotationDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        QuotationId BIGINT NOT NULL DEFAULT 0,
        QuotationNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortNo INT NOT NULL DEFAULT 0,
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Moq NVARCHAR(100) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'quotation' AND IsDeleted = 0)
BEGIN
    DECLARE @pInq BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'inquiry' AND IsDeleted = 0);
    IF @pInq IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pInq, N'报价单', N'quotation', N'/sales/quotation', N'receipt', 10, 2, N'sales:quotation', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN (N'quotation') AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);

IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 17 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (17, N'QT', N'报价单', N'QT', N'yyyyMMdd', 4, N'', 0, 1, N'报价单（询价单 → 报价单 → PI → 销售订单）', GETDATE(), 0);");

    }
}
