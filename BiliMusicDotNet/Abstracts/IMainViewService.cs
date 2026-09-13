using Avalonia.Controls;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet.Abstracts;

public interface IMainViewService
{
    /// <summary>
    /// 创建该平台的主视图。
    /// </summary>
    /// <param name="viewModel">主视图模型（两端共享的业务状态）。</param>
    /// <returns>该平台的主视图</returns>
    Control CreateMainView(MainViewModel viewModel);
}
