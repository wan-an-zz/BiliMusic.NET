using Android.Provider;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using BiliMusicDotNet.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BiliMusicDotNet.Android.Views;

public partial class AndroidMainView : UserControl
{
    public AndroidMainView()
    {
        InitializeComponent();

        DataContext = App.Services!.GetRequiredService<MainViewModel>();
    }
}