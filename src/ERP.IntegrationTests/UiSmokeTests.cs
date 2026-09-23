using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 真实浏览器 UI 测试 POC：登录页 → 主界面 → 菜单导航 → 退出
/// </summary>
[Collection("UiTests")]
// [Collection] 只负责共享 Fixture，不会转成 VSTest 用例属性；
// 显式 Trait 才能被验收脚本 / CI 的 --filter "Collection=UiTests" 正确选中（否则 0 用例静默通过）。
[Trait("Collection", "UiTests")]
public class UiSmokeTests
{
    private readonly UiTestFixture _fx;
    public UiSmokeTests(UiTestFixture fx) => _fx = fx;

    [Fact]
    public void 打开首页_默认显示登录页()
    {
        _fx.ResetBrowserSession();
        var loginPage = _fx.Driver.FindElement(By.Id("login-page"));
        Assert.NotNull(loginPage);
        Assert.True(loginPage.Displayed, "登录页应可见");
        _fx.CaptureEvidence("login-page-visible");
    }

    [Fact]
    public void 错误密码_提示登录错误且不跳转()
    {
        _fx.ResetBrowserSession();
        _fx.Driver.FindElement(By.Id("login-username")).Clear();
        _fx.Driver.FindElement(By.Id("login-username")).SendKeys("admin");
        _fx.Driver.FindElement(By.Id("login-password")).Clear();
        _fx.Driver.FindElement(By.Id("login-password")).SendKeys("WrongPass_123");
        _fx.Driver.FindElement(By.Id("login-btn")).Click();

        var wait = new WebDriverWait(_fx.Driver, TimeSpan.FromSeconds(5));
        var err = wait.Until(d =>
        {
            var el = d.FindElement(By.Id("login-error"));
            return el.Text.Length > 0 ? el : null;
        });
        Assert.NotNull(err);

        var loginPage = _fx.Driver.FindElement(By.Id("login-page"));
        Assert.True(loginPage.Displayed);
        var appPage = _fx.Driver.FindElement(By.Id("app-page"));
        Assert.False(appPage.Displayed, "错误密码不应跳到主界面");
        _fx.CaptureEvidence("invalid-password-rejected");
    }

    [Fact]
    public void 正确密码_进入主界面_可见菜单与用户名()
    {
        _fx.ResetBrowserSession();
        _fx.Driver.FindElement(By.Id("login-username")).Clear();
        _fx.Driver.FindElement(By.Id("login-username")).SendKeys("admin");
        _fx.Driver.FindElement(By.Id("login-password")).Clear();
        _fx.Driver.FindElement(By.Id("login-password")).SendKeys("Admin@123");
        _fx.Driver.FindElement(By.Id("login-btn")).Click();

        var wait = new WebDriverWait(_fx.Driver, TimeSpan.FromSeconds(15));
        // 不能只等元素出现：#app-page 一直在 DOM 中（登录页与主界面共用页面），必须等到「可见」才算登录成功
        wait.Until(d => d.FindElement(By.Id("app-page")).Displayed);
        var appPage = _fx.Driver.FindElement(By.Id("app-page"));
        Assert.True(appPage.Displayed, "登录成功后主界面应可见");

        var sidebarNav = _fx.Driver.FindElement(By.Id("sidebar-nav"));
        Assert.True(sidebarNav.Displayed);

        var headerUser = _fx.Driver.FindElement(By.Id("header-user"));
        Assert.False(string.IsNullOrEmpty(headerUser.Text));
        // 头部展示「姓名，无则登录名」（app.js: PROFILE.displayName || PROFILE.userName），因此不能写死 admin：
        // 这里与页面真实登录态比对，确认头部显示的确实是当前登录用户
        var expectedUser = ((IJavaScriptExecutor)_fx.Driver).ExecuteScript(
            "return (typeof PROFILE !== 'undefined' && PROFILE) ? String(PROFILE.displayName || PROFILE.userName || '') : '';")?.ToString();
        Assert.False(string.IsNullOrEmpty(expectedUser), "页面未加载当前登录用户信息（PROFILE）");
        Assert.Contains(expectedUser!, headerUser.Text);
        _fx.CaptureEvidence("admin-dashboard-visible");
    }

    [Fact]
    public void 退出登录_回到登录页()
    {
        _fx.ResetBrowserSession();
        _fx.Driver.FindElement(By.Id("login-username")).Clear();
        _fx.Driver.FindElement(By.Id("login-username")).SendKeys("admin");
        _fx.Driver.FindElement(By.Id("login-password")).Clear();
        _fx.Driver.FindElement(By.Id("login-password")).SendKeys("Admin@123");
        _fx.Driver.FindElement(By.Id("login-btn")).Click();

        var wait = new WebDriverWait(_fx.Driver, TimeSpan.FromSeconds(15));
        wait.Until(d => d.FindElement(By.Id("app-page")).Displayed);

        _fx.Driver.FindElement(By.Id("logout-btn")).Click();

        wait.Until(driver => driver.FindElement(By.Id("login-page")).Displayed);
        var loginPage = _fx.Driver.FindElement(By.Id("login-page"));
        Assert.True(loginPage.Displayed);
        var appPage = _fx.Driver.FindElement(By.Id("app-page"));
        Assert.False(appPage.Displayed);
        _fx.CaptureEvidence("logout-returned-to-login");
    }
}
