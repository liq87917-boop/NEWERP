using System.Diagnostics;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-294：通用报表配置三种导出（Excel / PDF / 定义）的 Blob 下载生命周期回归。
/// 核心断言在离线 Node 夹具中执行真实 report-configuration.js 的 rccExport / rccExportPdf /
/// rccExportDefinition 与共享助手 rccTriggerDownload，覆盖有界延迟清理、重复导出、点击抛异常、
/// 迟到 / 取消 / 未授权响应的下载抑制，以及 POST / 认证 / 安全文件名等既有契约。
/// 真实浏览器与数据库验收由环境决定（environment-blocked），不在本测试内执行。
/// </summary>
public class ReportConfigurationDownloadLifecycleTests
{
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    [Fact]
    public void Node离线夹具_三种导出下载生命周期()
    {
        var jsPath = Path.Combine(JsDirectory(), "report-configuration.js");
        Assert.True(File.Exists(jsPath), "report-configuration.js 不存在：" + jsPath);

        var scriptPath = Path.Combine(Path.GetTempPath(), "rcc-download-lifecycle-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(scriptPath, FixtureScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var (exitCode, stdout, stderr) = RunNode(scriptPath, jsPath);

            Assert.True(exitCode == 0,
                "Node 夹具失败（exit=" + exitCode + "）。" + Environment.NewLine
                + "stdout: " + stdout + Environment.NewLine
                + "stderr: " + stderr);

            Assert.Contains("RCC_DOWNLOAD_LIFECYCLE_OK", stdout);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void 源码契约_共享有界下载生命周期与三种导出接线()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        Assert.Contains("function rccTriggerDownload(blob, a)", js);
        Assert.Contains("const url = URL.createObjectURL(blob);", js);
        Assert.Contains("if (released) return;", js);
        Assert.Contains("URL.revokeObjectURL(url);", js);
        Assert.Contains("setTimeout(release, RCC_DOWNLOAD_CLEANUP_DELAY_MS)", js);
        Assert.Contains("const RCC_DOWNLOAD_CLEANUP_DELAY_MS = 1000;", js);

        var exportFn = Segment(js, "async function rccExport()", "/* 导出当前预览页为中文 PDF");
        Assert.Contains("method: 'POST'", exportFn);
        Assert.Contains("'Authorization': 'Bearer '", exportFn);
        Assert.Contains("if (RCC.dirty)", exportFn);
        Assert.Contains("if (seq !== RCC.requestSeq) return;", exportFn);
        Assert.Contains("a.download = '报表配置_' + dateStr + '.xlsx'", exportFn);
        Assert.Contains("rccTriggerDownload(blob, a)", exportFn);
        Assert.DoesNotContain("URL.createObjectURL(blob)", exportFn);

        var exportPdfFn = Segment(js, "async function rccExportPdf()", "/* 导出可移植报表定义信封");
        Assert.Contains("method: 'POST'", exportPdfFn);
        Assert.Contains("'Authorization': 'Bearer '", exportPdfFn);
        Assert.Contains("if (RCC.dirty)", exportPdfFn);
        Assert.Contains("if (seq !== RCC.requestSeq) return;", exportPdfFn);
        Assert.Contains("a.download = '报表配置_' + dateStr + '.pdf'", exportPdfFn);
        Assert.Contains("rccTriggerDownload(blob, a)", exportPdfFn);
        Assert.DoesNotContain("URL.createObjectURL(blob)", exportPdfFn);

        var exportDefinitionFn = Segment(js, "async function rccExportDefinition()", "/* 选择本地定义文件后读取其文本并交给导入");
        Assert.Contains("RCC_API + '/transfer/export'", exportDefinitionFn);
        Assert.Contains("'POST'", exportDefinitionFn);
        Assert.Contains("if (seq !== RCC.requestSeq) return;", exportDefinitionFn);
        Assert.Contains("a.download = '报表定义_' + safeName + '_' + dateStr + '.json'", exportDefinitionFn);
        Assert.Contains("rccTriggerDownload(blob, a)", exportDefinitionFn);
        Assert.DoesNotContain("URL.createObjectURL(blob)", exportDefinitionFn);

        Assert.Contains("rccExportDefinition,", js);
        Assert.Contains("rccTriggerDownload,", js);
        Assert.Contains("RCC_DOWNLOAD_CLEANUP_DELAY_MS,", js);
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

    private static string FixtureScript => FixtureScriptPart1 + FixtureScriptPart2 + FixtureScriptPart3;

    private const string FixtureScriptPart1 = @"'use strict';
var assert = require('assert');
var path = require('path');

var target = process.argv[2];
if (!target) { console.error('missing target js'); process.exit(2); }

var realSetTimeout = setTimeout;
var pendingTimers = [];
var createdUrls = [];
var revokedUrls = [];
var anchors = [];
var fetchCalls = [];
var fetchImpl = null;
var throwOnClick = false;

var resultEl = { innerHTML: '', textContent: '' };
var dirtyEl = { textContent: '' };
var envBannerEl = { innerHTML: '' };

global.setTimeout = function (fn, delay) {
  pendingTimers.push({ fn: fn, delay: delay });
  return pendingTimers.length;
};
global.clearTimeout = function () {};

global.fetch = function (url, opts) {
  fetchCalls.push({ url: url, opts: opts || {} });
  if (fetchImpl) return fetchImpl(url, opts || {});
  return Promise.resolve(jsonResponse({ code: 0, data: { rows: [] } }));
};

function jsonResponse(envelope) {
  return {
    ok: true,
    status: 200,
    headers: { get: function () { return 'application/json'; } },
    json: function () { return Promise.resolve(envelope); },
    blob: function () { return Promise.resolve({ size: 0 }); }
  };
}

function spreadsheetResponse() {
  return {
    ok: true,
    status: 200,
    headers: { get: function () { return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'; } },
    json: function () { return Promise.resolve({}); },
    blob: function () { return Promise.resolve({ size: 0 }); }
  };
}

function pdfResponse() {
  return {
    ok: true,
    status: 200,
    headers: { get: function () { return 'application/pdf'; } },
    json: function () { return Promise.resolve({}); },
    blob: function () { return Promise.resolve({ size: 0 }); }
  };
}

global.localStorage = { getItem: function () { return 'test-token'; } };

var documentRef = {};
documentRef.body = {
  appendChild: function (el) { el.parentNode = documentRef.body; }
};
documentRef.getElementById = function (id) {
  if (id === 'rcc-result') return resultEl;
  if (id === 'rcc-env-banner') return envBannerEl;
  if (id === 'rcc-dirty') return dirtyEl;
  return { innerHTML: '', textContent: '' };
};
documentRef.createElement = function (tag) {
  var a = {
    tagName: (tag || '').toUpperCase(),
    href: '',
    download: '',
    parentNode: null,
    removed: false,
    clickCount: 0,
    click: function () {
      if (throwOnClick) { throw new Error('anchor click failed'); }
      a.clickCount++;
    },
    remove: function () {
      a.removed = true;
      a.parentNode = null;
    }
  };
  anchors.push(a);
  return a;
};
documentRef.querySelector = function () { return null; };
global.document = documentRef;

global.toast = function () {};
global.confirm = function () { return true; };
global.logout = function () {};
global.Blob = function (parts, opts) {
  return { parts: parts, type: opts && opts.type, size: String(parts && parts[0] || '').length };
};
global.URL = {
  createObjectURL: function () {
    var u = 'blob:mock-' + (createdUrls.length + 1);
    createdUrls.push(u);
    return u;
  },
  revokeObjectURL: function (u) { revokedUrls.push(u); }
};

var rcc = require(path.resolve(target));

function flushTimers() {
  var timers = pendingTimers.splice(0, pendingTimers.length);
  timers.forEach(function (t) { t.fn(); });
}

function resetState() {
  rcc.RCC.current = { id: 1, version: 1 };
  rcc.RCC.sharedCurrent = null;
  rcc.RCC.name = '测试报表';
  rcc.RCC.dirty = false;
  rcc.RCC.busy = false;
  rcc.RCC.requestSeq = 0;
  rcc.RCC.lastAction = '';
  rcc.RCC.activeAbort = null;
  rcc.RCC.view = null;
  rcc.RCC.envBlocked = false;
  rcc.RCC.filterErrors = [];
  rcc.RCC.filters = [];
  rcc.RCC.groupings = [];
  rcc.RCC.page = 1;
  rcc.RCC.pageSize = 20;
  rcc.RCC.previewRevision = null;
  rcc.RCC.datasets = [];
  rcc.RCC.fields = [];
  rcc.RCC.selectedKeys = [];
  fetchCalls.length = 0;
  createdUrls.length = 0;
  revokedUrls.length = 0;
  anchors.length = 0;
  pendingTimers.length = 0;
  fetchImpl = null;
  throwOnClick = false;
  resultEl.innerHTML = '';
  dirtyEl.textContent = '';
  envBannerEl.innerHTML = '';
}
";
    private const string FixtureScriptPart2 = @"
async function main() {
  // 1. 助手与定义导出入口暴露给 Node
  assert.strictEqual(typeof rcc.rccTriggerDownload, 'function', 'helper exported');
  assert.strictEqual(typeof rcc.rccExportDefinition, 'function', 'definition export exported');
  assert.ok(rcc.RCC_DOWNLOAD_CLEANUP_DELAY_MS > 0, 'bounded cleanup delay > 0');

  // 2. Excel：有界延迟清理 + POST / 认证 / 安全文件名
  resetState();
  fetchImpl = function () { return Promise.resolve(spreadsheetResponse()); };
  await rcc.rccExport();
  assert.strictEqual(anchors.length, 1, 'excel creates one anchor');
  assert.strictEqual(anchors[0].clickCount, 1, 'excel anchor clicked once');
  assert.strictEqual(createdUrls.length, 1, 'excel creates one blob URL');
  assert.strictEqual(revokedUrls.length, 0, 'excel URL NOT revoked in initiating turn');
  assert.strictEqual(anchors[0].removed, false, 'excel anchor NOT removed in initiating turn');
  assert.strictEqual(pendingTimers.length, 1, 'excel schedules one cleanup timer');
  assert.strictEqual(pendingTimers[0].delay, rcc.RCC_DOWNLOAD_CLEANUP_DELAY_MS, 'excel bounded delay');
  assert.strictEqual(fetchCalls.length, 1, 'excel issues one request');
  assert.strictEqual(fetchCalls[0].url, '/api/report-configurations/export');
  assert.strictEqual(fetchCalls[0].opts.method, 'POST');
  assert.ok(fetchCalls[0].opts.headers['Authorization'].indexOf('Bearer test-token') >= 0, 'excel sends auth header');
  assert.ok(anchors[0].download.indexOf('.xlsx') >= 0, 'excel safe xlsx filename');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 1, 'excel URL revoked once after delay');
  assert.strictEqual(anchors[0].removed, true, 'excel anchor removed after delay');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 1, 'excel cleanup idempotent (exactly once)');

  // 3. PDF：有界延迟清理 + 安全文件名
  resetState();
  fetchImpl = function () { return Promise.resolve(pdfResponse()); };
  await rcc.rccExportPdf();
  assert.strictEqual(anchors.length, 1, 'pdf creates one anchor');
  assert.strictEqual(anchors[0].clickCount, 1, 'pdf anchor clicked once');
  assert.strictEqual(createdUrls.length, 1, 'pdf creates one blob URL');
  assert.strictEqual(revokedUrls.length, 0, 'pdf URL NOT revoked in initiating turn');
  assert.strictEqual(pendingTimers.length, 1, 'pdf schedules one cleanup timer');
  assert.strictEqual(pendingTimers[0].delay, rcc.RCC_DOWNLOAD_CLEANUP_DELAY_MS, 'pdf bounded delay');
  assert.strictEqual(fetchCalls[0].url, '/api/report-configurations/export/pdf');
  assert.strictEqual(fetchCalls[0].opts.method, 'POST');
  assert.ok(anchors[0].download.indexOf('.pdf') >= 0, 'pdf safe filename');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 1, 'pdf URL revoked once after delay');
  assert.strictEqual(anchors[0].removed, true, 'pdf anchor removed after delay');

  // 4. 定义：有界延迟清理 + POST + 安全文件名（非法字符被替换）
  resetState();
  fetchImpl = function () { return Promise.resolve(jsonResponse({ code: 0, data: { name: '客户:报表/名称', version: 1 } })); };
  await rcc.rccExportDefinition();
  assert.strictEqual(anchors.length, 1, 'definition creates one anchor');
  assert.strictEqual(anchors[0].clickCount, 1, 'definition anchor clicked once');
  assert.strictEqual(createdUrls.length, 1, 'definition creates one blob URL');
  assert.strictEqual(revokedUrls.length, 0, 'definition URL NOT revoked in initiating turn');
  assert.strictEqual(pendingTimers.length, 1, 'definition schedules one cleanup timer');
  assert.strictEqual(pendingTimers[0].delay, rcc.RCC_DOWNLOAD_CLEANUP_DELAY_MS, 'definition bounded delay');
  assert.strictEqual(fetchCalls[0].url, '/api/report-configurations/transfer/export');
  assert.strictEqual(fetchCalls[0].opts.method, 'POST');
  assert.ok(anchors[0].download.indexOf('报表定义_') >= 0, 'definition prefix');
  assert.ok(anchors[0].download.indexOf('客户_报表_名称') >= 0, 'definition sanitized name');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 1, 'definition URL revoked once after delay');
  assert.strictEqual(anchors[0].removed, true, 'definition anchor removed after delay');

  // 5. 重复导出：每个 Blob URL 各释放一次，清理幂等
  resetState();
  fetchImpl = function () { return Promise.resolve(spreadsheetResponse()); };
  await rcc.rccExport();
  await rcc.rccExport();
  assert.strictEqual(anchors.length, 2, 'repeated export creates two anchors');
  assert.strictEqual(createdUrls.length, 2, 'repeated export creates two URLs');
  assert.strictEqual(anchors[0].clickCount, 1, 'first anchor clicked once');
  assert.strictEqual(anchors[1].clickCount, 1, 'second anchor clicked once');
  assert.strictEqual(pendingTimers.length, 2, 'repeated export schedules two timers');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 2, 'both URLs revoked once each');
  assert.strictEqual(anchors[0].removed, true, 'first anchor removed');
  assert.strictEqual(anchors[1].removed, true, 'second anchor removed');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 2, 'repeated cleanup stays idempotent');

  // 6. 点击抛异常：清理仍按有界延迟执行，绝不泄漏 Blob URL
  resetState();
  fetchImpl = function () { return Promise.resolve(pdfResponse()); };
  throwOnClick = true;
  await rcc.rccExportPdf();
  assert.strictEqual(createdUrls.length, 1, 'click exception still created URL');
  assert.strictEqual(anchors.length, 1, 'click exception still created anchor');
  assert.strictEqual(revokedUrls.length, 0, 'click exception does not revoke synchronously');
  assert.strictEqual(pendingTimers.length, 1, 'click exception still schedules cleanup');
  assert.ok(resultEl.innerHTML.indexOf('anchor click failed') >= 0, 'click exception bounded error rendered');
  flushTimers();
  assert.strictEqual(revokedUrls.length, 1, 'click exception URL released exactly once after delay');
  assert.strictEqual(anchors[0].removed, true, 'click exception anchor removed after delay');
";
    private const string FixtureScriptPart3 = @"
  // 7. 迟到 Excel 响应（数据集 / 配置变化）绝不触发下载
  resetState();
  var resolveLate = null;
  fetchImpl = function () { return new Promise(function (resolve) { resolveLate = resolve; }); };
  var pG = rcc.rccExport();
  rcc.rccTouch();
  resolveLate(spreadsheetResponse());
  await pG;
  assert.strictEqual(anchors.length, 0, 'stale excel must not create anchor');
  assert.strictEqual(createdUrls.length, 0, 'stale excel must not create URL');
  assert.strictEqual(pendingTimers.length, 0, 'stale excel must not schedule cleanup');

  // 8. 取消 PDF 请求绝不触发下载
  resetState();
  fetchImpl = function () { return Promise.resolve(pdfResponse()); };
  var pH = rcc.rccExportPdf();
  rcc.rccCancel();
  await pH;
  assert.strictEqual(anchors.length, 0, 'cancelled pdf must not create anchor');
  assert.strictEqual(createdUrls.length, 0, 'cancelled pdf must not create URL');
  assert.strictEqual(pendingTimers.length, 0, 'cancelled pdf must not schedule cleanup');

  // 9. 迟到定义响应绝不触发下载
  resetState();
  var resolveDef = null;
  fetchImpl = function () { return new Promise(function (resolve) { resolveDef = resolve; }); };
  var pI = rcc.rccExportDefinition();
  rcc.rccTouch();
  resolveDef(jsonResponse({ code: 0, data: { name: 'stale' } }));
  await pI;
  assert.strictEqual(anchors.length, 0, 'stale definition must not create anchor');
  assert.strictEqual(createdUrls.length, 0, 'stale definition must not create URL');
  assert.strictEqual(pendingTimers.length, 0, 'stale definition must not schedule cleanup');

  // 10. 未授权响应绝不触发下载
  resetState();
  fetchImpl = function () { return Promise.resolve(jsonResponse({ code: 2003, message: '登录已过期' })); };
  await rcc.rccExport();
  assert.strictEqual(anchors.length, 0, 'unauthorized excel must not create anchor');
  assert.strictEqual(createdUrls.length, 0, 'unauthorized excel must not create URL');
  assert.strictEqual(pendingTimers.length, 0, 'unauthorized excel must not schedule cleanup');

  console.log('RCC_DOWNLOAD_LIFECYCLE_OK');
}

main().catch(function (err) {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
";
}
