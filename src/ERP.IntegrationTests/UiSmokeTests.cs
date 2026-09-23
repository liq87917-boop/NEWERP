using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 真实浏览器 UI 测试 POC：登录页 → 主界面 → 菜单导航 → 退出
/// </summary>
[Collection("UiTests")]
public class UiSmokeTests
{
    private readonly UiTestFixture _fx;
    public UiSmokeTests(UiTestFixture fx) => _fx = fx;

    [Fact]
    public void 打开首页_默认显示登录页()
    {
        _fx.Driver.Navigate().GoToUrl(UiTestFixture.BaseUrl);
        var loginPage = _fx.Driver.FindElement(By.Id("login-page"));
        Assert.NotNull(loginPage);
        Assert.True(loginPage.Displayed, "登录页应可见");
    }

    [Fact]
    public void 错误密码_提示登录错误且不跳转()
    {
        _fx.Driver.Navigate().GoToUrl(UiTestFixture.BaseUrl);
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
    }

    [Fact]
    public void 正确密码_进入主界面_可见菜单与用户名()
    {
        _fx.Driver.Navigate().GoToUrl(UiTestFixture.BaseUrl);
        _fx.Driver.FindElement(By.Id("login-username")).Clear();
        _fx.Driver.FindElement(By.Id("login-username")).SendKeys("admin");
        _fx.Driver.FindElement(By.Id("login-password")).Clear();
        _fx.Driver.FindElement(By.Id("login-password")).SendKeys("Admin@123");
        _fx.Driver.FindElement(By.Id("login-btn")).Click();

        var wait = new WebDriverWait(_fx.Driver, TimeSpan.FromSeconds(15));
        var appPage = wait.Until(d => d.FindElement(By.Id("app-page")));
        Assert.True(appPage.Displayed, "登录成功后主界面应可见");

        var sidebarNav = _fx.Driver.FindElement(By.Id("sidebar-nav"));
        Assert.True(sidebarNav.Displayed);

        var headerUser = _fx.Driver.FindElement(By.Id("header-user"));
        Assert.False(string.IsNullOrEmpty(headerUser.Text));
        Assert.Contains("admin", headerUser.Text);
    }

    [Fact]
    public void 退出登录_回到登录页()
    {
        _fx.Driver.Navigate().GoToUrl(UiTestFixture.BaseUrl);
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
    }
}