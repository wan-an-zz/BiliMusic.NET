using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BiliMusicDotNet.ViewModels;
using BiliMusicDotNet.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Tests;

/// <summary>
/// 视图定位器：导航项内容与弹窗内容都靠它把视图模型接到视图上。
/// </summary>
/// <remarks>
/// 页面视图自己不再取视图模型（code-behind 已改为无参构造），所以这条链路是唯一的注入点，
/// 断了就会出现"界面出来了但绑定全空"。
/// </remarks>
public class ViewLocatorTests
{
    [AvaloniaFact]
    public void 关于页内容能解析出已注入数据上下文的视图()
    {
        AssertResolves<AboutPageView, AboutPageViewModel>(new AboutPageViewModel(
            new HeadlessServices.StubLoginService(),
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default));
    }

    [AvaloniaFact]
    public void 推荐页内容能解析出已注入数据上下文的视图()
    {
        var model = App.Services!.GetRequiredService<RecommendationPageViewModel>();

        AssertResolves<RecommendationPageView, RecommendationPageViewModel>(model);
    }

    [AvaloniaFact]
    public void 扫码视图能解析出已注入数据上下文的视图()
    {
        AssertResolves<QrLoginView, QrLoginViewModel>(new QrLoginViewModel(() => { }));
    }

    [AvaloniaFact]
    public void 未注册的视图模型回退为提示文本()
    {
        var locator = new ViewLocator(App.Services!);

        var control = locator.Build(new UnregisteredViewModel());

        Assert.IsType<TextBlock>(control);
    }

    private static void AssertResolves<TView, TViewModel>(TViewModel model)
        where TView : Control
        where TViewModel : ViewModelBase
    {
        var locator = new ViewLocator(App.Services!);

        var control = locator.Build(model);

        var typed = Assert.IsType<TView>(control);
        Assert.Same(model, typed.DataContext);
    }

    /// <summary>没有同名视图的视图模型，用于验证兜底分支。</summary>
    private sealed class UnregisteredViewModel : ViewModelBase;
}
