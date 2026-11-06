using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet.Views;

public partial class AboutPageView : UserControl
{
    public AboutPageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>挂载后再确认一次选项行主题与模板，避免平台主题在附加阶段覆盖本地值。</summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Resources["AboutOptionTheme"] is ControlTheme theme)
            SettingsOptionButton.Theme = theme;

        if (Resources["AboutOptionRowTemplate"] is IControlTemplate template)
            SettingsOptionButton.Template = template;
    }

    /// <summary>点击头像：未登录去登录，已登录走二次确认登出。</summary>
    private void OnAvatarPointerPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as AboutPageViewModel)?.OnAvatarPointerPressed();
}