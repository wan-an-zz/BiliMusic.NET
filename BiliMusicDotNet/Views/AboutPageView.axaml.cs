using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Views;

public partial class AboutPageView : UserControl
{
    public AboutPageView()
    {
        InitializeComponent();

        DataContext = App.Services!.GetRequiredService<AboutPageViewModel>();
    }
}