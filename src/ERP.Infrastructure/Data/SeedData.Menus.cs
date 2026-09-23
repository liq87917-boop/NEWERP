using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 种子数据：菜单树初始化（8 大模块及二级菜单）
/// </summary>
public static partial class SeedData
{
    private static readonly (string Parent, string Code, string Name, string Path, string Icon, MenuType Type)[] MenuDefs =
    {
        ("", "system", "系统设置", "/system", "Settings", MenuType.Directory),
        ("system", "user", "用户管理", "/system/user", "Users", MenuType.Menu),
        ("system", "role", "角色管理", "/system/role", "ShieldCheck", MenuType.Menu),
        ("system", "user-permission", "用户权限", "/system/user-permission", "KeyRound", MenuType.Menu),
        ("system", "sys-parameter", "系统参数", "/system/parameter", "SlidersHorizontal", MenuType.Menu),
        ("system", "user-parameter", "用户参数", "/system/user-parameter", "UserCog", MenuType.Menu),
        ("system", "doc-rule", "单据号规则", "/system/doc-rule", "Hash", MenuType.Menu),
        ("system", "client-limit", "客户端限制", "/system/client-limit", "LockKeyhole", MenuType.Menu),
        ("system", "sys-log", "系统日志", "/system/log", "ScrollText", MenuType.Menu),

        ("", "base", "基础资料", "/base", "Database", MenuType.Directory),
        ("base", "customer", "客户资料", "/base/customer", "Building2", MenuType.Menu),
        ("base", "supplier", "供应商资料", "/base/supplier", "Factory", MenuType.Menu),
        ("base", "employee", "员工资料", "/base/employee", "UserRound", MenuType.Menu),
        ("base", "expense-account", "费用科目", "/base/expense-account", "Receipt", MenuType.Menu),
        ("base", "warehouse", "仓库资料", "/base/warehouse", "Warehouse", MenuType.Menu),
        ("base", "product", "商品资料", "/base/product", "Package", MenuType.Menu),
        ("base", "other-info", "其他资料", "/base/other-info", "Layers", MenuType.Menu),

        ("", "inquiry", "询价管理", "/inquiry", "FileSearch", MenuType.Directory),
        ("inquiry", "inquiry-new", "新建询价单", "/inquiry/new", "FilePlus2", MenuType.Menu),
        ("inquiry", "inquiry-export", "询价单导出", "/inquiry/export", "FileDown", MenuType.Menu),

        ("", "order", "订单管理", "/order", "ShoppingCart", MenuType.Directory),
        ("order", "sales-order", "销售订单", "/order/sales", "ClipboardList", MenuType.Menu),
        ("order", "sales-order-export", "销售订单导出", "/order/sales-export", "FileDown", MenuType.Menu),
        ("order", "purchase-order", "采购订单", "/order/purchase", "ClipboardList", MenuType.Menu),
        ("order", "purchase-order-export", "采购订单导出", "/order/purchase-export", "FileDown", MenuType.Menu),

        ("", "logistics", "物流管理", "/logistics", "Truck", MenuType.Directory),
        ("logistics", "stock-in", "采购入库", "/logistics/stock-in", "PackagePlus", MenuType.Menu),
        ("logistics", "stock-out", "销售出库", "/logistics/stock-out", "PackageMinus", MenuType.Menu),
        ("logistics", "stock-query", "库存查询", "/logistics/stock", "Boxes", MenuType.Menu),

        ("", "container", "装柜管理", "/container", "Container", MenuType.Directory),
        ("container", "receiving-plan", "收货计划", "/container/receiving-plan", "CalendarClock", MenuType.Menu),
        ("container", "booking", "订柜信息", "/container/booking", "Anchor", MenuType.Menu),
        ("container", "pre-loading", "预装柜单", "/container/pre-loading", "Container", MenuType.Menu),
        ("container", "loading-list", "装柜清单", "/container/loading-list", "ClipboardCheck", MenuType.Menu),

        ("", "finance", "账务管理", "/finance", "Landmark", MenuType.Directory),
        ("finance", "deposit-apply", "定金申请单", "/finance/deposit-apply", "FileText", MenuType.Menu),
        ("finance", "payment-apply", "货款申请单", "/finance/payment-apply", "FileText", MenuType.Menu),
        ("finance", "payment", "付款单", "/finance/payment", "Banknote", MenuType.Menu),
        ("finance", "container-settlement", "装柜结算单", "/finance/container-settlement", "Ship", MenuType.Menu),
        ("finance", "bulk-settlement", "散货结算单", "/finance/bulk-settlement", "Box", MenuType.Menu),
        ("finance", "receipt", "收款单", "/finance/receipt", "Wallet", MenuType.Menu),
        ("finance", "complaint", "客诉单", "/finance/complaint", "TriangleAlert", MenuType.Menu),

        ("", "report", "报表管理", "/report", "BarChart3", MenuType.Directory),
        ("report", "product-sales-ranking", "商品销量排名榜", "/report/product-sales-ranking", "TrendingUp", MenuType.Menu),
        ("report", "order-profit", "订单利润暂估表", "/report/order-profit", "ChartLine", MenuType.Menu),
        ("report", "customer-shipment", "客户出货量统计表", "/report/customer-shipment", "PieChart", MenuType.Menu),
        ("report", "salesman-output", "业务员产值报表", "/report/salesman-output", "UserRound", MenuType.Menu),
        ("report", "balance-sheet", "资产负债表", "/report/balance-sheet", "Scale", MenuType.Menu),
        ("report", "income-statement", "利润表", "/report/income-statement", "ChartLine", MenuType.Menu),
        ("report", "cash-flow", "现金流量表", "/report/cash-flow", "Waves", MenuType.Menu)
    };

    /// <summary>初始化菜单树</summary>
    private static async Task SeedMenusAsync(ErpDbContext db)
    {
        var idMap = new Dictionary<string, long>();
        foreach (var def in MenuDefs)
        {
            if (await db.SysMenus.AnyAsync(m => m.MenuCode == def.Code))
                continue;

            var parentId = 0L;
            if (!string.IsNullOrEmpty(def.Parent))
                idMap.TryGetValue(def.Parent, out parentId);

            var menu = new SysMenu
            {
                ParentId = parentId,
                MenuCode = def.Code,
                MenuName = def.Name,
                Path = def.Path,
                Icon = def.Icon,
                SortOrder = 0,
                MenuType = def.Type,
                CreatedAt = DateTime.Now
            };
            db.SysMenus.Add(menu);
            // 立即保存以获取自增主键，供后续子菜单关联父级 Id
            await db.SaveChangesAsync();
            idMap[def.Code] = menu.Id;
        }
    }
}
