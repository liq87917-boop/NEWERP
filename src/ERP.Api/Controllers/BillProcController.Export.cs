using ERP.Application.Common;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用单据控制器：Excel 导出（按单据类型、状态、日期范围筛选）
/// </summary>
public partial class BillProcController
{
    /// <summary>各单据类型默认导出列（key: 字段名, title: 中文标题）；未配置时导出全部字段</summary>
    private static readonly Dictionary<string, List<(string Key, string Title)>> ExportColumns = new()
    {
        ["sales-order"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("OrderDate", "订单日期"), ("CustId", "客户Id"),
            ("EmpId", "业务员Id"), ("Currency", "币种"), ("ExchangeRate", "汇率"),
            ("TotalAmount", "总金额"), ("DepositAmount", "定金"), ("DepositRatio", "定金比例%"),
            ("DeliveryDate", "交货日期"), ("Status", "状态"), ("Remark", "备注")
        },
        ["purchase-order"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("OrderDate", "订单日期"), ("SupplierId", "供应商Id"),
            ("EmpId", "采购员Id"), ("Currency", "币种"), ("ExchangeRate", "汇率"),
            ("TotalAmount", "总金额"), ("PaymentTerms", "付款条件"), ("DeliveryDate", "交货日期"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["inquiry"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("InquiryDate", "询价日期"), ("CustomerId", "客户Id"),
            ("ContactPerson", "联系人"), ("ContactPhone", "联系电话"), ("EmpId", "业务员Id"),
            ("Currency", "币种"), ("ExchangeRate", "汇率"), ("ValidDays", "有效期"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["stock-in"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("StockInDate", "入库日期"), ("SupplierId", "供应商Id"),
            ("WarehouseId", "仓库Id"), ("PurchaseOrderId", "采购订单Id"),
            ("TotalQuantity", "总数量"), ("TotalWeight", "总毛重"), ("TotalVolume", "总体积"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["stock-out"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("StockOutDate", "出库日期"), ("CustomerId", "客户Id"),
            ("WarehouseId", "仓库Id"), ("SalesOrderId", "销售订单Id"),
            ("TotalQuantity", "总数量"), ("TotalWeight", "总毛重"), ("TotalVolume", "总体积"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["receipt"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("ReceiptDate", "收款日期"), ("CustomerId", "客户Id"),
            ("Amount", "金额"), ("Currency", "币种"), ("PaymentMethod", "收款方式"),
            ("BankAccount", "银行账户"), ("Status", "状态"), ("Remark", "备注")
        },
        ["payment"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("PaymentDate", "付款日期"), ("SupplierId", "供应商Id"),
            ("PaymentApplyId", "申请单Id"), ("Amount", "金额"), ("Currency", "币种"),
            ("PaymentMethod", "付款方式"), ("BankAccount", "银行账户"), ("Status", "状态"), ("Remark", "备注")
        },
        ["deposit-apply"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("ApplyDate", "申请日期"), ("CustomerId", "客户Id"),
            ("SalesOrderId", "销售订单Id"), ("Amount", "金额"), ("Currency", "币种"),
            ("ExchangeRate", "汇率"), ("BankAccount", "银行账户"), ("Payee", "收款方"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["payment-apply"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("ApplyDate", "申请日期"), ("CustomerId", "客户Id"),
            ("SalesOrderId", "销售订单Id"), ("Amount", "金额"), ("Currency", "币种"),
            ("ExchangeRate", "汇率"), ("BankAccount", "银行账户"), ("Payee", "收款方"),
            ("Status", "状态"), ("Remark", "备注")
        },
        ["container-settlement"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("SettlementDate", "结算日期"), ("CustomerId", "客户Id"),
            ("LoadingListId", "装柜清单Id"), ("TotalAmount", "结算总金额"),
            ("FreightCost", "海运费"), ("OtherCost", "其他费用"), ("Status", "状态"), ("Remark", "备注")
        },
        ["bulk-settlement"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("SettlementDate", "结算日期"), ("CustomerId", "客户Id"),
            ("TotalAmount", "结算总金额"), ("FreightCost", "海运费"), ("Status", "状态"), ("Remark", "备注")
        },
        ["complaint"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("ComplaintDate", "客诉日期"), ("CustomerId", "客户Id"),
            ("SalesOrderId", "销售订单Id"), ("ComplaintType", "客诉类型"),
            ("ResponsibleDept", "责任部门"), ("Status", "状态"), ("Remark", "备注")
        },
        ["receiving-plan"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("PlanDate", "计划日期"), ("SupplierId", "供应商Id"),
            ("BookingNo", "订柜单号"), ("ContainerType", "柜型"), ("ContainerNo", "柜号"),
            ("ExpectedArrivalDate", "预计到货"), ("Destination", "目的地"),
            ("TotalQuantity", "总件数"), ("Status", "状态"), ("Remark", "备注")
        },
        ["booking"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("BookingDate", "订柜日期"), ("CustomerId", "客户Id"),
            ("SupplierId", "供应商Id"), ("ContainerType", "柜型"), ("ShippingCompany", "船公司"),
            ("VoyageNo", "航次"), ("SailingDate", "开船日期"), ("DeparturePort", "起运港"),
            ("DestinationPort", "目的港"), ("Status", "状态"), ("Remark", "备注")
        },
        ["pre-loading"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("LoadingDate", "装柜日期"), ("BookingId", "订柜Id"),
            ("ContainerNo", "柜号"), ("SealNo", "封条号"), ("TotalCartons", "总箱数"),
            ("TotalWeight", "总毛重"), ("TotalVolume", "总体积"), ("Status", "状态"), ("Remark", "备注")
        },
        ["loading-list"] = new List<(string, string)>
        {
            ("BillNo", "单据号"), ("LoadingDate", "装柜日期"), ("PreLoadingId", "预装柜单Id"),
            ("ContainerNo", "柜号"), ("CustomerId", "客户Id"), ("ShippingMark", "唛头"),
            ("TotalCartons", "总箱数"), ("TotalWeight", "总毛重"), ("TotalVolume", "总体积"),
            ("Status", "状态"), ("Remark", "备注")
        },
    };


    /// <summary>
    /// 导出单据为 Excel 文件（按单据号关键字、状态、日期范围筛选）
    /// </summary>
    [HttpGet("{billType}/export")]
    public async Task<IActionResult> Export(
        string billType,
        [FromQuery] string? keyword,
        [FromQuery] int? status,
        [FromQuery] string? dateField,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end)
    {
        if (!Bills.TryGetValue(billType, out var meta))
            return Ok(ApiResponse<object>.Fail("未知单据类型", ErrorCodes.InvalidParameter));

        var where = "1=1";
        if (!string.IsNullOrWhiteSpace(keyword)) where += " AND BillNo LIKE @kw";
        if (status.HasValue) where += " AND Status = @st";
        if (!string.IsNullOrWhiteSpace(dateField))
        {
            if (start.HasValue) where += $" AND {dateField} >= @start";
            if (end.HasValue) where += $" AND {dateField} <= @end";
        }

        var sql = $"SELECT * FROM db_owner.{meta.Table} WHERE {where} ORDER BY Oid DESC";
        using var conn = new SqlConnection(_sp.GetConnectionString());
        await conn.OpenAsync();
        using var cmd = new SqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(keyword)) cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
        if (status.HasValue) cmd.Parameters.AddWithValue("@st", status.Value);
        if (!string.IsNullOrWhiteSpace(dateField))
        {
            if (start.HasValue) cmd.Parameters.AddWithValue("@start", start.Value);
            if (end.HasValue) cmd.Parameters.AddWithValue("@end", end.Value);
        }

        var rows = new List<Dictionary<string, object?>>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
        }

        var columns = ExportColumns.TryGetValue(billType, out var cols) ? cols : null;
        var bytes = ExcelExporter.ExportRows(meta.Table, rows, columns);
        var fileName = $"{meta.Table}_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}

