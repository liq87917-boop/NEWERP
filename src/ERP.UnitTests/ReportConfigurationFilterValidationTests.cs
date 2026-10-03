using System.Diagnostics;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-277：通用报表配置筛选校验（fail closed）。核心断言在离线 Node 夹具中执行真实
/// report-configuration.js 的纯函数与提交行为（rccSave / rccPreview / rccExport），
/// 而不是仅做源码静态断言：混合有效 / 无效 in-list 绝不静默缩小；无效筛选绝不能产生更宽的
/// 请求；数字 0、布尔 false、完整类型化值序列化不变；非法数字 / 布尔 / 日期、未知目录键 /
/// 操作符、between 反向或不完整均 fail closed；非法保存 / 预览 / 导出在发起网络请求前被阻断，
/// 合法修正后可提交。真实浏览器与数据库验收由环境决定（environment-blocked），不在本测试内执行。
/// </summary>
public class ReportConfigurationFilterValidationTests
{
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    [Fact]
    public void Node离线夹具_筛选校验与提交行为FailClosed()
    {
        var jsPath = Path.Combine(JsDirectory(), "report-configuration.js");
        Assert.True(File.Exists(jsPath), "report-configuration.js 不存在：" + jsPath);

        var scriptPath = Path.Combine(Path.GetTempPath(), "rcc-filter-validation-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(scriptPath, FixtureScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var (exitCode, stdout, stderr) = RunNode(scriptPath, jsPath);

            Assert.True(exitCode == 0,
                "Node 夹具失败（exit=" + exitCode + "）。" + Environment.NewLine
                + "stdout: " + stdout + Environment.NewLine
                + "stderr: " + stderr);

            Assert.Contains("RCC_FILTER_VALIDATION_OK", stdout);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void 源码契约_筛选校验与行级反馈接线()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccValidateFilter(fields, f)", js);
        Assert.Contains("function rccValidateFilters(state)", js);
        Assert.Contains("function rccHasFilterErrors()", js);
        Assert.Contains("if (rccHasFilterErrors()) return;", js);   // 保存 / 预览 / 导出提交前阻断
        Assert.Contains("class=\"rcc-filter-error\"", js);          // 行级反馈标记
        Assert.Contains("任一成员无效即整体失效", js);              // in-list 绝不静默缩小
        Assert.Contains("未知布尔值 fail closed", js);              // 未知布尔绝不静默落为 false
        Assert.Contains("between 下界不能大于上界", js);            // between 反向 fail closed
        Assert.DoesNotContain(".filter(v => v !== null)",
            Segment(js, "function rccTypedValue", "function rccFilterValueToString"));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunNode(string scriptPath, string jsPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add(jsPath);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static string Segment(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        if (i < 0) return string.Empty;
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        return j < 0 ? source[i..] : source[i..j];
    }

    /// <summary>
    /// 离线 Node 夹具：require 真实 report-configuration.js，stub 浏览器 / 网络全局，
    /// 对纯函数与 rccSave / rccPreview / rccExport 提交行为做真实断言，成功输出 RCC_FILTER_VALIDATION_OK。
    /// </summary>
    private const string FixtureScript = @"'use strict';
var assert = require('assert');
var path = require('path');

var target = process.argv[2];
if (!target) { console.error('missing target js'); process.exit(2); }

var calls = [];
global.localStorage = { getItem: function () { return null; } };
global.fetch = function (url, opts) {
  calls.push({ url: url, opts: opts });
  return Promise.resolve({
    ok: true,
    status: 200,
    headers: { get: function () { return 'application/json'; } },
    json: function () { return Promise.resolve({ code: 0, data: { id: 1, name: 'n', version: 1 } }); },
    blob: function () { return Promise.resolve({}); },
    text: function () { return Promise.resolve(''); }
  });
};
global.document = {
  getElementById: function () { return { innerHTML: '', textContent: '' }; },
  createElement: function () { return {}; },
  body: { appendChild: function () {} },
  querySelector: function () { return null; }
};
global.toast = function () {};
global.logout = function () {};
global.URL = { createObjectURL: function () { return 'blob:'; }, revokeObjectURL: function () {} };
global.Blob = function () {};

var rcc = require(path.resolve(target));

var fields = [
  { key: 'amount', type: 'number', filterable: true, filterOperators: ['eq', 'in', 'between'] },
  { key: 'active', type: 'boolean', filterable: true, filterOperators: ['eq'] },
  { key: 'day', type: 'date', filterable: true, filterOperators: ['eq', 'between'] },
  { key: 'note', type: 'text', filterable: true, filterOperators: ['eq', 'in'] }
];

// 1. mixed valid/invalid in-list cannot silently narrow
assert.strictEqual(rcc.rccTypedValue(fields[0], 'in', '1,abc,2'), null);
assert.deepStrictEqual(rcc.rccTypedValue(fields[0], 'in', '1,2,3'), [1, 2, 3]);

// 2. invalid numeric fails; numeric zero stays zero
assert.strictEqual(rcc.rccTypedScalar(fields[0], 'abc'), null);
assert.strictEqual(rcc.rccTypedScalar(fields[0], '0'), 0);

// 3. boolean false valid; unknown boolean fails closed
assert.strictEqual(rcc.rccTypedScalar(fields[1], 'false'), false);
assert.strictEqual(rcc.rccTypedScalar(fields[1], 'true'), true);
assert.strictEqual(rcc.rccTypedScalar(fields[1], 'yes'), null);

// 4. invalid date fails; valid date passes
assert.strictEqual(rcc.rccTypedScalar(fields[2], '2026-02-31'), null);
assert.strictEqual(rcc.rccTypedScalar(fields[2], '2026-02-28'), '2026-02-28');

// 5. unknown catalog key / operator / reversed & incomplete between fail closed
assert.strictEqual(rcc.rccBuildFilter(fields, { fieldKey: 'nope', operator: 'eq', value: '1' }), null);
assert.strictEqual(rcc.rccBuildFilter(fields, { fieldKey: 'amount', operator: 'nope', value: '1' }), null);
assert.strictEqual(rcc.rccBuildFilter(fields, { fieldKey: 'amount', operator: 'between', value: '10', value2: '5' }), null);
assert.strictEqual(rcc.rccBuildFilter(fields, { fieldKey: 'amount', operator: 'between', value: '10', value2: '' }), null);

// 6. rccValidateFilters reports indexed row errors
var errs = rcc.rccValidateFilters({ fields: fields, filters: [
  { fieldKey: 'amount', operator: 'eq', value: 'abc' },
  { fieldKey: 'amount', operator: 'between', value: '10', value2: '5' }
]});
assert.strictEqual(errs.length, 2);
assert.strictEqual(errs[0].index, 0);
assert.strictEqual(errs[1].index, 1);

// 7. valid filters serialize unchanged (numeric zero, boolean false, complete typed in-list)
rcc.RCC.datasets = [{ datasetKey: 'ds', label: 'DS', fields: fields, metrics: [], groupingDimensions: [], relations: [] }];
rcc.RCC.datasetKey = 'ds';
var def = rcc.rccBuildDefinition({
  catalog: { schemaVersion: 1 },
  datasetKey: 'ds',
  fields: fields,
  selectedKeys: ['amount', 'active', 'day'],
  filters: [
    { fieldKey: 'amount', operator: 'in', value: '1,2,3' },
    { fieldKey: 'active', operator: 'eq', value: 'false' },
    { fieldKey: 'amount', operator: 'eq', value: '0' }
  ],
  groupings: [],
  coverage: 'current-page',
  aggregates: [],
  computedColumns: [],
  relations: [],
  pivot: { rowDimension: '', columnDimension: '' }
});
assert.strictEqual(def.filters.length, 3);
assert.deepStrictEqual(def.filters[0].value, [1, 2, 3]);
assert.strictEqual(def.filters[1].value, false);
assert.strictEqual(def.filters[2].value, 0);

// 8. submission: invalid filter -> no network; valid correction -> network
rcc.RCC.fields = fields;
rcc.RCC.selectedKeys = ['amount'];
rcc.RCC.name = 'x';
rcc.RCC.current = null;
rcc.RCC.busy = false;
rcc.RCC.filterErrors = [];
rcc.RCC.filters = [{ fieldKey: 'amount', operator: 'eq', value: 'abc' }];
calls.length = 0;
rcc.rccSave()
  .then(function () {
    assert.strictEqual(calls.length, 0, 'invalid save must not issue network request');

    rcc.RCC.filters = [{ fieldKey: 'amount', operator: 'eq', value: '0' }];
    return rcc.rccSave();
  })
  .then(function () {
    assert.ok(calls.length > 0, 'valid save must issue network request');

    // 9. preview/export with invalid filter (saved, not dirty) must not fetch
    rcc.RCC.current = { id: 1, version: 1 };
    rcc.RCC.dirty = false;
    rcc.RCC.filters = [{ fieldKey: 'amount', operator: 'between', value: '10', value2: '5' }];
    calls.length = 0;
    return rcc.rccPreview();
  })
  .then(function () {
    assert.strictEqual(calls.length, 0, 'invalid preview must not issue network request');
    return rcc.rccExport();
  })
  .then(function () {
    assert.strictEqual(calls.length, 0, 'invalid export must not issue network request');
    console.log('RCC_FILTER_VALIDATION_OK');
  })
  .catch(function (err) {
    console.error(err && err.stack ? err.stack : String(err));
    process.exit(1);
  });
";
}
