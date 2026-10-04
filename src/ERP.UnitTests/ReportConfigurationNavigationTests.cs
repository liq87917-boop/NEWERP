using System.Diagnostics;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-286 通用报表配置工作台授权导航入口测试：登录后按服务端报表配置目录授权决定是否展示
/// 一个「通用」入口（不是每个数据集一个菜单）。核心断言在离线 Node 夹具中执行真实 app.js 的
/// loadReportConfigurationEntry / addReportConfigurationNavEntry / renderMenu / addTab，
/// 覆盖授权 / 非授权目录、失败、注销重登与重复渲染去重、保留既有菜单与标签去重。
/// 真实浏览器登录与打开工作台由环境决定（environment-blocked），不在本测试内执行。
/// </summary>
public class ReportConfigurationNavigationTests
{
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    [Fact]
    public void Node离线夹具_授权入口_授权非授权失败重登与去重()
    {
        var jsPath = Path.Combine(JsDirectory(), "app.js");
        Assert.True(File.Exists(jsPath), "app.js 不存在：" + jsPath);

        var scriptPath = Path.Combine(Path.GetTempPath(), "rcc-navigation-" + Guid.NewGuid().ToString("N") + ".js");
        try
        {
            File.WriteAllText(scriptPath, FixtureScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var (exitCode, stdout, stderr) = RunNode(scriptPath, jsPath);

            Assert.True(exitCode == 0,
                "Node 夹具失败（exit=" + exitCode + "）。" + Environment.NewLine
                + "stdout: " + stdout + Environment.NewLine
                + "stderr: " + stderr);

            Assert.Contains("REPORT_CONFIGURATION_NAVIGATION_OK", stdout);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    [Fact]
    public void 源码契约_授权入口接线与FailClosed()
    {
        var app = File.ReadAllText(Path.Combine(JsDirectory(), "app.js"));

        // 通用入口：按服务端目录授权，单个通用入口（不是每个数据集一个菜单）
        Assert.Contains("function loadReportConfigurationEntry()", app);
        Assert.Contains("function addReportConfigurationNavEntry()", app);
        Assert.Contains("function hasAuthorizedReportDataset(catalog)", app);
        Assert.Contains("api('/api/report-configurations/catalog')", app);
        Assert.Contains("menuCode: 'report-configuration', menuName: '报表配置工作台'", app);

        // fail closed：目录为空 / 非数组 / 无授权数据集均不展示
        Assert.Contains("Array.isArray(catalog.datasets)", app);
        Assert.Contains("catalog.datasets.length > 0", app);
        Assert.Contains("目录失败（网络 / 未授权 / 环境未就绪）一律不展示入口", app);

        // 去重：重复渲染 / 注销重登不残留、不叠加
        Assert.Contains("nav.querySelectorAll('[data-code=\"report-configuration-entry\"]')", app);
        Assert.Contains("group.dataset.code = 'report-configuration-entry'", app);

        // 接入既有导航 / 标签生命周期，保留既有菜单与报表分派
        Assert.Contains("renderMenu(PROFILE.menus || []);", app);
        Assert.Contains("loadReportConfigurationEntry();", app);
        Assert.Contains("code === 'report-configuration'", app);
        Assert.Contains("renderReportConfigurationWorkspace()", app);
        Assert.Contains("function addTab(code, name)", app);
        Assert.Contains("openTabs = openTabs.filter(t => t.code !== code);", app);
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

    private const string FixtureScriptPart1 = @"'use strict';
var assert = require('assert');
var path = require('path');

var target = process.argv[2];
if (!target) { console.error('missing target js'); process.exit(2); }

function makeEl(tag) {
  var el = {
    tagName: (tag || 'DIV').toUpperCase(),
    children: [],
    dataset: {},
    className: '',
    style: {},
    textContent: '',
    onclick: null,
    parentNode: null,
    _innerHTML: '',
    _classes: {},
    classList: {
      add: function () { var a = Array.prototype.slice.call(arguments); for (var i = 0; i < a.length; i++) el._classes[a[i]] = true; },
      remove: function () { var a = Array.prototype.slice.call(arguments); for (var i = 0; i < a.length; i++) delete el._classes[a[i]]; },
      toggle: function (c, force) { var v = (typeof force === 'boolean') ? force : !el._classes[c]; if (v) el._classes[c] = true; else delete el._classes[c]; return v; },
      contains: function (c) { return !!el._classes[c]; }
    },
    appendChild: function (c) { c.parentNode = el; el.children.push(c); return c; },
    remove: function () {
      if (el.parentNode) {
        var i = el.parentNode.children.indexOf(el);
        if (i >= 0) el.parentNode.children.splice(i, 1);
      }
      el.parentNode = null;
    },
    querySelector: function () { return null; },
    querySelectorAll: function (sel) {
      var m = /\[data-code=""([^""]+)""\]/.exec(sel);
      if (!m) return [];
      return findByDataCode(el, m[1]);
    },
    addEventListener: function () {}
  };
  Object.defineProperty(el, 'innerHTML', {
    get: function () { return el._innerHTML; },
    set: function (v) { el._innerHTML = String(v); if (v === '') el.children = []; }
  });
  return el;
}

function findByDataCode(root, code) {
  var out = [];
  (function walk(node) {
    if (!node) return;
    if (node.dataset && node.dataset.code === code) out.push(node);
    (node.children || []).forEach(walk);
  })(root);
  return out;
}

var nav = makeEl('nav');
global.localStorage = { getItem: function () { return null; }, setItem: function () {}, removeItem: function () {} };
global.icon = function () { return '<svg></svg>'; };
global.document = {
  getElementById: function (id) {
    if (id === 'sidebar-nav') return nav;
    return makeEl('div');
  },
  createElement: function (tag) { return makeEl(tag); },
  querySelector: function () { return null; },
  querySelectorAll: function () { return []; },
  addEventListener: function () {},
  removeEventListener: function () {},
  readyState: 'loading',
  documentElement: makeEl('html'),
  body: { classList: { contains: function () { return false; } }, appendChild: function () {} }
};
global.location = { reload: function () {} };

function jsonEnvelope(envelope) {
  return { json: function () { return Promise.resolve(envelope); } };
}

var fetchImpl = null;
global.fetch = function (url, opts) {
  if (fetchImpl) return fetchImpl(url, opts || {});
  return Promise.resolve(jsonEnvelope({ code: 0, data: { datasets: [] } }));
};

var app = require(path.resolve(target));";

    private const string FixtureScriptPart2 = @"
async function main() {
  function countEntry() {
    return nav.querySelectorAll('[data-code=""report-configuration-entry""]').length;
  }
  function authorized() {
    return Promise.resolve(jsonEnvelope({ code: 0, data: { schemaVersion: 1, datasets: [{ datasetKey: 'sales-order', label: '销售订单' }] } }));
  }

  // 1. 授权目录 → 展示一个通用入口，子项携带 report-configuration 编码
  nav.innerHTML = '';
  fetchImpl = authorized;
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 1, 'authorized catalog adds exactly one entry');
  assert.strictEqual(nav.children.length, 1);
  assert.strictEqual(nav.children[0].dataset.code, 'report-configuration-entry');
  assert.ok(findByDataCode(nav.children[0], 'report-configuration') !== null, 'entry child carries report-configuration code');

  // 2. 非授权（空目录 / null / 非数组）→ 不展示
  nav.innerHTML = '';
  fetchImpl = function () { return Promise.resolve(jsonEnvelope({ code: 0, data: { datasets: [] } })); };
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 0, 'empty catalog adds no entry');

  nav.innerHTML = '';
  fetchImpl = function () { return Promise.resolve(jsonEnvelope({ code: 0, data: null })); };
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 0, 'null catalog adds no entry');

  nav.innerHTML = '';
  fetchImpl = function () { return Promise.resolve(jsonEnvelope({ code: 0, data: { datasets: 'x' } })); };
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 0, 'non-array datasets adds no entry');

  // 3. 目录失败（网络 reject / 业务非零）→ 不展示
  nav.innerHTML = '';
  fetchImpl = function () { return Promise.reject(new Error('network')); };
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 0, 'rejected catalog adds no entry');

  nav.innerHTML = '';
  fetchImpl = function () { return Promise.resolve(jsonEnvelope({ code: 500, message: '失败' })); };
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 0, 'business error adds no entry');

  // 4. 重复渲染 / 注销重登 → 不残留、不重复
  nav.innerHTML = '';
  fetchImpl = authorized;
  await app.loadReportConfigurationEntry();
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 1, 'repeated rendering keeps exactly one entry');

  app.renderMenu([]);
  await app.loadReportConfigurationEntry();
  await app.loadReportConfigurationEntry();
  assert.strictEqual(countEntry(), 1, 'logout/re-entry keeps exactly one entry');

  // 5. 保留既有菜单，通用入口追加在其后
  nav.innerHTML = '';
  app.renderMenu([
    { menuCode: 'report', menuName: '报表管理', children: [{ menuCode: 'sales-order', menuName: '销售订单' }] },
    { menuCode: 'base', menuName: '基础资料', children: [{ menuCode: 'customer', menuName: '客户资料' }] }
  ]);
  assert.strictEqual(nav.children.length, 2, 'existing menus preserved before entry');
  await app.loadReportConfigurationEntry();
  assert.strictEqual(nav.children.length, 3, 'existing menus + exactly one generic entry');
  assert.strictEqual(nav.children[2].dataset.code, 'report-configuration-entry', 'generic entry appended after existing menus');

  // 6. 标签去重（复用既有 addTab 生命周期）
  app.addTab('report-configuration', '报表配置工作台');
  app.addTab('report-configuration', '报表配置工作台');
  var tabs = app.getOpenTabs();
  assert.strictEqual(tabs.length, 1, 'addTab dedups report-configuration tab');
  assert.strictEqual(tabs[0].code, 'report-configuration');

  console.log('REPORT_CONFIGURATION_NAVIGATION_OK');
}

main().catch(function (err) {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
";

    private const string FixtureScript =
        FixtureScriptPart1 +
        FixtureScriptPart2;
}
