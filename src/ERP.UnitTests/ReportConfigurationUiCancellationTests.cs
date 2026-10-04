using System.Diagnostics;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-283：通用报表配置工作台取消预览 / 导出请求的离线状态机覆盖。核心断言在离线 Node 夹具中执行真实
/// report-configuration.js 的 rccPreview / rccExport / rccExportPdf / rccCancel / rccDisposeWorkspace，
/// 覆盖取消于响应头之前、导出 blob 读取期间、迟到响应、切换 / 离开工作台、再次操作成功与正常错误，
/// 并保留既有 UI / 导出契约（正常导出仍下载）。真实浏览器与数据库验收由环境决定（environment-blocked），不在本测试内执行。
/// </summary>
public class ReportConfigurationUiCancellationTests
{
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    [Fact]
    public void Node离线夹具_取消预览与导出请求状态机()
    {
        var jsPath = Path.Combine(JsDirectory(), "report-configuration.js");
        Assert.True(File.Exists(jsPath), "report-configuration.js 不存在：" + jsPath);

        var scriptPath = Path.Combine(Path.GetTempPath(), "rcc-cancellation-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(scriptPath, FixtureScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var (exitCode, stdout, stderr) = RunNode(scriptPath, jsPath);

            Assert.True(exitCode == 0,
                "Node 夹具失败（exit=" + exitCode + "）。" + Environment.NewLine
                + "stdout: " + stdout + Environment.NewLine
                + "stderr: " + stderr);

            Assert.Contains("RCC_CANCELLATION_OK", stdout);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void 源码契约_取消接线与只读请求作用域()
    {
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "report-configuration.js"));

        // 取消动作在预览 / Excel / PDF 期间可见，使用 AbortController 与请求作用域信号
        Assert.Contains("onclick=\"rccCancel()\"", js);
        Assert.Contains("function rccCancel()", js);
        Assert.Contains("new AbortController()", js);
        Assert.Contains("RCC.activeAbort = controller", js);
        Assert.Contains("signal: controller.signal", js);
        Assert.Contains("async function rccPreview()", js);
        Assert.Contains("async function rccExport()", js);
        Assert.Contains("async function rccExportPdf()", js);

        // 取消立即释放忙态 / 保留状态，且只针对只读预览 / 导出（不触碰已提交生命周期写入）
        Assert.Contains("RCC.busy = false;", js);
        Assert.Contains("RCC.lastAction !== 'preview'", js);
        Assert.Contains("rccErrorHtml('cancelled'", js);
        Assert.Contains("AbortError", js);

        // 切换 / 离开工作台释放活动只读 / 导出请求
        Assert.Contains("function rccDisposeWorkspace()", js);
        Assert.Contains("function rccAbortActive()", js);
        Assert.Contains("rccAbortActive()", js);
        Assert.Contains("MutationObserver", js);

        // 迟到响应 / 导出 blob 在读取期间被取消绝不触发下载
        Assert.Contains("if (seq !== RCC.requestSeq) return;", js);
        Assert.Contains("读取 blob 期间被取消 / 离开：绝不触发下载", js);
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

    private const string FixtureScript =
        FixtureScriptPart1 +
        FixtureScriptPart2 +
        FixtureScriptPart3;

    private const string FixtureScriptPart1 = @"'use strict';
var assert = require('assert');
var path = require('path');

var target = process.argv[2];
if (!target) { console.error('missing target js'); process.exit(2); }

var downloads = 0;
var fetchCalls = [];
var fetchImpl = null;
var resultEl = { innerHTML: '', textContent: '' };

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

function abortError() {
  var e = new Error('The operation was aborted');
  e.name = 'AbortError';
  return e;
}

global.localStorage = { getItem: function () { return null; } };
global.document = {
  getElementById: function (id) {
    if (id === 'rcc-result') return resultEl;
    if (id === 'rcc-env-banner') return { innerHTML: '' };
    if (id === 'rcc-dirty') return { textContent: '' };
    return { innerHTML: '', textContent: '' };
  },
  createElement: function (tag) {
    return { tagName: (tag || '').toUpperCase(), click: function () { downloads++; }, remove: function () {} };
  },
  body: { appendChild: function () {} },
  querySelector: function () { return null; }
};
global.toast = function () {};
global.confirm = function () { return true; };
global.URL = { createObjectURL: function () { return 'blob:mock'; }, revokeObjectURL: function () {} };

var rcc = require(path.resolve(target));

function resetState() {
  rcc.RCC.current = { id: 1, version: 1 };
  rcc.RCC.name = '测试报表';
  rcc.RCC.dirty = false;
  rcc.RCC.busy = false;
  rcc.RCC.requestSeq = 0;
  rcc.RCC.lastAction = '';
  rcc.RCC.activeAbort = null;
  rcc.RCC.view = null;
  rcc.RCC.envBlocked = false;
  rcc.RCC.filterErrors = [];
  fetchCalls.length = 0;
  downloads = 0;
  fetchImpl = null;
  resultEl.innerHTML = '';
}

function tick() { return new Promise(function (resolve) { setTimeout(resolve, 0); }); }
";

    private const string FixtureScriptPart2 = @"
async function main() {
  // 1. 取消于响应头之前：释放忙态 / 保留状态，迟到响应不覆盖，显式取消结果
  resetState();
  var resolveLate = null;
  fetchImpl = function () { return new Promise(function (resolve) { resolveLate = resolve; }); };
  var p1 = rcc.rccPreview();
  assert.strictEqual(rcc.RCC.busy, true, 'preview should set busy');
  assert.ok(rcc.RCC.activeAbort, 'preview should create an AbortController');
  var sig1 = rcc.RCC.activeAbort.signal;
  assert.strictEqual(sig1.aborted, false, 'signal starts not aborted');
  rcc.rccCancel();
  assert.strictEqual(sig1.aborted, true, 'cancel aborts the in-flight preview signal');
  assert.strictEqual(rcc.RCC.busy, false, 'cancel releases busy immediately');
  assert.strictEqual(rcc.RCC.activeAbort, null, 'cancel clears active abort');
  assert.strictEqual(rcc.RCC.current.id, 1, 'cancel preserves saved definition');
  assert.strictEqual(rcc.RCC.dirty, false, 'cancel preserves unsaved edits (dirty)');
  assert.ok(resultEl.innerHTML.indexOf('请求已取消') >= 0, 'explicit cancelled result distinct from other errors');
  resolveLate(jsonResponse({ code: 0, data: { rows: [{ a: 1 }] } }));
  await p1;
  assert.strictEqual(rcc.RCC.view, null, 'late preview response must not replace preview');

  // 2. 取消后下一次操作成功
  fetchImpl = function () { return Promise.resolve(jsonResponse({ code: 0, data: { rows: [{ a: 2 }] } })); };
  await rcc.rccPreview();
  assert.ok(rcc.RCC.view !== null, 'subsequent preview succeeds after cancel');
  assert.strictEqual(rcc.RCC.busy, false, 'subsequent preview clears busy');

  // 3. 取消于导出 blob 读取期间（响应头已返回）
  resetState();
  fetchImpl = function (url, opts) {
    return Promise.resolve({
      ok: true,
      status: 200,
      headers: { get: function () { return 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'; } },
      json: function () { return Promise.resolve({}); },
      blob: function () {
        return new Promise(function (resolve, reject) {
          if (opts.signal && opts.signal.aborted) { reject(abortError()); return; }
          if (opts.signal) opts.signal.addEventListener('abort', function () { reject(abortError()); });
        });
      }
    });
  };
  var p3 = rcc.rccExport();
  await tick();
  assert.strictEqual(rcc.RCC.busy, true, 'export busy during blob read');
  assert.ok(rcc.RCC.activeAbort, 'export holds active abort');
  var sig3 = rcc.RCC.activeAbort.signal;
  rcc.rccCancel();
  assert.strictEqual(sig3.aborted, true, 'cancel aborts export blob read');
  assert.strictEqual(rcc.RCC.busy, false, 'cancel releases export busy');
  await p3;
  assert.strictEqual(downloads, 0, 'cancelled export must not trigger a download');
";

    private const string FixtureScriptPart3 = @"
  // 4. 切换（rccTouch）使在途请求过期并释放忙态；迟到响应不覆盖
  resetState();
  var resolveSwitch = null;
  fetchImpl = function () { return new Promise(function (resolve) { resolveSwitch = resolve; }); };
  var p4 = rcc.rccPreview();
  assert.strictEqual(rcc.RCC.busy, true);
  rcc.rccTouch();
  assert.strictEqual(rcc.RCC.busy, false, 'switch releases busy');
  assert.strictEqual(rcc.RCC.activeAbort, null, 'switch disposes active read');
  resolveSwitch(jsonResponse({ code: 0, data: { rows: [{ a: 3 }] } }));
  await p4;
  assert.strictEqual(rcc.RCC.view, null, 'superseded preview must not replace');

  // 5. 离开 / 重进工作台：释放活动请求；随后操作成功
  resetState();
  var resolveLeave = null;
  fetchImpl = function () { return new Promise(function (resolve) { resolveLeave = resolve; }); };
  var p5 = rcc.rccPreview();
  assert.ok(rcc.RCC.activeAbort);
  var sig5 = rcc.RCC.activeAbort.signal;
  rcc.rccDisposeWorkspace();
  assert.strictEqual(sig5.aborted, true, 'leaving aborts active read');
  assert.strictEqual(rcc.RCC.busy, false, 'leaving releases busy');
  assert.strictEqual(rcc.RCC.activeAbort, null);
  resolveLeave(jsonResponse({ code: 0, data: { rows: [{ a: 4 }] } }));
  await p5;
  assert.strictEqual(rcc.RCC.view, null, 'disposed preview must not replace');
  fetchImpl = function () { return Promise.resolve(jsonResponse({ code: 0, data: { rows: [{ a: 5 }] } })); };
  await rcc.rccPreview();
  assert.ok(rcc.RCC.view !== null, 're-entry preview succeeds');

  // 6. 正常错误：渲染错误与网络错误区分，忙态复位
  resetState();
  fetchImpl = function () { return Promise.resolve(jsonResponse({ code: 5001, message: '字体缺失' })); };
  await rcc.rccPreview();
  assert.strictEqual(rcc.RCC.busy, false, 'normal error clears busy');
  assert.ok(resultEl.innerHTML.indexOf('文件生成失败') >= 0, 'rendering error rendered');

  resetState();
  fetchImpl = function () { return Promise.reject(new Error('boom')); };
  await rcc.rccPreview();
  assert.strictEqual(rcc.RCC.busy, false, 'network error clears busy');
  assert.ok(resultEl.innerHTML.indexOf('网络请求失败') >= 0, 'network error rendered');

  // 7. 正常导出仍下载（未破坏既有导出路径）
  resetState();
  fetchImpl = function () { return Promise.resolve(spreadsheetResponse()); };
  await rcc.rccExport();
  assert.strictEqual(downloads, 1, 'normal export still triggers download');
  assert.strictEqual(rcc.RCC.busy, false);

  console.log('RCC_CANCELLATION_OK');
}

main().catch(function (err) {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
";
}
