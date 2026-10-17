using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using BiliMusicDotNet.ViewModels;

namespace BiliMusicDotNet;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private readonly IServiceProvider _services;

    /// <summary>
    /// 创建视图定位器。
    /// </summary>
    /// <param name="services">用于解析视图的容器（组合根）。</param>
    public ViewLocator(IServiceProvider services) => _services = services;

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type is null)
            return NotFound(name);

        // 优先交给容器创建：视图同样享有构造函数注入。
        var control = _services.GetService(type) as Control
            // 兜底：未注册到容器的视图仍按无参构造创建，保持模板原有行为。
            ?? Activator.CreateInstance(type) as Control;

        if (control is null)
            return NotFound(name);
        
        control.DataContext ??= param;

        return control;
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }

    private static TextBlock NotFound(string name) => new() { Text = "Not Found: " + name };
}
