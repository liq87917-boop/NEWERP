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

        // 21. 阶段 3：形式发票 PI（报价单 → PI → 销售订单）
        //     21.1 主子表建表（幂等）；21.2 菜单 + 授权 + 字轨 + 银行信息默认参数
        // 21.1 形式发票 PI 主子表建表（幂等）
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.ProformaInvoices') IS NULL
BEGIN
    CREATE TABLE db_owner.ProformaInvoices (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PiNo NVARCHAR(50) NOT NULL,
        PiDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        QuotationId BIGINT NULL,
        QuotationNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        ContactPerson NVARCHAR(50) NOT NULL DEFAULT N'',
        ContactPhone NVARCHAR(50) NOT NULL DEFAULT N'',
        ContactEmail NVARCHAR(100) NOT NULL DEFAULT N'',
        Consignee NVARCHAR(300) NOT NULL DEFAULT N'',
        NotifyParty NVARCHAR(300) NOT NULL DEFAULT N'',
        ShippingMarks NVARCHAR(500) NOT NULL DEFAULT N'',
        BankInfo NVARCHAR(1000) NOT NULL DEFAULT N'',
        TradeTerms NVARCHAR(50) NOT NULL DEFAULT N'',
        PortOfLoading NVARCHAR(100) NOT NULL DEFAULT N'',
        PortOfDestination NVARCHAR(100) NOT NULL DEFAULT N'',
        PaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        ShippingTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        LeadTime NVARCHAR(100) NOT NULL DEFAULT N'',
        Currency INT NOT NULL DEFAULT 2,
        ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmountCny DECIMAL(18,4) NOT NULL DEFAULT 0,
        DepositRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        DepositAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
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

IF OBJECT_ID('db_owner.ProformaInvoiceDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.ProformaInvoiceDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PiId BIGINT NOT NULL DEFAULT 0,
        PiNo NVARCHAR(50) NOT NULL DEFAULT N'',
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
END");

        // 21.2 形式发票 PI 菜单（挂在「询报价」分组下，与报价单同级）+ 幂等授权
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'proforma-invoice' AND IsDeleted = 0)
BEGIN
    DECLARE @pInq2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'inquiry' AND IsDeleted = 0);
    IF @pInq2 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pInq2, N'形式发票 PI', N'proforma-invoice', N'/sales/proforma-invoice', N'file-text', 20, 2, N'sales:proforma-invoice', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT x.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysMenus m
JOIN db_owner.SysMenus sib ON sib.MenuCode IN (N'inquiry', N'quotation') AND sib.IsDeleted = 0
JOIN db_owner.SysRoleMenus x ON x.MenuId = sib.Id AND x.IsDeleted = 0
WHERE m.MenuCode = N'proforma-invoice' AND m.IsDeleted = 0
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus y
                  WHERE y.RoleId = x.RoleId AND y.MenuId = m.Id AND y.IsDeleted = 0);

IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 18 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (18, N'PI', N'形式发票 PI', N'PI', N'yyyyMMdd', 4, N'', 0, 1, N'形式发票 PI（报价单 → PI → 销售订单）', GETDATE(), 0);

IF NOT EXISTS (SELECT 1 FROM db_owner.SysParameters WHERE ParamKey = N'PI_BankInfo' AND IsDeleted = 0)
    INSERT INTO db_owner.SysParameters (ParamKey, ParamValue, ParamName, Description, IsSystem, CreatedAt, IsDeleted)
    VALUES (N'PI_BankInfo', N'', N'PI 银行信息（默认）', N'转 PI 时自动带入的收款银行信息（Beneficiary / Bank / Account / SWIFT，多行文本）；PI 单据上可逐单覆盖', 0, GETDATE(), 0);");

        // 22. 阶段 3：销售订单 / 采购订单追溯字段（ERP-008 幂等补齐）
        // 说明：销售订单与采购订单由 EF 主子表承载（db_owner.SalesOrders / db_owner.PurchaseOrders），
        //       建表由 EF EnsureCreated 完成，此处只做「缺列补齐」，并用 OBJECT_ID 兜底防止表缺失时中断启动。
        //       新列一律带默认值（空串 / 0 / 0 位），历史单据可正常打开、列表与打印不受影响。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SalesOrders') IS NOT NULL
BEGIN
    IF COL_LENGTH('db_owner.SalesOrders', 'CustomerPoNo') IS NULL             ALTER TABLE db_owner.SalesOrders ADD CustomerPoNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'ContractNo') IS NULL               ALTER TABLE db_owner.SalesOrders ADD ContractNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'TradeTerms') IS NULL               ALTER TABLE db_owner.SalesOrders ADD TradeTerms NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'DestinationPort') IS NULL          ALTER TABLE db_owner.SalesOrders ADD DestinationPort NVARCHAR(100) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'Consignee') IS NULL                ALTER TABLE db_owner.SalesOrders ADD Consignee NVARCHAR(300) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'NotifyParty') IS NULL              ALTER TABLE db_owner.SalesOrders ADD NotifyParty NVARCHAR(300) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'ShippingMarks') IS NULL            ALTER TABLE db_owner.SalesOrders ADD ShippingMarks NVARCHAR(500) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'SourceQuotationId') IS NULL        ALTER TABLE db_owner.SalesOrders ADD SourceQuotationId BIGINT NULL;
    IF COL_LENGTH('db_owner.SalesOrders', 'SourceQuotationNo') IS NULL        ALTER TABLE db_owner.SalesOrders ADD SourceQuotationNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'SourcePiId') IS NULL               ALTER TABLE db_owner.SalesOrders ADD SourcePiId BIGINT NULL;
    IF COL_LENGTH('db_owner.SalesOrders', 'SourcePiNo') IS NULL               ALTER TABLE db_owner.SalesOrders ADD SourcePiNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'ExportMode') IS NULL               ALTER TABLE db_owner.SalesOrders ADD ExportMode NVARCHAR(20) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'CommissionRatio') IS NULL          ALTER TABLE db_owner.SalesOrders ADD CommissionRatio DECIMAL(18,4) NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.SalesOrders', 'BusinessNature') IS NULL           ALTER TABLE db_owner.SalesOrders ADD BusinessNature NVARCHAR(20) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'SplitShipment') IS NULL            ALTER TABLE db_owner.SalesOrders ADD SplitShipment BIT NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.SalesOrders', 'InspectionRequirement') IS NULL    ALTER TABLE db_owner.SalesOrders ADD InspectionRequirement NVARCHAR(500) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.SalesOrders', 'PackagingRequirement') IS NULL     ALTER TABLE db_owner.SalesOrders ADD PackagingRequirement NVARCHAR(500) NOT NULL DEFAULT N'';
END

IF OBJECT_ID('db_owner.PurchaseOrders') IS NOT NULL
BEGIN
    IF COL_LENGTH('db_owner.PurchaseOrders', 'OwningCustomerId') IS NULL      ALTER TABLE db_owner.PurchaseOrders ADD OwningCustomerId BIGINT NULL;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'OwningCustomerName') IS NULL    ALTER TABLE db_owner.PurchaseOrders ADD OwningCustomerName NVARCHAR(200) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.PurchaseOrders', 'OwningSalesOrderId') IS NULL    ALTER TABLE db_owner.PurchaseOrders ADD OwningSalesOrderId BIGINT NULL;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'OwningSalesOrderNo') IS NULL    ALTER TABLE db_owner.PurchaseOrders ADD OwningSalesOrderNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.PurchaseOrders', 'AdvanceOnBehalf') IS NULL       ALTER TABLE db_owner.PurchaseOrders ADD AdvanceOnBehalf BIT NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'SupplierConfirmedDate') IS NULL ALTER TABLE db_owner.PurchaseOrders ADD SupplierConfirmedDate DATETIME2 NULL;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'TaxRate') IS NULL               ALTER TABLE db_owner.PurchaseOrders ADD TaxRate DECIMAL(18,4) NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'TaxIncluded') IS NULL           ALTER TABLE db_owner.PurchaseOrders ADD TaxIncluded BIT NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.PurchaseOrders', 'ArrivalProgress') IS NULL       ALTER TABLE db_owner.PurchaseOrders ADD ArrivalProgress NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.PurchaseOrders', 'QcStatus') IS NULL              ALTER TABLE db_owner.PurchaseOrders ADD QcStatus NVARCHAR(30) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.PurchaseOrders', 'ContractNo') IS NULL            ALTER TABLE db_owner.PurchaseOrders ADD ContractNo NVARCHAR(50) NOT NULL DEFAULT N'';
    IF COL_LENGTH('db_owner.PurchaseOrders', 'SettlementProgress') IS NULL    ALTER TABLE db_owner.PurchaseOrders ADD SettlementProgress NVARCHAR(50) NOT NULL DEFAULT N'';
END");

        // 23. 阶段 4：库存单据（盘点/调拨/退货）+ 库存流水（ERP-009 幂等补齐）
        // 说明：四张库存单据主/明细表由 EF 主子表承载（DbSet 属性名即表名），此处以 IF NOT EXISTS 补齐；
        //       库存流水 StockMovements 是「已审核单据改库存恰好一次」与库存估价的成本基准，
        //       销审不删流水、只追加红字流水（IsReversal / ReversalOfMovementId / IsReversed）。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.StockMovements') IS NULL
BEGIN
    CREATE TABLE db_owner.StockMovements (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        MovementDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        MovementType INT NOT NULL DEFAULT 0,
        SourceDocType NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceDocId BIGINT NOT NULL DEFAULT 0,
        SourceDocNo NVARCHAR(50) NOT NULL DEFAULT N'',
        WarehouseId BIGINT NOT NULL DEFAULT 0,
        WarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Direction INT NOT NULL DEFAULT 1,
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        BalanceQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        BalanceAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        BalanceAverageCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        IsReversal BIT NOT NULL DEFAULT 0,
        ReversalOfMovementId BIGINT NULL,
        IsReversed BIT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockMovements_SourceDoc' AND object_id = OBJECT_ID('db_owner.StockMovements'))
    CREATE INDEX IX_StockMovements_SourceDoc ON db_owner.StockMovements(SourceDocType, SourceDocId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StockMovements_Warehouse_Product' AND object_id = OBJECT_ID('db_owner.StockMovements'))
    CREATE INDEX IX_StockMovements_Warehouse_Product ON db_owner.StockMovements(WarehouseId, ProductId);");

        // 23.1 库存盘点/调整单主表 + 明细
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.StockAdjustments') IS NULL
BEGIN
    CREATE TABLE db_owner.StockAdjustments (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AdjustmentNo NVARCHAR(50) NOT NULL,
        AdjustmentDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        WarehouseId BIGINT NOT NULL DEFAULT 0,
        WarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        AdjustType NVARCHAR(20) NOT NULL DEFAULT N'',
        TotalDiffQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalDiffAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
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

IF OBJECT_ID('db_owner.StockAdjustmentDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.StockAdjustmentDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        StockAdjustmentId BIGINT NOT NULL DEFAULT 0,
        AdjustmentNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortNo INT NOT NULL DEFAULT 0,
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        BookQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        ActualQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        DiffQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        DiffAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 23.2 仓库调拨单主表 + 明细
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.StockTransfers') IS NULL
BEGIN
    CREATE TABLE db_owner.StockTransfers (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TransferNo NVARCHAR(50) NOT NULL,
        TransferDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        FromWarehouseId BIGINT NOT NULL DEFAULT 0,
        FromWarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        ToWarehouseId BIGINT NOT NULL DEFAULT 0,
        ToWarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        TotalQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
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

IF OBJECT_ID('db_owner.StockTransferDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.StockTransferDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        StockTransferId BIGINT NOT NULL DEFAULT 0,
        TransferNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortNo INT NOT NULL DEFAULT 0,
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        BatchNo NVARCHAR(50) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 23.3 销售退货单主表 + 明细
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SalesReturns') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesReturns (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ReturnNo NVARCHAR(50) NOT NULL,
        ReturnDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        CustomerId BIGINT NULL,
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        WarehouseId BIGINT NOT NULL DEFAULT 0,
        WarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        SourceStockOutId BIGINT NULL,
        SourceStockOutNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ReturnReason NVARCHAR(200) NOT NULL DEFAULT N'',
        TotalQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
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

IF OBJECT_ID('db_owner.SalesReturnDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesReturnDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SalesReturnId BIGINT NOT NULL DEFAULT 0,
        ReturnNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortNo INT NOT NULL DEFAULT 0,
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 23.4 采购退货单主表 + 明细
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.PurchaseReturns') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseReturns (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ReturnNo NVARCHAR(50) NOT NULL,
        ReturnDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        SupplierId BIGINT NULL,
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        WarehouseId BIGINT NOT NULL DEFAULT 0,
        WarehouseName NVARCHAR(100) NOT NULL DEFAULT N'',
        SourceStockInId BIGINT NULL,
        SourceStockInNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ReturnReason NVARCHAR(200) NOT NULL DEFAULT N'',
        TotalQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        TotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
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

IF OBJECT_ID('db_owner.PurchaseReturnDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseReturnDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PurchaseReturnId BIGINT NOT NULL DEFAULT 0,
        ReturnNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortNo INT NOT NULL DEFAULT 0,
        ProductId BIGINT NULL,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        Amount DECIMAL(18,4) NOT NULL DEFAULT 0,
        UnitCost DECIMAL(18,6) NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END");

        // 23.5 库存表补充成本列（移动加权平均成本单价 6 位、库存金额 4 位；历史数据默认 0 不影响现有功能）
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.Stocks') IS NOT NULL
BEGIN
    IF COL_LENGTH('db_owner.Stocks', 'AverageCost') IS NULL ALTER TABLE db_owner.Stocks ADD AverageCost DECIMAL(18,6) NOT NULL DEFAULT 0;
    IF COL_LENGTH('db_owner.Stocks', 'TotalCost') IS NULL   ALTER TABLE db_owner.Stocks ADD TotalCost DECIMAL(18,4) NOT NULL DEFAULT 0;
END");

        // 23.6 库存单据菜单（挂在「库存管理」分组下，与采购入库 / 销售出库 / 库存查询同级）+ 幂等授权
        await db.Database.ExecuteSqlRawAsync(@"
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'stock-adjustment' AND IsDeleted = 0)
BEGIN
    DECLARE @pLog1 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'logistics' AND IsDeleted = 0);
    IF @pLog1 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pLog1, N'库存盘点调整', N'stock-adjustment', N'/logistics/stock-adjustment', N'clipboard-check', 40, 2, N'logistics:stock-adjustment', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'stock-transfer' AND IsDeleted = 0)
BEGIN
    DECLARE @pLog2 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'logistics' AND IsDeleted = 0);
    IF @pLog2 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pLog2, N'仓库调拨', N'stock-transfer', N'/logistics/stock-transfer', N'truck', 50, 2, N'logistics:stock-transfer', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'sales-return' AND IsDeleted = 0)
BEGIN
    DECLARE @pLog3 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'logistics' AND IsDeleted = 0);
    IF @pLog3 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pLog3, N'销售退货', N'sales-return', N'/logistics/sales-return', N'package-minus', 60, 2, N'logistics:sales-return', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'purchase-return' AND IsDeleted = 0)
BEGIN
    DECLARE @pLog4 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'logistics' AND IsDeleted = 0);
    IF @pLog4 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pLog4, N'采购退货', N'purchase-return', N'/logistics/purchase-return', N'package-plus', 70, 2, N'logistics:purchase-return', GETDATE(), 0);
END
IF NOT EXISTS (SELECT 1 FROM db_owner.SysMenus WHERE MenuCode = N'stock-movement' AND IsDeleted = 0)
BEGIN
    DECLARE @pLog5 BIGINT = (SELECT TOP 1 Id FROM db_owner.SysMenus WHERE MenuCode = N'logistics' AND IsDeleted = 0);
    IF @pLog5 IS NOT NULL
        INSERT INTO db_owner.SysMenus (ParentId, MenuName, MenuCode, Path, Icon, SortOrder, MenuType, PermissionCode, CreatedAt, IsDeleted)
        VALUES (@pLog5, N'库存流水', N'stock-movement', N'/logistics/stock-movement', N'scroll', 80, 2, N'logistics:stock-movement', GETDATE(), 0);
END

INSERT INTO db_owner.SysRoleMenus (RoleId, MenuId, CreatedAt, IsDeleted)
SELECT DISTINCT rm.RoleId, m.Id, GETDATE(), 0
FROM db_owner.SysRoleMenus rm
JOIN db_owner.SysMenus m ON m.MenuCode IN
    (N'stock-adjustment', N'stock-transfer', N'sales-return', N'purchase-return', N'stock-movement')
    AND m.IsDeleted = 0
WHERE rm.IsDeleted = 0
  AND rm.MenuId = m.ParentId
  AND NOT EXISTS (SELECT 1 FROM db_owner.SysRoleMenus x
                  WHERE x.RoleId = rm.RoleId AND x.MenuId = m.Id AND x.IsDeleted = 0);

IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 19 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (19, N'PD', N'库存盘点调整单', N'PD', N'yyyyMMdd', 4, N'', 0, 1, N'库存盘点/调整单（ERP-009）', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 20 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (20, N'DB', N'仓库调拨单', N'DB', N'yyyyMMdd', 4, N'', 0, 1, N'仓库调拨单（ERP-009）', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 21 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (21, N'XTH', N'销售退货单', N'XTH', N'yyyyMMdd', 4, N'', 0, 1, N'销售退货单（ERP-009）', GETDATE(), 0);
IF NOT EXISTS (SELECT 1 FROM db_owner.SysDocumentNumberRules WHERE DocumentType = 22 AND IsDeleted = 0)
    INSERT INTO db_owner.SysDocumentNumberRules (DocumentType, RuleCode, RuleName, Prefix, DateFormat, SerialLength, Separator, CurrentSequence, YearlyReset, Remark, CreatedAt, IsDeleted)
    VALUES (22, N'CTH', N'采购退货单', N'CTH', N'yyyyMMdd', 4, N'', 0, 1, N'采购退货单（ERP-009）', GETDATE(), 0);");

        // 24. 报价单版本链（ERP-035：多轮议价版本留痕）
        //     24.1 版本号 / 根单 / 上一版本列（幂等补齐）：RevisionNumber 默认 1 = 初始版本，
        //          因此新增列之前创建的历史报价单读取时即为初始版本 —— 不回填、不改写历史数据；
        //     24.2 链内版本号唯一索引（过滤索引：软删除与根单不参与），并发创建版本时在数据库层兜底。
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.Quotations', 'RevisionNumber') IS NULL
    ALTER TABLE db_owner.Quotations ADD RevisionNumber INT NOT NULL DEFAULT 1;
IF COL_LENGTH('db_owner.Quotations', 'RootQuotationId') IS NULL
    ALTER TABLE db_owner.Quotations ADD RootQuotationId BIGINT NULL;
IF COL_LENGTH('db_owner.Quotations', 'RootQuotationNo') IS NULL
    ALTER TABLE db_owner.Quotations ADD RootQuotationNo NVARCHAR(50) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.Quotations', 'PreviousRevisionId') IS NULL
    ALTER TABLE db_owner.Quotations ADD PreviousRevisionId BIGINT NULL;
IF COL_LENGTH('db_owner.Quotations', 'PreviousRevisionNo') IS NULL
    ALTER TABLE db_owner.Quotations ADD PreviousRevisionNo NVARCHAR(50) NOT NULL DEFAULT N'';

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_Quotations_RevisionChain' AND object_id = OBJECT_ID('db_owner.Quotations'))
    CREATE UNIQUE INDEX UX_Quotations_RevisionChain
        ON db_owner.Quotations(RootQuotationId, RevisionNumber)
        WHERE IsDeleted = 0 AND RootQuotationId IS NOT NULL;");

        // 25. 客户「指定货代」（ERP-036：复用「其他资料」Forwarder 字典项的主数据指引）
        //     25.1 两列为幂等补齐：ForwarderId 可空（历史客户保持 NULL = 未指定，无需回填）、
        //          ForwarderName 为服务端写入的名称快照（默认空串，历史客户不受影响）；
        //     25.2 刻意不建外键：字典项可软删除 / 停用，历史引用必须继续可读；
        //     25.3 不新增任何单据关联列 —— 指定货代不会自动写入订舱 / 装柜 / 报关 / 费用单据。
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.BaseCustomers', 'ForwarderId') IS NULL
    ALTER TABLE db_owner.BaseCustomers ADD ForwarderId BIGINT NULL;
IF COL_LENGTH('db_owner.BaseCustomers', 'ForwarderName') IS NULL
    ALTER TABLE db_owner.BaseCustomers ADD ForwarderName NVARCHAR(100) NOT NULL DEFAULT N'';");

        // 26. 商品规格变体（ERP-037：商品资料下的颜色 / 尺码 SKU 子表）
        //     26.1 建表为幂等补齐：历史商品没有任何规格行 = 单规格商品，读取 / 编辑 / 打印行为不变，
        //          因此**不做任何回填**（不生成默认规格、不split已有库存、不改写任何单据行）；
        //     26.2 两个过滤唯一索引与 ErpDbContext 模型同名同过滤条件：
        //          - UX_BaseProductVariants_ProductCode：规格编码在同商品内唯一（软删除行不占用编码）；
        //          - UX_BaseProductVariants_ProductColorSize：启用状态下「颜色 + 尺码」组合在同商品内唯一
        //            （停用行作为历史保留、不占用组合）；
        //     26.3 表内不建外键、不被任何单据引用：规格是主数据细分，不参与库存数量 / 成本口径。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.BaseProductVariants') IS NULL
BEGIN
    CREATE TABLE db_owner.BaseProductVariants (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ProductId BIGINT NOT NULL,
        VariantCode NVARCHAR(50) NOT NULL DEFAULT N'',
        Color NVARCHAR(50) NOT NULL DEFAULT N'',
        Size NVARCHAR(50) NOT NULL DEFAULT N'',
        ColorSizeKey NVARCHAR(120) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_BaseProductVariants_ProductCode'
                 AND object_id = OBJECT_ID('db_owner.BaseProductVariants'))
    CREATE UNIQUE INDEX UX_BaseProductVariants_ProductCode
        ON db_owner.BaseProductVariants(ProductId, VariantCode)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_BaseProductVariants_ProductColorSize'
                 AND object_id = OBJECT_ID('db_owner.BaseProductVariants'))
    CREATE UNIQUE INDEX UX_BaseProductVariants_ProductColorSize
        ON db_owner.BaseProductVariants(ProductId, ColorSizeKey)
        WHERE IsDeleted = 0 AND Status = 1;");

        // 27. 商品 / SKU 货源关系（ERP-038：多供应商货源指引子表）
        //     27.1 建表为幂等补齐：历史商品 / 供应商没有任何货源关系行 = 行为完全不变，
        //          因此**不做任何回填**（不自动选供应商、不生成采购报价或采购订单、不改写库存与历史单据）；
        //     27.2 ScopeKey 为服务端按规格推导的作用域键（P = 商品级；V{规格Id} = 规格级）：
        //          SQL Server 唯一索引对 NULL 不去重，用该列把「同一范围 + 同一供应商不重复」
        //          与「同一范围只有一个启用首选」落到数据库层；
        //     27.3 两个过滤唯一索引与 ErpDbContext 模型同名同过滤条件；
        //     27.4 表内不建外键、不被任何单据引用：货源关系是主数据指引，不参与定价、库存数量与成本口径。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.BaseProductSuppliers') IS NULL
BEGIN
    CREATE TABLE db_owner.BaseProductSuppliers (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ProductId BIGINT NOT NULL,
        VariantId BIGINT NULL,
        ScopeKey NVARCHAR(30) NOT NULL DEFAULT N'',
        SupplierId BIGINT NOT NULL,
        SupplierItemCode NVARCHAR(100) NOT NULL DEFAULT N'',
        PurchaseUnit NVARCHAR(20) NOT NULL DEFAULT N'',
        MinOrderQty DECIMAL(18,4) NOT NULL DEFAULT 0,
        LeadTimeDays INT NOT NULL DEFAULT 0,
        IsPreferred BIT NOT NULL DEFAULT 0,
        Status INT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_BaseProductSuppliers_ScopeSupplier'
                 AND object_id = OBJECT_ID('db_owner.BaseProductSuppliers'))
    CREATE UNIQUE INDEX UX_BaseProductSuppliers_ScopeSupplier
        ON db_owner.BaseProductSuppliers(ProductId, ScopeKey, SupplierId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_BaseProductSuppliers_ScopePreferred'
                 AND object_id = OBJECT_ID('db_owner.BaseProductSuppliers'))
    CREATE UNIQUE INDEX UX_BaseProductSuppliers_ScopePreferred
        ON db_owner.BaseProductSuppliers(ProductId, ScopeKey)
        WHERE IsDeleted = 0 AND Status = 1 AND IsPreferred = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_BaseProductSuppliers_SupplierId'
                 AND object_id = OBJECT_ID('db_owner.BaseProductSuppliers'))
    CREATE INDEX IX_BaseProductSuppliers_SupplierId
        ON db_owner.BaseProductSuppliers(SupplierId)
        WHERE IsDeleted = 0;");

        // 28. 订柜信息的外贸 / 物流跟踪字段（ERP-040：订柜信息 = 本套跟踪值的权威记录）
        //     28.1 各列幂等补齐：文本列 NOT NULL DEFAULT N''、日期与报关行 Id 可空 ——
        //          历史订柜记录的默认值即「未知 / 未填写」，因此**不做任何回填**，
        //          也不为任何单据臆造 ETD / ETA / ATD / ATA / 查验 / 放行日期；
        //     28.2 刻意不建外键：报关行是「其他资料」InfoType=CustomsBroker 字典项，允许软删除 / 停用，
        //          历史订柜记录必须继续可读（外键会阻止字典项删除或使读取失败）；
        //     28.3 InspectionRequired 为 BIT NULL 三态：NULL = 未知（未标注）、0 = 不需要查验、1 = 需要查验，
        //          三者互不混淆，绝不把「未知」落成「不需要」；
        //     28.4 预装柜单 / 装柜清单**不加任何跟踪列**：它们只按持久化引用（ContainerPreLoading.BookingId）
        //          只读回显订柜信息，不维护第二份跟踪值，也不按柜号等自由文本匹配；
        //     28.5 本段只加列，不建表、不改写其他表，也不调用任何外部跟踪系统。
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.ContainerBooking', 'ShipmentMode') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD ShipmentMode NVARCHAR(10) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'BillOfLadingNo') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD BillOfLadingNo NVARCHAR(50) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'ShippingOrderNo') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD ShippingOrderNo NVARCHAR(50) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'TransitPort') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD TransitPort NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'Etd') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD Etd DATETIME2 NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'Eta') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD Eta DATETIME2 NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'Atd') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD Atd DATETIME2 NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'Ata') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD Ata DATETIME2 NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'TruckerName') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD TruckerName NVARCHAR(200) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'CustomsBrokerId') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD CustomsBrokerId BIGINT NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'CustomsBrokerName') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD CustomsBrokerName NVARCHAR(100) NOT NULL DEFAULT N'';
IF COL_LENGTH('db_owner.ContainerBooking', 'InspectionRequired') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD InspectionRequired BIT NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'InspectionDate') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD InspectionDate DATETIME2 NULL;
IF COL_LENGTH('db_owner.ContainerBooking', 'CustomsReleaseDate') IS NULL
    ALTER TABLE db_owner.ContainerBooking ADD CustomsReleaseDate DATETIME2 NULL;");

        // 29. 装柜清单多客户参与方（ERP-041：一柜多客户的客户归属清单子表）
        //     29.1 建表为幂等补齐：历史装柜清单没有任何参与方行 = 行为完全不变，
        //          继续按 ContainerLoadingList.CustomerId 单客户读取，因此**不做任何回填**，
        //          也不按箱数 / 体积 / 金额替客户分摊费用或生成任何单据；
        //     29.2 兼容主客户字段：只有显式指定主参与方时，服务端才把参与方客户写回 CustomerId
        //          （与参与方同一次 SaveChanges），历史清单的 CustomerId 原样保留、读取不写库；
        //     29.3 两个过滤唯一索引与 ErpDbContext 模型同名同过滤条件：
        //          UX_ContainerLoadingListParticipants_ListCustomer —— 同一清单内同一客户不重复（软删除行不占用）；
        //          UX_ContainerLoadingListParticipants_ListPrimary  —— 同一清单最多一条**启用中**主参与方；
        //          另建 IX_ContainerLoadingListParticipants_CustomerId 供客户侧有界检索；
        //     29.4 表内不建外键、不被任何单据引用：客户软删除 / 停用后历史参与方仍可读
        //          （按编码 / 名称快照显示并显式标注不可用），无需任何历史数据修补；
        //     29.5 本段只建本表与其索引，不改写装柜清单、装柜明细、订柜跟踪值、单证、费用与库存。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.ContainerLoadingListParticipants') IS NULL
BEGIN
    CREATE TABLE db_owner.ContainerLoadingListParticipants (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        LoadingListId BIGINT NOT NULL,
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        IsPrimary BIT NOT NULL DEFAULT 0,
        Status INT NOT NULL DEFAULT 1,
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ContainerLoadingListParticipants_ListCustomer'
                 AND object_id = OBJECT_ID('db_owner.ContainerLoadingListParticipants'))
    CREATE UNIQUE INDEX UX_ContainerLoadingListParticipants_ListCustomer
        ON db_owner.ContainerLoadingListParticipants(LoadingListId, CustomerId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ContainerLoadingListParticipants_ListPrimary'
                 AND object_id = OBJECT_ID('db_owner.ContainerLoadingListParticipants'))
    CREATE UNIQUE INDEX UX_ContainerLoadingListParticipants_ListPrimary
        ON db_owner.ContainerLoadingListParticipants(LoadingListId)
        WHERE IsDeleted = 0 AND Status = 1 AND IsPrimary = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerLoadingListParticipants_CustomerId'
                 AND object_id = OBJECT_ID('db_owner.ContainerLoadingListParticipants'))
    CREATE INDEX IX_ContainerLoadingListParticipants_CustomerId
        ON db_owner.ContainerLoadingListParticipants(CustomerId)
        WHERE IsDeleted = 0;");

        // 30. 装柜费用分摊批次与来源留痕（ERP-042：既有费用单之上的留痕层）
        //     30.1 只做**幂等补齐**：给既有 FinanceExpenses 增加 3 个**可空 / 空串**留痕列
        //          （AllocationBatchNo / AllocationSourceExpenseId / AllocationSourceExpenseNo）：
        //          历史费用单这些列保持空 = 「历史分摊（无批次留痕）」或「未分摊」，
        //          因此**不做任何回填、不改写任何既有金额 / 归属 / 付款状态**；
        //     30.2 建表为幂等补齐：本表只记录「这次分摊是谁按什么方法 / 基数 / 比例生成的」，
        //          分摊结果仍写既有 FinanceExpenses 行 —— 不引入第二套账务引擎、不生成凭证 / 收付款 / 结算单；
        //     30.3 索引与 ErpDbContext 模型同名同过滤条件：
        //          UX_FinanceExpenseAllocationBatches_BatchNo      —— 批次号唯一；
        //          UX_FinanceExpenseAllocationBatches_SourceLive   —— 同一「来源费用 + 装柜清单 + 分摊方法」
        //                                                             最多一条**有效**批次（并发兜底；服务端业务
        //                                                             口径更严：同一来源费用 + 清单不区分方法只
        //                                                             允许一条有效批次，作废后可重生成）；
        //          IX_FinanceExpenseAllocationBatches_LoadingListId —— 按装柜清单有界检索；
        //          IX_FinanceExpenses_AllocationBatchNo            —— 读取侧按批次号一次批量解析批次状态；
        //     30.4 批次 / 分摊行不建到装柜清单 / 参与方 / 客户的数据库外键（软删除与停用后历史留痕仍必须可读），
        //          也不被任何其他单据引用；费用单列的留痕刻意只用快照，避免编辑费用单时被静默清空；
        //     30.5 本段只加列 / 建本表与其索引，不改写装柜清单、装柜明细、参与方、订柜跟踪值、单证、库存与订单。
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationBatchNo') IS NULL
    ALTER TABLE db_owner.FinanceExpenses ADD AllocationBatchNo NVARCHAR(50) NULL;

IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationSourceExpenseId') IS NULL
    ALTER TABLE db_owner.FinanceExpenses ADD AllocationSourceExpenseId BIGINT NULL;

IF COL_LENGTH('db_owner.FinanceExpenses', 'AllocationSourceExpenseNo') IS NULL
    ALTER TABLE db_owner.FinanceExpenses ADD AllocationSourceExpenseNo NVARCHAR(50) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FinanceExpenses_AllocationBatchNo'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenses'))
    CREATE INDEX IX_FinanceExpenses_AllocationBatchNo
        ON db_owner.FinanceExpenses(AllocationBatchNo)
        WHERE IsDeleted = 0 AND AllocationBatchNo <> N'';

IF OBJECT_ID('db_owner.FinanceExpenseAllocationBatches') IS NULL
BEGIN
    CREATE TABLE db_owner.FinanceExpenseAllocationBatches (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BatchNo NVARCHAR(50) NOT NULL,
        SourceExpenseId BIGINT NOT NULL,
        SourceExpenseNo NVARCHAR(50) NOT NULL DEFAULT N'',
        LoadingListId BIGINT NOT NULL,
        LoadingListNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ContainerNo NVARCHAR(50) NOT NULL DEFAULT N'',
        AllocationMethod NVARCHAR(30) NOT NULL DEFAULT N'',
        BasisKind NVARCHAR(30) NOT NULL DEFAULT N'',
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        ExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 1,
        SourceAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        AllocatedTotal DECIMAL(18,2) NOT NULL DEFAULT 0,
        LineCount INT NOT NULL DEFAULT 0,
        Status INT NOT NULL DEFAULT 1,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_FinanceExpenseAllocationBatches_BatchNo'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationBatches'))
    CREATE UNIQUE INDEX UX_FinanceExpenseAllocationBatches_BatchNo
        ON db_owner.FinanceExpenseAllocationBatches(BatchNo);

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_FinanceExpenseAllocationBatches_SourceLive'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationBatches'))
    CREATE UNIQUE INDEX UX_FinanceExpenseAllocationBatches_SourceLive
        ON db_owner.FinanceExpenseAllocationBatches(SourceExpenseId, LoadingListId, AllocationMethod)
        WHERE IsDeleted = 0 AND Status = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FinanceExpenseAllocationBatches_LoadingListId'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationBatches'))
    CREATE INDEX IX_FinanceExpenseAllocationBatches_LoadingListId
        ON db_owner.FinanceExpenseAllocationBatches(LoadingListId)
        WHERE IsDeleted = 0;");

        // 30.6 分摊行表（批次 → 参与方 的逐行留痕）：
        //      - 只为「由本批次生成的费用单行」和「生成依据」留痕，不参与任何金额计算的口径改写；
        //      - 同一批次内同一参与方不重复（UX_FinanceExpenseAllocationLines_BatchParticipant）；
        //      - 唯一外键是「分摊行 → 生成的费用单行」（FK_FinanceExpenseAllocationLines_Expense）：
        //        仅用于让 EF 在同一次 SaveChanges 内回填 ExpenseId；费用单只做软删除，因此不影响删除语义；
        //        刻意**不建**到参与方 / 客户 / 装柜清单的外键：参与方停用、客户删除、清单软删后
        //        历史留痕仍必须可读（这些引用只存编码 / 名称 / 单号快照）；
        //      - 本段只建本表与其索引，不写任何数据。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.FinanceExpenseAllocationLines') IS NULL
BEGIN
    CREATE TABLE db_owner.FinanceExpenseAllocationLines (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BatchId BIGINT NOT NULL,
        BatchNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceExpenseId BIGINT NOT NULL,
        SourceExpenseNo NVARCHAR(50) NOT NULL DEFAULT N'',
        LoadingListId BIGINT NOT NULL,
        LoadingListNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ContainerNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ParticipantId BIGINT NOT NULL,
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        ParticipantPrimary BIT NOT NULL DEFAULT 0,
        AllocationMethod NVARCHAR(30) NOT NULL DEFAULT N'',
        BasisKind NVARCHAR(30) NOT NULL DEFAULT N'',
        BasisSource NVARCHAR(30) NOT NULL DEFAULT N'',
        BasisValue DECIMAL(18,4) NOT NULL DEFAULT 0,
        Ratio DECIMAL(18,4) NOT NULL DEFAULT 0,
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        AllocatedAmountCny DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        ExpenseId BIGINT NULL,
        ExpenseNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_FinanceExpenseAllocationLines_BatchParticipant'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationLines'))
    CREATE UNIQUE INDEX UX_FinanceExpenseAllocationLines_BatchParticipant
        ON db_owner.FinanceExpenseAllocationLines(BatchId, ParticipantId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FinanceExpenseAllocationLines_SourceExpenseId'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationLines'))
    CREATE INDEX IX_FinanceExpenseAllocationLines_SourceExpenseId
        ON db_owner.FinanceExpenseAllocationLines(SourceExpenseId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FinanceExpenseAllocationLines_ParticipantId'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationLines'))
    CREATE INDEX IX_FinanceExpenseAllocationLines_ParticipantId
        ON db_owner.FinanceExpenseAllocationLines(ParticipantId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_FinanceExpenseAllocationLines_ExpenseId'
                 AND object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationLines'))
    CREATE INDEX IX_FinanceExpenseAllocationLines_ExpenseId
        ON db_owner.FinanceExpenseAllocationLines(ExpenseId);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_FinanceExpenseAllocationLines_Expense'
                 AND parent_object_id = OBJECT_ID('db_owner.FinanceExpenseAllocationLines'))
    ALTER TABLE db_owner.FinanceExpenseAllocationLines
        ADD CONSTRAINT FK_FinanceExpenseAllocationLines_Expense
        FOREIGN KEY (ExpenseId) REFERENCES db_owner.FinanceExpenses(Id);");

        // 31. 供应商采购发票登记（ERP-043：普票 / 专票证据台账 + 可选的采购订单关联）
        //     31.1 只建「发票 + 关联行」两张表与其索引 / 外键：**不含任何 UPDATE / 回填语句**，
        //          既有供应商与采购订单不因本段产生任何变化（没有发票数据时行为与历史完全一致）；
        //     31.2 有效身份唯一：UX_PurchaseInvoices_ActiveIdentity（供应商 + 发票类型 + 规范化代码 / 号码），
        //          过滤 IsDeleted = 0 AND Status <> 2 —— 已作废记录保留可读但不占用身份（可重新登记）；
        //     31.3 同一发票内同一采购订单不重复：UX_PurchaseInvoiceAllocations_InvoiceOrder（过滤 IsDeleted = 0）；
        //     31.4 唯一外键是「关联行 → 发票」（级联，发票仍只做软删除）；关联行**刻意不建**到采购订单 / 供应商的
        //          外键，也不在采购订单上加任何列 —— 订单软删除 / 取消、供应商停用或改名都不影响历史证据可读；
        //     31.5 本段只建本模块两张表与其索引，不改写采购订单、库存与库存成本、退税、付款与供应商数据。
        //     31.6 ERP-065 只**追加**两个可选证据列：DueDate（DATETIME2 NULL = 「未知」）与
        //          PaymentTerms（NVARCHAR(200) NOT NULL DEFAULT N'' = 「未提供」）：新建库由上面的建表语句包含，
        //          既有库由本段末尾的 IF COL_LENGTH(...) IS NULL 幂等加列补齐（**刻意留在第 31 段内**：
        //          第 37 段及以后的模块段落有「只建表 / 建索引、不改既有表」的既有契约，加列不得追加到其后）；
        //          历史行保持 NULL / 空串，不含任何回填，也不改写任何既有列与单据。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.PurchaseInvoices') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseInvoices (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InvoiceType NVARCHAR(20) NOT NULL DEFAULT N'普票',
        InvoiceCode NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceNumber NVARCHAR(50) NOT NULL DEFAULT N'',
        NormalizedInvoiceCode NVARCHAR(50) NOT NULL DEFAULT N'',
        NormalizedInvoiceNumber NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceDate DATETIME2 NOT NULL,
        SupplierId BIGINT NOT NULL,
        SupplierCode NVARCHAR(50) NOT NULL DEFAULT N'',
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        NetAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        GrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        DueDate DATETIME2 NULL,
        PaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 0,
        RecordedAt DATETIME2 NULL,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_PurchaseInvoices_ActiveIdentity'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoices'))
    CREATE UNIQUE INDEX UX_PurchaseInvoices_ActiveIdentity
        ON db_owner.PurchaseInvoices(SupplierId, InvoiceType, NormalizedInvoiceCode, NormalizedInvoiceNumber)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PurchaseInvoices_SupplierId'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoices'))
    CREATE INDEX IX_PurchaseInvoices_SupplierId
        ON db_owner.PurchaseInvoices(SupplierId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PurchaseInvoices_Status_InvoiceDate'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoices'))
    CREATE INDEX IX_PurchaseInvoices_Status_InvoiceDate
        ON db_owner.PurchaseInvoices(Status, InvoiceDate)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PurchaseInvoices_NormalizedInvoiceNumber'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoices'))
    CREATE INDEX IX_PurchaseInvoices_NormalizedInvoiceNumber
        ON db_owner.PurchaseInvoices(NormalizedInvoiceNumber)
        WHERE IsDeleted = 0;

IF OBJECT_ID('db_owner.PurchaseInvoiceAllocations') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseInvoiceAllocations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PurchaseInvoiceId BIGINT NOT NULL,
        PurchaseOrderId BIGINT NOT NULL,
        OrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        OrderDate DATETIME2 NOT NULL,
        OrderCurrency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        SupplierId BIGINT NOT NULL,
        SupplierCode NVARCHAR(50) NOT NULL DEFAULT N'',
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_PurchaseInvoiceAllocations_InvoiceOrder'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoiceAllocations'))
    CREATE UNIQUE INDEX UX_PurchaseInvoiceAllocations_InvoiceOrder
        ON db_owner.PurchaseInvoiceAllocations(PurchaseInvoiceId, PurchaseOrderId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PurchaseInvoiceAllocations_PurchaseOrderId'
                 AND object_id = OBJECT_ID('db_owner.PurchaseInvoiceAllocations'))
    CREATE INDEX IX_PurchaseInvoiceAllocations_PurchaseOrderId
        ON db_owner.PurchaseInvoiceAllocations(PurchaseOrderId);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_PurchaseInvoiceAllocations_Invoice'
                 AND parent_object_id = OBJECT_ID('db_owner.PurchaseInvoiceAllocations'))
    ALTER TABLE db_owner.PurchaseInvoiceAllocations
        ADD CONSTRAINT FK_PurchaseInvoiceAllocations_Invoice
        FOREIGN KEY (PurchaseInvoiceId) REFERENCES db_owner.PurchaseInvoices(Id);

IF COL_LENGTH('db_owner.PurchaseInvoices', 'DueDate') IS NULL
    ALTER TABLE db_owner.PurchaseInvoices ADD DueDate DATETIME2 NULL;

IF COL_LENGTH('db_owner.PurchaseInvoices', 'PaymentTerms') IS NULL
    ALTER TABLE db_owner.PurchaseInvoices ADD PaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'';
");

        // 32. 业务单据附件引用登记（ERP-045：仅元数据的附件引用册）
        //     32.1 只建「附件引用」一张表与其索引：**不含任何 UPDATE / 回填语句**，
        //          父单据（销售订单 / 采购订单 / 装柜清单 / 单证）与既有 FileNote、商品图片位
        //          不因本段产生任何变化（没有引用数据时行为与历史完全一致）；
        //     32.2 父单据只保存服务端写入的号码 / 类型快照，**刻意不建**任何外键，也不在父单据上加列 ——
        //          父单据改名、停用或软删除都不影响历史引用可读，本表也不参与父单据的金额 / 库存 / 财务 / 出运计算；
        //     32.3 有效身份唯一：UX_DocumentAttachmentReferences_ActiveIdentity
        //          （父单据类型 + 父单据 Id + 分类 + 不透明引用标识），过滤 IsDeleted = 0 AND Status = 0 ——
        //          已作废记录保留可读但不占用身份（作废后可重新登记同一引用标识）；
        //     32.4 索引与 ErpDbContext 模型同名同过滤条件：父单据 / 引用标识均有界检索；
        //     32.5 本段只建本模块一张表与其索引，不含任何对象存储读写、抓取或删除操作。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.DocumentAttachmentReferences') IS NULL
BEGIN
    CREATE TABLE db_owner.DocumentAttachmentReferences (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ParentType NVARCHAR(30) NOT NULL,
        ParentId BIGINT NOT NULL,
        ParentNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ParentTypeText NVARCHAR(30) NOT NULL DEFAULT N'',
        Category NVARCHAR(30) NOT NULL,
        DisplayName NVARCHAR(200) NOT NULL,
        ReferenceId NVARCHAR(200) NOT NULL,
        ContentType NVARCHAR(120) NOT NULL DEFAULT N'',
        SizeBytes BIGINT NOT NULL DEFAULT 0,
        Checksum NVARCHAR(128) NOT NULL DEFAULT N'',
        Notes NVARCHAR(500) NOT NULL DEFAULT N'',
        SourceAuthorizationAcknowledged BIT NOT NULL DEFAULT 0,
        SourceAuthorizationNote NVARCHAR(300) NOT NULL DEFAULT N'',
        AuthorizedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        AuthorizedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        RegisteredAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        Status INT NOT NULL DEFAULT 0,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_DocumentAttachmentReferences_ActiveIdentity'
                 AND object_id = OBJECT_ID('db_owner.DocumentAttachmentReferences'))
    CREATE UNIQUE INDEX UX_DocumentAttachmentReferences_ActiveIdentity
        ON db_owner.DocumentAttachmentReferences(ParentType, ParentId, Category, ReferenceId)
        WHERE IsDeleted = 0 AND Status = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_DocumentAttachmentReferences_ParentType_ParentId'
                 AND object_id = OBJECT_ID('db_owner.DocumentAttachmentReferences'))
    CREATE INDEX IX_DocumentAttachmentReferences_ParentType_ParentId
        ON db_owner.DocumentAttachmentReferences(ParentType, ParentId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_DocumentAttachmentReferences_ReferenceId'
                 AND object_id = OBJECT_ID('db_owner.DocumentAttachmentReferences'))
    CREATE INDEX IX_DocumentAttachmentReferences_ReferenceId
        ON db_owner.DocumentAttachmentReferences(ReferenceId)
        WHERE IsDeleted = 0;");

        // 33. 销售订单变更申请登记（ERP-047：只登记拟议变更的不可变登记册）
        //     33.1 只建「申请 + 拟议明细」两张表与其索引 / 外键：**不含任何 UPDATE / 回填语句**，
        //          既有销售订单主表 / 明细表不因本段产生任何变化（没有申请数据时行为与历史完全一致）；
        //     33.2 来源订单只保存服务端写入的快照（Source* 列），**刻意不建**到 SalesOrders / SalesOrderDetails 的外键，
        //          也不在来源订单上加列 —— 来源改名、停用或软删除都不影响历史申请可读；
        //     33.3 申请号在未删除记录内唯一：UX_SalesOrderChangeRequests_RequestNo（过滤 IsDeleted = 0）；
        //     33.4 索引与 ErpDbContext 模型同名同过滤条件：来源订单 / 状态均有界检索；
        //     33.5 本段只建本模块两张表与其索引，不改写销售订单、报价单、出库、装柜与出运、
        //          收款与发票、佣金、库存与库存成本、单证中心与财务数据。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SalesOrderChangeRequests') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesOrderChangeRequests (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        RequestNo NVARCHAR(50) NOT NULL,
        SalesOrderId BIGINT NOT NULL,
        SalesOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceStatus INT NOT NULL DEFAULT 0,
        SourceUpdatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        SourceDetailSignature NVARCHAR(500) NOT NULL DEFAULT N'',
        SourceSnapshotMarker NVARCHAR(300) NOT NULL DEFAULT N'',
        Reason NVARCHAR(500) NOT NULL,
        SourceOrderDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        SourceCustomerId BIGINT NOT NULL DEFAULT 0,
        SourceSalesmanId BIGINT NULL,
        SourceCurrency INT NOT NULL DEFAULT 0,
        SourceExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 0,
        SourceTotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceDepositRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceDepositAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourcePaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        SourceDeliveryDate DATETIME2 NULL,
        SourceShippingMethod NVARCHAR(100) NOT NULL DEFAULT N'',
        SourcePortId BIGINT NULL,
        SourceRemark NVARCHAR(500) NOT NULL DEFAULT N'',
        SourceCustomerPoNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceContractNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceTradeTerms NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceDestinationPort NVARCHAR(100) NOT NULL DEFAULT N'',
        SourceConsignee NVARCHAR(300) NOT NULL DEFAULT N'',
        SourceNotifyParty NVARCHAR(300) NOT NULL DEFAULT N'',
        SourceShippingMarks NVARCHAR(500) NOT NULL DEFAULT N'',
        SourceQuotationNoSnapshot NVARCHAR(50) NOT NULL DEFAULT N'',
        SourcePiNoSnapshot NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceExportMode NVARCHAR(20) NOT NULL DEFAULT N'',
        SourceCommissionRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceBusinessNature NVARCHAR(20) NOT NULL DEFAULT N'',
        SourceSplitShipment BIT NOT NULL DEFAULT 0,
        SourceInspectionRequirement NVARCHAR(500) NOT NULL DEFAULT N'',
        SourcePackagingRequirement NVARCHAR(500) NOT NULL DEFAULT N'',
        ProposedOrderDate DATETIME2 NOT NULL DEFAULT GETDATE(),
        ProposedCustomerId BIGINT NOT NULL DEFAULT 0,
        ProposedSalesmanId BIGINT NULL,
        ProposedCurrency INT NOT NULL DEFAULT 0,
        ProposedExchangeRate DECIMAL(18,6) NOT NULL DEFAULT 0,
        ProposedTotalAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedDepositRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedDepositAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedPaymentTerms NVARCHAR(200) NOT NULL DEFAULT N'',
        ProposedDeliveryDate DATETIME2 NULL,
        ProposedShippingMethod NVARCHAR(100) NOT NULL DEFAULT N'',
        ProposedPortId BIGINT NULL,
        ProposedRemark NVARCHAR(500) NOT NULL DEFAULT N'',
        ProposedCustomerPoNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ProposedContractNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ProposedTradeTerms NVARCHAR(50) NOT NULL DEFAULT N'',
        ProposedDestinationPort NVARCHAR(100) NOT NULL DEFAULT N'',
        ProposedConsignee NVARCHAR(300) NOT NULL DEFAULT N'',
        ProposedNotifyParty NVARCHAR(300) NOT NULL DEFAULT N'',
        ProposedShippingMarks NVARCHAR(500) NOT NULL DEFAULT N'',
        ProposedExportMode NVARCHAR(20) NOT NULL DEFAULT N'',
        ProposedCommissionRatio DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedBusinessNature NVARCHAR(20) NOT NULL DEFAULT N'',
        ProposedSplitShipment BIT NOT NULL DEFAULT 0,
        ProposedInspectionRequirement NVARCHAR(500) NOT NULL DEFAULT N'',
        ProposedPackagingRequirement NVARCHAR(500) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 0,
        SubmittedAt DATETIME2 NULL,
        CancelledAt DATETIME2 NULL,
        CancelledReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_SalesOrderChangeRequests_RequestNo'
                 AND object_id = OBJECT_ID('db_owner.SalesOrderChangeRequests'))
    CREATE UNIQUE INDEX UX_SalesOrderChangeRequests_RequestNo
        ON db_owner.SalesOrderChangeRequests(RequestNo)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SalesOrderChangeRequests_SourceOrder_Status'
                 AND object_id = OBJECT_ID('db_owner.SalesOrderChangeRequests'))
    CREATE INDEX IX_SalesOrderChangeRequests_SourceOrder_Status
        ON db_owner.SalesOrderChangeRequests(SalesOrderId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SalesOrderChangeRequests_Status_CreatedAt'
                 AND object_id = OBJECT_ID('db_owner.SalesOrderChangeRequests'))
    CREATE INDEX IX_SalesOrderChangeRequests_Status_CreatedAt
        ON db_owner.SalesOrderChangeRequests(Status, CreatedAt)
        WHERE IsDeleted = 0;");

        // 33.6 拟议明细表（来源行快照 + 拟议值）+ 明细索引 + 唯一外键「明细 → 申请」：
        //      申请本身仍只做软删除，物理删除时才级联清理明细行；明细不建到销售订单 / 商品的外键。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SalesOrderChangeRequestDetails') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesOrderChangeRequestDetails (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ChangeRequestId BIGINT NOT NULL,
        LineNo INT NOT NULL DEFAULT 0,
        HasSourceLine BIT NOT NULL DEFAULT 0,
        SourceProductId BIGINT NOT NULL DEFAULT 0,
        SourceProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        SourceSpec NVARCHAR(200) NOT NULL DEFAULT N'',
        SourceUnit NVARCHAR(20) NOT NULL DEFAULT N'',
        SourceQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceUnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        SourceDeliveryDate DATETIME2 NULL,
        SourceRemark NVARCHAR(500) NOT NULL DEFAULT N'',
        ProposedRemoved BIT NOT NULL DEFAULT 0,
        ProposedProductId BIGINT NOT NULL DEFAULT 0,
        ProposedProductName NVARCHAR(200) NOT NULL DEFAULT N'',
        ProposedSpec NVARCHAR(200) NOT NULL DEFAULT N'',
        ProposedUnit NVARCHAR(20) NOT NULL DEFAULT N'',
        ProposedQuantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedUnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedAmount DECIMAL(18,4) NOT NULL DEFAULT 0,
        ProposedDeliveryDate DATETIME2 NULL,
        ProposedRemark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SalesOrderChangeRequestDetails_Request_LineNo'
                 AND object_id = OBJECT_ID('db_owner.SalesOrderChangeRequestDetails'))
    CREATE INDEX IX_SalesOrderChangeRequestDetails_Request_LineNo
        ON db_owner.SalesOrderChangeRequestDetails(ChangeRequestId, LineNo)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_SalesOrderChangeRequestDetails_Request'
                 AND parent_object_id = OBJECT_ID('db_owner.SalesOrderChangeRequestDetails'))
    ALTER TABLE db_owner.SalesOrderChangeRequestDetails
        ADD CONSTRAINT FK_SalesOrderChangeRequestDetails_Request
        FOREIGN KEY (ChangeRequestId) REFERENCES db_owner.SalesOrderChangeRequests(Id);");

        // 34. 供应商付款引用登记（ERP-049：付款单 → 采购订单 的引用证据行）
        //     34.1 只建「付款引用行」一张表与其索引：**不含任何 UPDATE / 回填语句**，
        //          既有付款单、采购订单与供应商不因本段产生任何变化（没有引用数据时行为与历史完全一致）；
        //     34.2 有效引用行唯一：UX_SupplierPaymentAllocations_PaymentOrder
        //          （付款单 + 采购订单），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          已作废行保留可读但不占用额度（作废后可重新登记同一订单的有效引用）；
        //     34.3 付款单 / 供应商 / 采购订单只保存服务端写入的快照，**刻意不建**任何外键，也不在付款单 /
        //          采购订单上加列 —— 付款单软删除、订单软删除或取消、供应商停用或改名都不影响历史证据可读；
        //     34.4 本段只建本模块一张表与其索引，不改写付款单、采购订单、发票与发票关联、库存与库存成本、
        //          退税、费用或供应商数据，也不执行任何付款 / 记账 / 核销语句。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SupplierPaymentAllocations') IS NULL
BEGIN
    CREATE TABLE db_owner.SupplierPaymentAllocations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PaymentId BIGINT NOT NULL,
        PaymentNo NVARCHAR(50) NOT NULL DEFAULT N'',
        PaymentDate DATETIME2 NOT NULL,
        PaymentStatus INT NOT NULL DEFAULT 0,
        PaymentStatusText NVARCHAR(30) NOT NULL DEFAULT N'',
        PaymentAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        PurchaseOrderId BIGINT NOT NULL,
        OrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        OrderDate DATETIME2 NOT NULL,
        OrderStatus INT NOT NULL DEFAULT 0,
        OrderCurrency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        SupplierId BIGINT NOT NULL,
        SupplierCode NVARCHAR(50) NOT NULL DEFAULT N'',
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 1,
        AllocatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_SupplierPaymentAllocations_PaymentOrder'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentAllocations'))
    CREATE UNIQUE INDEX UX_SupplierPaymentAllocations_PaymentOrder
        ON db_owner.SupplierPaymentAllocations(PaymentId, PurchaseOrderId)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentAllocations_PaymentId_Status'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentAllocations'))
    CREATE INDEX IX_SupplierPaymentAllocations_PaymentId_Status
        ON db_owner.SupplierPaymentAllocations(PaymentId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentAllocations_PurchaseOrderId'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentAllocations'))
    CREATE INDEX IX_SupplierPaymentAllocations_PurchaseOrderId
        ON db_owner.SupplierPaymentAllocations(PurchaseOrderId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentAllocations_Status_AllocatedAt'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentAllocations'))
    CREATE INDEX IX_SupplierPaymentAllocations_Status_AllocatedAt
        ON db_owner.SupplierPaymentAllocations(Status, AllocatedAt)
        WHERE IsDeleted = 0;");

        // 35. 单证明细行快照（ERP-051：商业发票 / 装箱单 的商品明细证据行）
        //     35.1 只建「单证明细行」一张表 + 一个过滤唯一索引 + 一条外键：不含任何数据改写语句
        //          （无 UPDATE、INSERT、DELETE 数据操作），既有单证（含无明细的历史单证）不因本段产生任何变化；
        //     35.2 同一单证内未删除明细行的行序唯一：UX_TradeDocumentItems_Document_LineNo
        //          （TradeDocumentId + LineNo），过滤 IsDeleted = 0 —— 与 EF 模型同名的并发兜底；
        //     35.3 商品资料只保存**服务端写入的行快照**（编码 / 中英文名称 / 规格 / 数量 / 单位 / 单价 /
        //          行金额 / 箱数 / 净重 / 毛重 / 币种），**刻意不建**到商品资料的外键 ——
        //          商品资料被停用或软删除后历史明细行必须始终可读；
        //     35.4 明细行与单证主表建立唯一外键「明细 → 单证」（与第 30 / 31 / 33 段同一幂等口径，
        //          外键不含级联动作：单证主表只做软删除，物理删除仅由应用层按 EF 级联语义执行）；
        //     35.5 本段不改写单证台账、商品资料、销售订单、采购订单、装柜清单、库存 / 库存流水、发票、
        //          退税、费用或财务数据，也不为历史单证生成任何明细行。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.TradeDocumentItems') IS NULL
BEGIN
    CREATE TABLE db_owner.TradeDocumentItems (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        TradeDocumentId BIGINT NOT NULL,
        LineNo INT NOT NULL DEFAULT 1,
        ProductId BIGINT NOT NULL DEFAULT 0,
        ProductCode NVARCHAR(50) NOT NULL DEFAULT N'',
        ProductNameCn NVARCHAR(200) NOT NULL DEFAULT N'',
        ProductNameEn NVARCHAR(200) NOT NULL DEFAULT N'',
        Spec NVARCHAR(200) NOT NULL DEFAULT N'',
        Quantity DECIMAL(18,4) NOT NULL DEFAULT 0,
        Unit NVARCHAR(20) NOT NULL DEFAULT N'',
        UnitPrice DECIMAL(18,4) NOT NULL DEFAULT 0,
        LineAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        PackageCount INT NULL,
        NetWeight DECIMAL(18,4) NULL,
        GrossWeight DECIMAL(18,4) NULL,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'USD',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_TradeDocumentItems_Document_LineNo'
                 AND object_id = OBJECT_ID('db_owner.TradeDocumentItems'))
    CREATE UNIQUE INDEX UX_TradeDocumentItems_Document_LineNo
        ON db_owner.TradeDocumentItems(TradeDocumentId, LineNo)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_TradeDocumentItems_TradeDocument'
                 AND parent_object_id = OBJECT_ID('db_owner.TradeDocumentItems'))
    ALTER TABLE db_owner.TradeDocumentItems
        ADD CONSTRAINT FK_TradeDocumentItems_TradeDocument
        FOREIGN KEY (TradeDocumentId) REFERENCES db_owner.TradeDocuments(Id);");

        // 36. 客户收款引用登记（ERP-053：收款单 → 销售订单 的引用证据行）
        //     36.1 只建「收款引用行」一张表与其索引：**不含任何 UPDATE / 回填语句**，
        //          既有收款单、销售订单与客户不因本段产生任何变化（没有引用数据时行为与历史完全一致）；
        //     36.2 有效引用行唯一：UX_CustomerReceiptAllocations_ReceiptOrder
        //          （收款单 + 销售订单），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          已作废行保留可读但不占用额度（作废后可重新登记同一订单的有效引用）；
        //     36.3 收款单 / 客户 / 销售订单只保存服务端写入的快照，**刻意不建**任何外键，也不在收款单 /
        //          销售订单上加列 —— 收款单软删除、订单软删除或取消、客户停用或改名都不影响历史证据可读；
        //     36.4 ERP-032 / ERP-046 的权威口径里收款单只记录客户（ReferenceReceipt = FinanceReceipt.CustomerId），
        //          没有订单级持久化引用，因此本表是仓库中**唯一**的收款引用（分摊）登记模型，
        //          不建立第二套收款 → 订单链接结构；
        //     36.5 本段只建本模块一张表与其索引，不改写收款单、销售订单、客户资料、发票、库存与库存成本、
        //          装柜与单证、佣金 / 回佣、退税、费用或客户信用数据，也不执行任何收款 / 记账 / 核销语句。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.CustomerReceiptAllocations') IS NULL
BEGIN
    CREATE TABLE db_owner.CustomerReceiptAllocations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ReceiptId BIGINT NOT NULL,
        ReceiptNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ReceiptDate DATETIME2 NOT NULL,
        ReceiptStatus INT NOT NULL DEFAULT 0,
        ReceiptStatusText NVARCHAR(30) NOT NULL DEFAULT N'',
        ReceiptAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        SalesOrderId BIGINT NOT NULL,
        OrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        OrderDate DATETIME2 NOT NULL,
        OrderStatus INT NOT NULL DEFAULT 0,
        OrderCurrency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 1,
        AllocatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_CustomerReceiptAllocations_ReceiptOrder'
                 AND object_id = OBJECT_ID('db_owner.CustomerReceiptAllocations'))
    CREATE UNIQUE INDEX UX_CustomerReceiptAllocations_ReceiptOrder
        ON db_owner.CustomerReceiptAllocations(ReceiptId, SalesOrderId)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerReceiptAllocations_ReceiptId_Status'
                 AND object_id = OBJECT_ID('db_owner.CustomerReceiptAllocations'))
    CREATE INDEX IX_CustomerReceiptAllocations_ReceiptId_Status
        ON db_owner.CustomerReceiptAllocations(ReceiptId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerReceiptAllocations_SalesOrderId'
                 AND object_id = OBJECT_ID('db_owner.CustomerReceiptAllocations'))
    CREATE INDEX IX_CustomerReceiptAllocations_SalesOrderId
        ON db_owner.CustomerReceiptAllocations(SalesOrderId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerReceiptAllocations_Status_AllocatedAt'
                 AND object_id = OBJECT_ID('db_owner.CustomerReceiptAllocations'))
    CREATE INDEX IX_CustomerReceiptAllocations_Status_AllocatedAt
        ON db_owner.CustomerReceiptAllocations(Status, AllocatedAt)
        WHERE IsDeleted = 0;");

        // 37. 客户销项发票证据登记（ERP-055：普票 / 专票 / 出口发票证据台账 + 可选销售订单分摊）
        //     37.1 只建「销项发票证据」与「分摊行」两张表及其索引：**不含任何 UPDATE / INSERT / DELETE 语句**，
        //          既有客户、销售订单、收款单与单证不因本段产生任何变化（没有发票数据时行为与历史完全一致）；
        //     37.2 有效身份唯一：UX_CustomerSalesInvoiceEvidences_ActiveIdentity
        //          （客户 + 发票类型 + 规范化代码 / 号码），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          作废记录保留可读但不占用身份（作废后可重新登记同一身份）；
        //     37.3 分摊行唯一：UX_CustomerSalesInvoiceAllocations_InvoiceOrder（发票 + 销售订单），
        //          过滤 IsDeleted = 0 —— 草稿期整体替换，登记后冻结，作废后保留；
        //     37.4 客户 / 销售订单 / 单证只保存服务端写入的快照，**刻意不建任何外键**（本段不做任何既有表结构变更），
        //          也不在客户 / 销售订单 / 收款单 / 单证表上加列 —— 客户停用或删除、订单取消或软删除、
        //          单证删除都不影响历史证据可读（与 ERP-049 / ERP-053 的登记册口径一致）；
        //     37.5 本段只建本模块两张表与其索引，不改写销售订单、客户资料、收款单与引用行、发票、库存与库存成本、
        //          装柜与单证、佣金 / 回佣、退税、费用或客户信用数据，也不执行任何开票 / 报税 / 记账 / 核销语句。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences') IS NULL
BEGIN
    CREATE TABLE db_owner.CustomerSalesInvoiceEvidences (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        InvoiceType NVARCHAR(20) NOT NULL DEFAULT N'普票',
        InvoiceCode NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceNumber NVARCHAR(50) NOT NULL DEFAULT N'',
        NormalizedInvoiceCode NVARCHAR(50) NOT NULL DEFAULT N'',
        NormalizedInvoiceNumber NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceDate DATETIME2 NOT NULL,
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        NetAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        TaxAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        GrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        TradeDocumentId BIGINT NULL,
        TradeDocumentNo NVARCHAR(50) NOT NULL DEFAULT N'',
        TradeDocumentDocType NVARCHAR(30) NOT NULL DEFAULT N'',
        CommercialInvoiceReference NVARCHAR(100) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 0,
        RecordedAt DATETIME2 NULL,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF OBJECT_ID('db_owner.CustomerSalesInvoiceAllocations') IS NULL
BEGIN
    CREATE TABLE db_owner.CustomerSalesInvoiceAllocations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerSalesInvoiceEvidenceId BIGINT NOT NULL,
        SalesOrderId BIGINT NOT NULL,
        OrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        OrderDate DATETIME2 NOT NULL,
        OrderStatus INT NOT NULL DEFAULT 0,
        OrderCurrency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        SortOrder INT NOT NULL DEFAULT 0,
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_CustomerSalesInvoiceEvidences_ActiveIdentity'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences'))
    CREATE UNIQUE INDEX UX_CustomerSalesInvoiceEvidences_ActiveIdentity
        ON db_owner.CustomerSalesInvoiceEvidences(CustomerId, InvoiceType, NormalizedInvoiceCode, NormalizedInvoiceNumber)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerSalesInvoiceEvidences_CustomerId_Status'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences'))
    CREATE INDEX IX_CustomerSalesInvoiceEvidences_CustomerId_Status
        ON db_owner.CustomerSalesInvoiceEvidences(CustomerId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerSalesInvoiceEvidences_Status_InvoiceDate'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences'))
    CREATE INDEX IX_CustomerSalesInvoiceEvidences_Status_InvoiceDate
        ON db_owner.CustomerSalesInvoiceEvidences(Status, InvoiceDate)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerSalesInvoiceEvidences_NormalizedInvoiceNumber'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceEvidences'))
    CREATE INDEX IX_CustomerSalesInvoiceEvidences_NormalizedInvoiceNumber
        ON db_owner.CustomerSalesInvoiceEvidences(NormalizedInvoiceNumber)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_CustomerSalesInvoiceAllocations_InvoiceOrder'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceAllocations'))
    CREATE UNIQUE INDEX UX_CustomerSalesInvoiceAllocations_InvoiceOrder
        ON db_owner.CustomerSalesInvoiceAllocations(CustomerSalesInvoiceEvidenceId, SalesOrderId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_CustomerSalesInvoiceAllocations_SalesOrderId'
                 AND object_id = OBJECT_ID('db_owner.CustomerSalesInvoiceAllocations'))
    CREATE INDEX IX_CustomerSalesInvoiceAllocations_SalesOrderId
        ON db_owner.CustomerSalesInvoiceAllocations(SalesOrderId)
        WHERE IsDeleted = 0;");

        // 38. 装柜出运引用登记（ERP-057：显式源记录关联 + 出运证据快照 + 修订留痕）
        //     38.1 审计结论：装柜链路已有**唯一**的持久化引用关系（ContainerPreLoading.BookingId /
        //          ContainerLoadingList.PreLoadingId），订柜信息由 ERP-040 承载本套跟踪值的权威记录；
        //          因此本段只建「出运引用登记册」两张表，**不新增 / 不修改订柜、预装柜、装柜清单的任何列**
        //          （段内不做任何既有表的结构修改），也不建立第二套出运主数据；
        //     38.2 有效引用唯一：UX_ContainerShipmentReferences_ActiveSource
        //          （SourceType + SourceId），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          已作废行保留可读但不占用额度（作废后可重新登记，新旧并存可查）；
        //     38.3 源记录（订柜信息 / 预装柜单 / 装柜清单）与报关行字典项只保存**服务端写入的快照**，
        //          **刻意不建**任何外键：源记录软删除、报关行字典项停用 / 删除都不影响历史证据可读；
        //     38.4 出运方式 / 港口 / 计划时间 / 承运人 / 货代 / 拖车 / 报关行快照全部可空或默认空串
        //          （= 未知），本段不写入任何默认业务值、也不回填任何历史单据；
        //     38.5 修订留痕表只追加：UX_ContainerShipmentReferenceRevisions_Reference_Revision
        //          （引用 + 修订号）保证同一引用内修订号不重复，保存修订前的原值；
        //     38.6 本段只建本模块两张表与其索引，不含任何 UPDATE / INSERT / DELETE 语句，也不改写
        //          装柜链路、订单、库存、单证、发票、费用与结算数据。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.ContainerShipmentReferences') IS NULL
BEGIN
    CREATE TABLE db_owner.ContainerShipmentReferences (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SourceType NVARCHAR(20) NOT NULL DEFAULT N'',
        SourceId BIGINT NOT NULL,
        SourceNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SourceDate DATETIME2 NOT NULL,
        SourceStatus INT NOT NULL DEFAULT 0,
        SourceStatusText NVARCHAR(30) NOT NULL DEFAULT N'',
        ContainerNo NVARCHAR(50) NOT NULL DEFAULT N'',
        ShipmentMode NVARCHAR(10) NOT NULL DEFAULT N'',
        ShippingOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        BillOfLadingNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CarrierName NVARCHAR(200) NOT NULL DEFAULT N'',
        ForwarderName NVARCHAR(200) NOT NULL DEFAULT N'',
        DeparturePort NVARCHAR(100) NOT NULL DEFAULT N'',
        TransitPort NVARCHAR(100) NOT NULL DEFAULT N'',
        DestinationPort NVARCHAR(100) NOT NULL DEFAULT N'',
        PlannedDepartureAt DATETIME2 NULL,
        PlannedArrivalAt DATETIME2 NULL,
        TruckerName NVARCHAR(200) NOT NULL DEFAULT N'',
        CustomsBrokerId BIGINT NULL,
        CustomsBrokerName NVARCHAR(100) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 1,
        RecordedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        RevisionNo INT NOT NULL DEFAULT 1,
        LastRevisedAt DATETIME2 NULL,
        LastRevisionReason NVARCHAR(200) NOT NULL DEFAULT N'',
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF OBJECT_ID('db_owner.ContainerShipmentReferenceRevisions') IS NULL
BEGIN
    CREATE TABLE db_owner.ContainerShipmentReferenceRevisions (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ContainerShipmentReferenceId BIGINT NOT NULL,
        RevisionNo INT NOT NULL,
        SourceType NVARCHAR(20) NOT NULL DEFAULT N'',
        SourceId BIGINT NOT NULL,
        SourceNo NVARCHAR(50) NOT NULL DEFAULT N'',
        SupersededAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        Reason NVARCHAR(200) NOT NULL DEFAULT N'',
        ShipmentMode NVARCHAR(10) NOT NULL DEFAULT N'',
        ShippingOrderNo NVARCHAR(50) NOT NULL DEFAULT N'',
        BillOfLadingNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CarrierName NVARCHAR(200) NOT NULL DEFAULT N'',
        ForwarderName NVARCHAR(200) NOT NULL DEFAULT N'',
        DeparturePort NVARCHAR(100) NOT NULL DEFAULT N'',
        TransitPort NVARCHAR(100) NOT NULL DEFAULT N'',
        DestinationPort NVARCHAR(100) NOT NULL DEFAULT N'',
        PlannedDepartureAt DATETIME2 NULL,
        PlannedArrivalAt DATETIME2 NULL,
        TruckerName NVARCHAR(200) NOT NULL DEFAULT N'',
        CustomsBrokerId BIGINT NULL,
        CustomsBrokerName NVARCHAR(100) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ContainerShipmentReferences_ActiveSource'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentReferences'))
    CREATE UNIQUE INDEX UX_ContainerShipmentReferences_ActiveSource
        ON db_owner.ContainerShipmentReferences(SourceType, SourceId)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentReferences_Source_Status'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentReferences'))
    CREATE INDEX IX_ContainerShipmentReferences_Source_Status
        ON db_owner.ContainerShipmentReferences(SourceType, SourceId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentReferences_Status_RecordedAt'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentReferences'))
    CREATE INDEX IX_ContainerShipmentReferences_Status_RecordedAt
        ON db_owner.ContainerShipmentReferences(Status, RecordedAt)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentReferences_ShipmentMode'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentReferences'))
    CREATE INDEX IX_ContainerShipmentReferences_ShipmentMode
        ON db_owner.ContainerShipmentReferences(ShipmentMode)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ContainerShipmentReferenceRevisions_Reference_Revision'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentReferenceRevisions'))
    CREATE UNIQUE INDEX UX_ContainerShipmentReferenceRevisions_Reference_Revision
        ON db_owner.ContainerShipmentReferenceRevisions(ContainerShipmentReferenceId, RevisionNo)
        WHERE IsDeleted = 0;
");

        // 39. 装柜出运里程碑证据（ERP-058：挂 ERP-057 出运引用之下的只追加操作性事件留痕）
        //     39.1 审计结论：ERP-057 已建立唯一权威的出运引用登记册，本段只建**里程碑留痕表**，
        //          按显式的父出运引用 Id 关联；不做任何既有表的结构修改，也不新建出运 / 跟踪主数据；
        //     39.2 重复有效证据唯一：UX_ContainerShipmentMilestones_ActiveIdentity
        //          （父出运引用 + 事件类型 + 事件时间），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          重复登记由服务端先行拒绝，索引只作并发兜底；已作废行保留可读但不占用额度
        //          （作废后同一父记录 + 类型 + 时间可重新登记）；
        //     39.3 事件时间必填，来源说明 / 备注 / 记录人可选：未填写保持默认空串（= 未知），
        //          本段不写入任何默认业务值、也不回填任何历史单据；
        //     39.4 刻意不建任何外键：父出运引用被软删除 / 作废后历史里程碑必须始终可读，
        //          只是由服务端显式标注不可用，绝不改派到别的出运引用；
        //     39.5 本段只建本模块一张表与其索引，不含任何 UPDATE / INSERT / DELETE 语句，也不改写
        //          父出运引用、装柜链路、订单、库存、单证、发票、费用与结算数据。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.ContainerShipmentMilestones') IS NULL
BEGIN
    CREATE TABLE db_owner.ContainerShipmentMilestones (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ContainerShipmentReferenceId BIGINT NOT NULL,
        EventType NVARCHAR(20) NOT NULL DEFAULT N'',
        EventAt DATETIME2 NOT NULL,
        SourceDescription NVARCHAR(200) NOT NULL DEFAULT N'',
        Notes NVARCHAR(500) NOT NULL DEFAULT N'',
        RecordedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        RecordedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        Status INT NOT NULL DEFAULT 1,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ContainerShipmentMilestones_ActiveIdentity'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentMilestones'))
    CREATE UNIQUE INDEX UX_ContainerShipmentMilestones_ActiveIdentity
        ON db_owner.ContainerShipmentMilestones(ContainerShipmentReferenceId, EventType, EventAt)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentMilestones_Parent_Status_EventAt'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentMilestones'))
    CREATE INDEX IX_ContainerShipmentMilestones_Parent_Status_EventAt
        ON db_owner.ContainerShipmentMilestones(ContainerShipmentReferenceId, Status, EventAt)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentMilestones_EventType_EventAt'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentMilestones'))
    CREATE INDEX IX_ContainerShipmentMilestones_EventType_EventAt
        ON db_owner.ContainerShipmentMilestones(EventType, EventAt)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ContainerShipmentMilestones_Status_RecordedAt'
                 AND object_id = OBJECT_ID('db_owner.ContainerShipmentMilestones'))
    CREATE INDEX IX_ContainerShipmentMilestones_Status_RecordedAt
        ON db_owner.ContainerShipmentMilestones(Status, RecordedAt)
        WHERE IsDeleted = 0;
");

        // 40. 业务单据附件内容证据（ERP-061：在既有 OSS 客户端与 ERP-045 元数据引用册之外，建立唯一的附件内容册）
        //     40.1 审计结论：本仓库没有二进制附件子系统（既有文件存储代码只有 OssStorageService，
        //          仅被商品图片上传使用；ERP-045 只登记引用元数据）。本段只建**附件内容证据表**，
        //          并按显式归属（单据类型 + Id）关联，**不**在销售订单 / 采购订单表上加列或建外键；
        //     40.2 **刻意不建唯一索引**：同一摘要的重复上传不做内容寻址去重，两次上传是两条独立证据，
        //          绝不静默合并、覆盖或替换（Sha256 上的普通索引只用于人工核对重复上传）；
        //     40.3 只保存服务端权威元数据与**服务端生成**的不透明存储键（内容本体不在数据库）；
        //          本段不写入任何默认业务值、不回填历史单据，也不迁移任何既有字段；
        //     40.4 本段只建本模块一张表与其索引，不含任何 UPDATE / INSERT / DELETE 语句，也不改写
        //          订单、库存、出库、装柜、单证、发票、费用与结算等任何既有表；
        //     40.5 生产库执行仍由 Human Gate 控制：本段只在应用启动时以 IF OBJECT_ID(...) IS NULL 幂等补齐，
        //          自动化验证只作用于本机非生产测试库。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.AttachmentEvidences') IS NULL
BEGIN
    CREATE TABLE db_owner.AttachmentEvidences (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        OwnerType NVARCHAR(30) NOT NULL DEFAULT N'',
        OwnerId BIGINT NOT NULL,
        OwnerNo NVARCHAR(50) NOT NULL DEFAULT N'',
        OwnerTypeText NVARCHAR(30) NOT NULL DEFAULT N'',
        OriginalFileName NVARCHAR(255) NOT NULL DEFAULT N'',
        MediaType NVARCHAR(120) NOT NULL DEFAULT N'',
        SizeBytes BIGINT NOT NULL DEFAULT 0,
        Sha256 NVARCHAR(64) NOT NULL DEFAULT N'',
        Description NVARCHAR(500) NOT NULL DEFAULT N'',
        StorageKey NVARCHAR(200) NOT NULL DEFAULT N'',
        StorageProvider NVARCHAR(30) NOT NULL DEFAULT N'',
        UploadedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        RecordedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        Status INT NOT NULL DEFAULT 0,
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AttachmentEvidences_Owner_Status_RecordedAt'
                 AND object_id = OBJECT_ID('db_owner.AttachmentEvidences'))
    CREATE INDEX IX_AttachmentEvidences_Owner_Status_RecordedAt
        ON db_owner.AttachmentEvidences(OwnerType, OwnerId, Status, RecordedAt)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AttachmentEvidences_Sha256'
                 AND object_id = OBJECT_ID('db_owner.AttachmentEvidences'))
    CREATE INDEX IX_AttachmentEvidences_Sha256
        ON db_owner.AttachmentEvidences(Sha256)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AttachmentEvidences_Status_RecordedAt'
                 AND object_id = OBJECT_ID('db_owner.AttachmentEvidences'))
    CREATE INDEX IX_AttachmentEvidences_Status_RecordedAt
        ON db_owner.AttachmentEvidences(Status, RecordedAt)
        WHERE IsDeleted = 0;
");

        // 41. 供应商付款 → 采购发票 引用登记（ERP-066：付款单 → 已登记采购发票 的引用证据行）
        //     41.1 只建「付款发票引用行」一张表与其索引：**不含任何 UPDATE / INSERT / DELETE 语句**，
        //          既有付款单、供应商采购发票、采购订单与供应商不因本段产生任何变化
        //          （没有引用数据时行为与历史完全一致）；
        //     41.2 有效引用行唯一：UX_SupplierPaymentInvoiceAllocations_PaymentInvoice
        //          （付款单 + 采购发票），过滤 IsDeleted = 0 AND Status <> 2 ——
        //          已作废行保留可读但不占用额度（作废后可重新登记同一发票的有效引用）；
        //     41.3 付款单 / 供应商 / 发票只保存服务端写入的快照（含发票身份 / 含税总额与登记人），
        //          **刻意不建**任何外键，也不在付款单、发票与采购订单上加列 ——
        //          付款单或发票软删除、供应商停用或改名都不影响历史证据可读；
        //     41.4 本段只建本模块一张表与其索引，不改写付款单、发票与发票关联（ERP-043 / ERP-065）、
        //          ERP-049 的采购订单引用行、采购订单、库存与库存成本、退税、费用或供应商数据，
        //          也不执行任何付款 / 记账 / 核销 / 认证语句；
        //     41.5 生产库执行仍由 Human Gate 控制：本段只在应用启动时以 IF OBJECT_ID(...) IS NULL 幂等补齐，
        //          自动化验证只作用于本机非生产测试库。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations') IS NULL
BEGIN
    CREATE TABLE db_owner.SupplierPaymentInvoiceAllocations (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PaymentId BIGINT NOT NULL,
        PaymentNo NVARCHAR(50) NOT NULL DEFAULT N'',
        PaymentDate DATETIME2 NOT NULL,
        PaymentStatus INT NOT NULL DEFAULT 0,
        PaymentStatusText NVARCHAR(30) NOT NULL DEFAULT N'',
        PaymentAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        PurchaseInvoiceId BIGINT NOT NULL,
        InvoiceType NVARCHAR(20) NOT NULL DEFAULT N'',
        InvoiceCode NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceNumber NVARCHAR(50) NOT NULL DEFAULT N'',
        InvoiceIdentityText NVARCHAR(120) NOT NULL DEFAULT N'',
        InvoiceDate DATETIME2 NOT NULL,
        InvoiceStatus INT NOT NULL DEFAULT 1,
        InvoiceStatusText NVARCHAR(30) NOT NULL DEFAULT N'',
        InvoiceGrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        SupplierId BIGINT NOT NULL,
        SupplierCode NVARCHAR(50) NOT NULL DEFAULT N'',
        SupplierName NVARCHAR(200) NOT NULL DEFAULT N'',
        AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 1,
        AllocatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        RecordedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_SupplierPaymentInvoiceAllocations_PaymentInvoice'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations'))
    CREATE UNIQUE INDEX UX_SupplierPaymentInvoiceAllocations_PaymentInvoice
        ON db_owner.SupplierPaymentInvoiceAllocations(PaymentId, PurchaseInvoiceId)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentInvoiceAllocations_PaymentId_Status'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations'))
    CREATE INDEX IX_SupplierPaymentInvoiceAllocations_PaymentId_Status
        ON db_owner.SupplierPaymentInvoiceAllocations(PaymentId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentInvoiceAllocations_PurchaseInvoiceId'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations'))
    CREATE INDEX IX_SupplierPaymentInvoiceAllocations_PurchaseInvoiceId
        ON db_owner.SupplierPaymentInvoiceAllocations(PurchaseInvoiceId)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SupplierPaymentInvoiceAllocations_Status_AllocatedAt'
                 AND object_id = OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations'))
    CREATE INDEX IX_SupplierPaymentInvoiceAllocations_Status_AllocatedAt
        ON db_owner.SupplierPaymentInvoiceAllocations(Status, AllocatedAt)
        WHERE IsDeleted = 0;
");

        // 42. 代理服务费协议证据登记（ERP-069：客户代理服务费的仓库内商业条款证据）
        //     42.1 **新增前的既有模型审计**（见 docs/代理服务费协议证据说明.md §2）：本仓库此前没有「代理服务费协议」
        //          权威模型 —— BaseCustomers.CommissionRatio（客户佣金 / 回佣比例）、BaseSuppliers.RebateRatio（供应商
        //          返点比例）、销售订单上的 CommissionRatio 列（订单佣金比例快照）都只是主数据 / 单据上的**比例设置**，
        //          `业务员提成表`（/api/reports/sales-commission，比例取自系统参数 SalesCommissionRate、按毛利计算）
        //          是**内部业务员提成报表**；三者与「客户代理服务费协议」的计费主体 / 对象 / 依据都不同，
        //          因此本段建的是**独立的协议证据表**：既不替换、也不复制、也不派生上述任一模型；
        //     42.2 只建「协议证据」一张表与其过滤索引：**不含任何 UPDATE / INSERT / DELETE 回填语句**，
        //          也不在任何既有表（客户 / 销售订单 / 参数 / 发票 / 收款 / 库存 / 费用 / 退税）上加列；
        //     42.3 有效身份唯一：UX_AgencyServiceFeeAgreements_ActiveIdentity（CustomerId, NormalizedAgreementNo，
        //          过滤 IsDeleted = 0 AND Status <> 2）= 同一「客户 + 规范化协议号」在**未作废**记录内唯一
        //          （草稿同样占用身份，避免同一协议重复登记；作废记录保留可读但不占用身份）；
        //     42.4 费用条款列只保存**用户显式提交**并已按口径取整的值：费率 DECIMAL(9,4)、固定金额 DECIMAL(18,2)；
        //          本段不写入任何默认业务值，也**不**从业务员提成设置（SalesCommissionRate）、客户 / 供应商主数据比例、
        //          历史订单或自由文本派生任何条款；
        //     42.5 本表**刻意不建**到客户的外键与导航属性：客户停用 / 软删除 / 改名都不影响历史证据可读；
        //     42.6 生产库执行仍由 Human Gate 控制：本段只在应用启动时以 IF OBJECT_ID(...) IS NULL 幂等补齐，
        //          自动化验证只作用于本机非生产测试库。
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('db_owner.AgencyServiceFeeAgreements') IS NULL
BEGIN
    CREATE TABLE db_owner.AgencyServiceFeeAgreements (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AgreementNo NVARCHAR(50) NOT NULL DEFAULT N'',
        NormalizedAgreementNo NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerId BIGINT NOT NULL,
        CustomerCode NVARCHAR(50) NOT NULL DEFAULT N'',
        CustomerName NVARCHAR(200) NOT NULL DEFAULT N'',
        EffectiveFrom DATETIME2 NOT NULL,
        EffectiveTo DATETIME2 NULL,
        Currency NVARCHAR(20) NOT NULL DEFAULT N'CNY',
        FeeMethod NVARCHAR(20) NOT NULL DEFAULT N'',
        RatePercent DECIMAL(9,4) NOT NULL DEFAULT 0,
        FixedAmount DECIMAL(18,2) NOT NULL DEFAULT 0,
        FeeBasis NVARCHAR(200) NOT NULL DEFAULT N'',
        Status INT NOT NULL DEFAULT 0,
        RecordedAt DATETIME2 NULL,
        RecordedBy NVARCHAR(100) NOT NULL DEFAULT N'',
        VoidedAt DATETIME2 NULL,
        VoidReason NVARCHAR(500) NOT NULL DEFAULT N'',
        Remark NVARCHAR(500) NOT NULL DEFAULT N'',
        CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
        CreatedBy BIGINT NULL,
        UpdatedAt DATETIME2 NULL,
        UpdatedBy BIGINT NULL,
        IsDeleted BIT NOT NULL DEFAULT 0,
        RowVersion ROWVERSION NOT NULL
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_AgencyServiceFeeAgreements_ActiveIdentity'
                 AND object_id = OBJECT_ID('db_owner.AgencyServiceFeeAgreements'))
    CREATE UNIQUE INDEX UX_AgencyServiceFeeAgreements_ActiveIdentity
        ON db_owner.AgencyServiceFeeAgreements(CustomerId, NormalizedAgreementNo)
        WHERE IsDeleted = 0 AND Status <> 2;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AgencyServiceFeeAgreements_CustomerId_Status'
                 AND object_id = OBJECT_ID('db_owner.AgencyServiceFeeAgreements'))
    CREATE INDEX IX_AgencyServiceFeeAgreements_CustomerId_Status
        ON db_owner.AgencyServiceFeeAgreements(CustomerId, Status)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AgencyServiceFeeAgreements_Status_EffectiveFrom'
                 AND object_id = OBJECT_ID('db_owner.AgencyServiceFeeAgreements'))
    CREATE INDEX IX_AgencyServiceFeeAgreements_Status_EffectiveFrom
        ON db_owner.AgencyServiceFeeAgreements(Status, EffectiveFrom)
        WHERE IsDeleted = 0;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_AgencyServiceFeeAgreements_NormalizedAgreementNo'
                 AND object_id = OBJECT_ID('db_owner.AgencyServiceFeeAgreements'))
    CREATE INDEX IX_AgencyServiceFeeAgreements_NormalizedAgreementNo
        ON db_owner.AgencyServiceFeeAgreements(NormalizedAgreementNo)
        WHERE IsDeleted = 0;
");

    }
}
