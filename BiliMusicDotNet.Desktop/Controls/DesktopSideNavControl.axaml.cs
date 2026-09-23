using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using BiliMusicDotNet.Models.Navigation;
using CommunityToolkit.Mvvm.Input;

namespace BiliMusicDotNet.Desktop.Controls;

/// <summary>
/// 桌面端左侧导航栏 + 右侧内容区
/// </summary>
/// <remarks>
/// 只依赖 <see cref="NavItem"/> 这一份共享数据模型，不认识任何具体页面；
/// 视图经 <c>Application.DataTemplates</c> 中的 <c>ViewLocator</c> 解析。
/// 分段规则、动画规格与样式常量见 <c>docs/UI/导航控件文档.md</c>。
/// </remarks>
public partial class DesktopSideNavControl : UserControl
{
    /// <summary>指示器淡出 / 淡入的等待时长（略长于 180ms 的透明度过渡）。</summary>
    private const int FadePhaseDelayMs = 210;

    /// <summary>指示器视觉高度，用于把图标顶边修正为指示器顶边。</summary>
    private const double IndicatorHeight = 48;

    /// <summary>导航项数量上限，防止异常数据撑爆位置缓存。</summary>
    private const int MaxNavItems = 64;

    /// <summary>导航项集合，控件按项的 <see cref="NavItem.Group"/> 自行分三段渲染。</summary>
    public static readonly StyledProperty<ObservableCollection<NavItem>> ItemsResourceProperty =
        AvaloniaProperty.Register<DesktopSideNavControl, ObservableCollection<NavItem>>(
            nameof(ItemsResource), new ObservableCollection<NavItem>());

    /// <summary>导航项外观模板；不设置时用控件内置的纯图标模板。</summary>
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<DesktopSideNavControl, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>
    /// 当前选中项的全局索引，按「顶部 A 组 → 中间 B 组 → 底部 A 组」顺序拼接计数，默认 0。
    /// </summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<DesktopSideNavControl, int>(
            nameof(SelectedIndex), 0,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>当前选中项，只读。</summary>
    public static readonly DirectProperty<DesktopSideNavControl, NavItem?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<DesktopSideNavControl, NavItem?>(
            nameof(SelectedItem), o => o.SelectedItem);

    private readonly ObservableCollection<NavItem> _topItems = new();
    private readonly ObservableCollection<NavItem> _middleItems = new();
    private readonly ObservableCollection<NavItem> _bottomItems = new();
    private readonly Dictionary<int, double> _indicatorOffsets = new();
    private NavItem? _selectedItem;
    private CancellationTokenSource? _animationCts;
    private double _lastHeight;

    public DesktopSideNavControl()
    {
        InitializeComponent();

        SelectItemCommand = new RelayCommand<NavItem?>(SelectItem);
        TopItems.ItemsSource = _topItems;
        MiddleItems.ItemsSource = _middleItems;
        BottomItems.ItemsSource = _bottomItems;
        LayoutUpdated += OnLayoutUpdated;

        // 内置模板默认生效，外部设置 ItemTemplate 即覆盖。
        ItemTemplate = (IDataTemplate?)Resources["DefaultNavItemTemplate"];
    }

    /// <summary>导航项集合，控件按项的 <see cref="NavItem.Group"/> 自行分三段渲染。</summary>
    public ObservableCollection<NavItem> ItemsResource
    {
        get => GetValue(ItemsResourceProperty);
        set => SetValue(ItemsResourceProperty, value);
    }

    /// <summary>导航项外观模板；不设置时用控件内置的纯图标模板。</summary>
    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>
    /// 当前选中项的全局索引，按「顶部 A 组 → 中间 B 组 → 底部 A 组」顺序拼接计数。
    /// 越界值不抛异常，仅表现为无选中项。
    /// </summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>当前选中项，只读；右侧内容区渲染它的 <see cref="NavItem.Content"/>。</summary>
    public NavItem? SelectedItem
    {
        get => _selectedItem;
        private set => SetAndRaise(SelectedItemProperty, ref _selectedItem, value);
    }

    /// <summary>模板内点击导航项时调用的命令，参数为该导航项本身。</summary>
    public ICommand SelectItemCommand { get; }

    /// <summary>
    /// 右侧内容面板：宿主可往里追加子元素（如自实现标题栏），后加的画在上层。
    /// </summary>
    /// <remarks>
    /// 控件不绘制这一层的外观；圆角、白底与阴影由宿主决定，以便贴合各自的窗口质感。
    /// </remarks>
    public Panel ContentArea => ContentAreaHost;

    /// <summary>导航栏外框，宿主可覆盖它的圆角 / 背景等外观。</summary>
    public Border Sidebar => SidebarHost;

    /// <summary>三段数据源的读取顺序，与全局索引的拼接顺序一致。</summary>
    private IReadOnlyList<ObservableCollection<NavItem>> Sections => new[]
    {
        _topItems,
        _middleItems,
        _bottomItems,
    };

    /// <summary>供模板点击回调：把被点的导航项设为选中项。</summary>
    public void SelectItem(NavItem? item)
    {
        if (item is null)
            return;

        var index = IndexOf(item);
        if (index >= 0)
            SelectedIndex = index;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 构造期（InitializeComponent 之前）具名元素还没就绪，此时不做任何计算。
        if (TopItems is null || MiddleItems is null || BottomItems is null)
            return;

        if (change.Property == ItemsResourceProperty)
        {
            Resubscribe(change.OldValue as ObservableCollection<NavItem>, change.NewValue as ObservableCollection<NavItem>);
            Repartition();
            ResetIndicatorToSelection();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            var oldIndex = change.OldValue is int old ? old : 0;
            var newIndex = change.NewValue is int current ? current : 0;

            UpdateSelection();
            if (oldIndex != newIndex && _indicatorOffsets.Count > 0)
            {
                if (!_indicatorOffsets.ContainsKey(newIndex))
                    CalculateIndicatorPositions();
                AnimateIndicator(oldIndex, newIndex);
            }
            else if (_indicatorOffsets.TryGetValue(newIndex, out var offset))
            {
                SetIndicatorPosition(offset);
            }
        }
        else if (change.Property == ItemTemplateProperty)
        {
            CalculateIndicatorPositions();
        }
    }

    private void Resubscribe(ObservableCollection<NavItem>? oldValue, ObservableCollection<NavItem>? newValue)
    {
        if (ReferenceEquals(oldValue, newValue))
            return;

        if (oldValue is not null)
            oldValue.CollectionChanged -= OnItemsChanged;

        if (newValue is not null)
            newValue.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Repartition();
        ResetIndicatorToSelection();
    }

    /// <summary>按 <see cref="NavItem.Group"/> 把集合摊进三段，保持各自原有顺序。</summary>
    private void Repartition()
    {
        _topItems.Clear();
        _middleItems.Clear();
        _bottomItems.Clear();

        foreach (var item in FlattenItems())
        {
            if (item.Group != NavGroup.A)
            {
                _middleItems.Add(item);
                continue;
            }

            // A 组要求「一个钉顶、一个钉底」，多于两个时按先后轮流分配到两端。
            if (_topItems.Count <= _bottomItems.Count)
                _topItems.Add(item);
            else
                _bottomItems.Add(item);
        }

        UpdateSelection();
    }

    /// <summary>把导航项按源顺序摊平，便于按下标取值。</summary>
    private List<NavItem> FlattenItems()
    {
        var items = new List<NavItem>();
        foreach (var item in ItemsResource)
        {
            items.Add(item);
            if (items.Count >= MaxNavItems)
                break;
        }

        return items;
    }

    /// <summary>按全局索引顺序摊平三段，与源集合的分段结果一致。</summary>
    private List<NavItem> FlattenSections() => Sections.SelectMany(section => section).ToList();

    private int IndexOf(NavItem item)
    {
        var index = 0;
        foreach (var section in Sections)
        {
            foreach (var candidate in section)
            {
                if (ReferenceEquals(candidate, item))
                    return index;
                index++;
            }
        }

        return -1;
    }

    private void UpdateSelection()
    {
        var flat = FlattenSections();
        var selected = SelectedIndex;
        var selectedItem = selected >= 0 && selected < flat.Count ? flat[selected] : null;

        for (var i = 0; i < flat.Count; i++)
            flat[i].IsSelected = i == selected;

        SelectedItem = selectedItem;
    }

    /// <summary>集合变动后重新定位指示器：清掉旧缓存并立即落位。</summary>
    private void ResetIndicatorToSelection()
    {
        _indicatorOffsets.Clear();
        CalculateIndicatorPositions();
        if (_indicatorOffsets.TryGetValue(SelectedIndex, out var offset))
        {
            Indicator.Opacity = 1;
            SetIndicatorPosition(offset);
        }
    }

    /// <summary>按三段顺序取出各段实际渲染出的项容器。</summary>
    private IEnumerable<Control> EnumerateContainers()
    {
        foreach (var host in new[] { TopItems, MiddleItems, BottomItems })
        {
            foreach (var child in host.GetVisualChildren())
            {
                if (child is Control { DataContext: not null } container)
                    yield return container;
            }
        }
    }

    /// <summary>读取各导航项真实布局位置并换算成指示器的 Margin.Top。</summary>
    private void CalculateIndicatorPositions()
    {
        UpdateLayout();

        var items = FlattenSections();
        var containers = EnumerateContainers().ToList();
        if (items.Count == 0 || containers.Count == 0)
        {
            _indicatorOffsets.Clear();
            return;
        }

        _indicatorOffsets.Clear();
        var count = Math.Min(items.Count, containers.Count);

        for (var i = 0; i < count; i++)
        {
            var container = containers[i];
            var icon = FindIcon(container);

            // 指示器顶边 = 图标（48 高）的顶边；没有图标时退回容器顶边。
            _indicatorOffsets[i] = icon is not null
                ? container.Bounds.Y + icon.Bounds.Y + ((icon.Bounds.Height - IndicatorHeight) / 2.0)
                : container.Bounds.Y;
        }
    }

    /// <summary>在项容器内找到图标元素，用于对齐指示器。</summary>
    private static Control? FindIcon(Control container)
    {
        foreach (var child in container.GetVisualDescendants())
        {
            if (child is PathIcon icon)
                return icon;
        }

        return null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        var isFirstLayout = _lastHeight == 0 && Bounds.Height > 0;
        var heightChanged = Math.Abs(Bounds.Height - _lastHeight) > 1;
        if (isFirstLayout || heightChanged)
        {
            _lastHeight = Bounds.Height;
            CalculateIndicatorPositions();
            if (_indicatorOffsets.TryGetValue(SelectedIndex, out var offset))
                SetIndicatorPosition(offset);
        }
    }

    private void SetIndicatorPosition(double y)
    {
        Indicator.Margin = new Thickness(0, y, 0, 0);
        Indicator.InvalidateArrange();
    }

    private void AnimateIndicator(int oldIndex, int newIndex)
    {
        if (!_indicatorOffsets.TryGetValue(newIndex, out var newOffset))
            return;

        var items = FlattenSections();
        if (oldIndex < 0 || newIndex < 0 || oldIndex >= items.Count || newIndex >= items.Count)
            return;

        // 只要过渡涉及首尾两端（A 组）就换成淡出淡入，避免指示器长距离掠过中间项。
        var involvesGroupA = items[oldIndex].Group == NavGroup.A || items[newIndex].Group == NavGroup.A;

        if (involvesGroupA)
            AnimateShrinkThenExpand(newOffset);
        else
            AnimateSmoothTranslate(newOffset);
    }

    private async void AnimateShrinkThenExpand(double newOffset)
    {
        var cts = new CancellationTokenSource();
        var previous = _animationCts;
        _animationCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        var token = cts.Token;

        try
        {
            Indicator.Opacity = 0;
            await Task.Delay(FadePhaseDelayMs, token);

            SetIndicatorPosition(newOffset);
            await Task.Delay(FadePhaseDelayMs, token);

            Indicator.Opacity = 1;
        }
        catch (OperationCanceledException)
        {
            // 快速连点时由后一次动画接管，无需处理。
        }
    }

    private void AnimateSmoothTranslate(double newOffset)
    {
        // 取消在途淡出，确保指示器可见后再平移。
        _animationCts?.Cancel();
        Indicator.Opacity = 1;
        SetIndicatorPosition(newOffset);
    }
}
