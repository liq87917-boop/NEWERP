using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 打印模板（打印设计结果）：按单据类型保存打印抬头、纸张、字号、打印字段顺序等配置
/// </summary>
public class SysPrintTemplate : BaseEntity
{
    /// <summary>单据类型（菜单编码，如 sales-order / customer）</summary>
    [Required, MaxLength(50)]
    public string BillType { get; set; } = string.Empty;

    /// <summary>模板名称</summary>
    [Required, MaxLength(100)]
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>打印标题（为空时使用单据中文名称）</summary>
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>公司抬头（打印页顶部公司名称）</summary>
    [MaxLength(200)]
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>公司地址（可选，打印在页眉）</summary>
    [MaxLength(300)]
    public string CompanyAddress { get; set; } = string.Empty;

    /// <summary>公司联系电话（可选，打印在页眉）</summary>
    [MaxLength(100)]
    public string CompanyPhone { get; set; } = string.Empty;

    /// <summary>是否显示公司抬头</summary>
    public bool ShowCompanyHeader { get; set; } = true;

    /// <summary>是否打印明细（副表）</summary>
    public bool ShowDetailTable { get; set; } = true;

    /// <summary>是否打印备注</summary>
    public bool ShowRemark { get; set; } = true;

    /// <summary>纸张规格（A4 / A5 / A4-L 横向 / 80mm 小票）</summary>
    [MaxLength(20)]
    public string PaperSize { get; set; } = "A4";

    /// <summary>正文字号（px）</summary>
    public int FontSize { get; set; } = 12;

    /// <summary>打印字段顺序（JSON 数组，元素为字段键，如 ["BillNo","OrderDate"]）</summary>
    [MaxLength(2000)]
    public string FieldKeys { get; set; } = string.Empty;

    /// <summary>页脚文本（如签字栏、联系电话）</summary>
    [MaxLength(500)]
    public string FooterText { get; set; } = string.Empty;

    /// <summary>是否为该单据类型的默认模板</summary>
    public bool IsDefault { get; set; }

    // ============ 外观样式（可视化设计器可调） ============

    /// <summary>正文字体族（如 Microsoft YaHei / SimSun / SimHei / KaiTi）</summary>
    [MaxLength(50)]
    public string FontFamily { get; set; } = "Microsoft YaHei";

    /// <summary>单据标题字号（px）</summary>
    public int TitleFontSize { get; set; } = 16;

    /// <summary>单据标题颜色（HEX，如 #1e3a8a）</summary>
    [MaxLength(20)]
    public string TitleColor { get; set; } = "#000000";

    /// <summary>标题对齐方式（left / center / right）</summary>
    [MaxLength(10)]
    public string TitleAlign { get; set; } = "center";

    /// <summary>公司名称字号（px）</summary>
    public int CompanyFontSize { get; set; } = 18;

    /// <summary>公司名称颜色（HEX）</summary>
    [MaxLength(20)]
    public string CompanyColor { get; set; } = "#000000";

    /// <summary>正文文字颜色（HEX）</summary>
    [MaxLength(20)]
    public string TextColor { get; set; } = "#000000";

    /// <summary>表头 / 字段标签背景色（HEX）</summary>
    [MaxLength(20)]
    public string HeaderBgColor { get; set; } = "#f2f2f2";

    /// <summary>边框颜色（HEX）</summary>
    [MaxLength(20)]
    public string BorderColor { get; set; } = "#999999";

    /// <summary>边框样式（solid 实线 / dashed 虚线 / none 无边框）</summary>
    [MaxLength(20)]
    public string BorderStyle { get; set; } = "solid";

    /// <summary>数据行高（px，0 表示按内容自适应）</summary>
    public int RowHeight { get; set; }

    /// <summary>单元格内边距（px）</summary>
    public int CellPadding { get; set; } = 6;

    /// <summary>
    /// 网格布局（Excel 式设计器数据，JSON）：
    /// { "cols":[120,...], "rows":[34,...], "cells":{"0_0":{"v":"公司：{CompanyName}","bold":true,"align":"left","fs":12,"color":"#000","bg":"","bd":1}} }
    /// 为空时表示该单据仍使用「表单式」打印模板
    /// </summary>
    public string? LayoutJson { get; set; }
}
