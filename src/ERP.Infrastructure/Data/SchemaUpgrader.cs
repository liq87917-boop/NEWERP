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

    }
}
