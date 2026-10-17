using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet.Views;

public partial class AboutPageView : UserControl
{
    public AboutPageView()
    {
        InitializeComponent();
    }

    /// <summary>点击头像：未登录去登录，已登录走二次确认登出。</summary>
    private void OnAvatarPointerPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as AboutPageViewModel)?.OnAvatarPointerPressed();
}