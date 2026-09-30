'use strict';
/* ERP-168 前端 UI 逻辑单测：有效 as-of 日期回显 + 显式日期筛选在预览 / 导出请求中的保留。
   运行：node tests/automation/dynamic_supplier_aging_asof_ui.test.js
   失败时抛异常并以非零码退出。 */

const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const JS_PATH = path.join(ROOT, 'src', 'ERP.Api', 'wwwroot', 'js', 'supplier-reconciliation-aging.js');

let passed = 0;
function ok(cond, msg) { assert.ok(cond, msg); passed += 1; }
function eq(actual, expected, msg) { assert.deepStrictEqual(actual, expected, msg); passed += 1; }

const mod = require(JS_PATH);

const catalogFields = [
  { key: 'invoiceId', label: '发票Id', dataType: 'number', filterable: false },
  { key: 'supplierName', label: '供应商名称', dataType: 'text', filterable: false },
  { key: 'currency', label: '币种', dataType: 'text', filterable: true },
  { key: 'grossAmount', label: '含税总额', dataType: 'number', filterable: false },
  { key: 'dueDate', label: '显式到期日', dataType: 'date', filterable: true },
];

// ---- 1. 显式 as-of 日期在预览 / 导出请求中保留 ----
const explicit = mod.sraDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName', 'currency'],
  asOfDate: '2026-09-24T00:00:00',
  page: 1,
  pageSize: 20,
  maxPageSize: 200,
});
eq(explicit.asOfDate, '2026-09-24', '显式 as-of 日期保留为请求体（预览 / 导出复用同一请求组装）');

// ---- 2. 省略 as-of 时不携带多余键（后端按当天默认解析） ----
const omitted = mod.sraDesBuildRequest({
  catalogFields,
  selectedKeys: ['supplierName'],
  page: 1,
  pageSize: 20,
  maxPageSize: 200,
});
ok(!('asOfDate' in omitted), '省略 as-of 时不携带 asOfDate 键（交给后端按当天默认解析）');

// ---- 3. 设计器结果回显后端返回的有效 as-of 日期 ----
const withText = mod.sraDesResultHtml({
  columns: [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }],
  rows: [{ supplierName: '甲供应商' }],
  total: 1, page: 1, pageSize: 50, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
  asOfDateText: '账龄基准日（as-of）：2026-09-24',
});
ok(withText.includes('账龄基准日（as-of）：2026-09-24'), '回显后端返回的有效 as-of 日期文案');
ok(withText.includes('共 1 条'), '当前页证据汇总仍可见');

// ---- 4. 仅有原始日期值（无文案）时按日期回显 ----
const withRaw = mod.sraDesResultHtml({
  columns: [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }],
  rows: [{ supplierName: '甲供应商' }],
  total: 1, page: 1, pageSize: 50, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
  asOfDate: '2026-09-24T00:00:00',
});
ok(withRaw.includes('账龄基准日（as-of）：2026-09-24'), '仅有日期值时按日期回显');

// ---- 5. 缺省日期时不渲染 as-of 行 ----
const noDate = mod.sraDesResultHtml({
  columns: [{ key: 'supplierName', label: '供应商名称', dataType: 'text' }],
  rows: [{ supplierName: '甲供应商' }],
  total: 1, page: 1, pageSize: 50, totalPages: 1,
  readOnlyText: '只读', boundaryText: '边界', disclaimerText: '免责',
});
ok(!noDate.includes('账龄基准日'), '缺少 as-of 时回退不渲染日期行');

// ---- 6. 前端接线契约：设计器接入 as-of 回显，不执行任意 SQL ----
const jsSrc = fs.readFileSync(JS_PATH, 'utf8');
ok(jsSrc.includes('function sraDesResultHtml('), '注册结果渲染函数');
ok(jsSrc.includes('asOfDateText') && jsSrc.includes('asOfDate'), '结果渲染读取后端回传的有效 as-of 日期');
ok(!/FromSql|ExecuteSql|SqlCommand|SELECT\s+\*/i.test(jsSrc), '不执行任意 SQL');

console.log('\n✅ ERP-168 动态供应商对账与账龄 as-of 回显 UI 逻辑测试全部通过（' + passed + ' 项断言）。\n');
