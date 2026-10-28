using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using BiliMusicDotNet.Models.Navigation;
using CommunityToolkit.Mvvm.Input;

namespace BiliMusicDotNet.Android.Controls;

/// <summary>
/// 安卓端底部导航栏 + 上方内容区：喂进一份导航项集合，由控件自行渲染标签与视图。
/// </summary>
/// <remarks>
/// 只依赖 <see cref="NavItem"/> 这一份共享数据模型，不认识任何具体页面；
/// 视图经 <c>Application.DataTemplates</c> 中的 <c>ViewLocator</c> 解析。
/// 样式常量与动画规格见 <c>docs/UI/导航控件文档.md</c>。
/// </remarks>
public partial class BottomNavControl : UserControl
{
    /// <summary>导航项数量上限，防止异常数据拖垮布局。</summary>
    private const int MaxNavItems = 8;

    /// <summary>导航项集合。</summary>
    public static readonly StyledProperty<ObservableCollection<NavItem>> ItemsResourceProperty =
        AvaloniaProperty.Register<BottomNavControl, ObservableCollection<NavItem>>(
            nameof(ItemsResource), new ObservableCollection<NavItem>());

    /// <summary>标签外观模板；不设置时用控件内置的「图标 + 文字」模板。</summary>
    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<BottomNavControl, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>当前选中项的索引，默认 0。</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<BottomNavControl, int>(
            nameof(SelectedIndex), 0,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>导航栏背景色。</summary>
    public static readonly StyledProperty<string> NavControlBackgroundColorProperty =
        AvaloniaProperty.Register<BottomNavControl, string>(
            nameof(NavControlBackgroundColor), "#FFFFFF");

    /// <summary>当前选中项；不属于导航项集合的项按外部选中处理。</summary>
    public static readonly DirectProperty<BottomNavControl, NavItem?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<BottomNavControl, NavItem?>(
            nameof(SelectedItem),
            o => o.SelectedItem,
            (o, value) => o.SelectedItem = value,
            defaultBindingMode: BindingMode.TwoWay);

    private NavItem? _selectedItem;
    private NavItem? _pendingSelectedItem;
    private bool _hasExternalSelection;

    public BottomNavControl()
    {
        InitializeComponent();

        SelectItemCommand = new RelayCommand<NavItem?>(SelectItem);
        TabItems.ItemsSource = ItemsResource;

        // 内置模板默认生效，外部设置 ItemTemplate 即覆盖。
        ItemTemplate = (IDataTemplate?)Resources["DefaultNavItemTemplate"];
    }

    /// <summary>导航项集合。</summary>
    public ObservableCollection<NavItem> ItemsResource
    {
        get => GetValue(ItemsResourceProperty);
        set => SetValue(ItemsResourceProperty, value);
    }

    /// <summary>标签外观模板；不设置时用控件内置的「图标 + 文字」模板。</summary>
    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    /// <summary>当前选中项的索引。越界值不抛异常，仅表现为无选中项。</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>导航栏背景色。</summary>
    public string NavControlBackgroundColor
    {
        get => GetValue(NavControlBackgroundColorProperty);
        set => SetValue(NavControlBackgroundColorProperty, value);
    }

    /// <summary>当前选中项，可写；不属于导航项集合的项按外部选中处理。</summary>
    public NavItem? SelectedItem
    {
        get => _selectedItem;
        set => SetSelectedItem(value);
    }

    /// <summary>模板内点击标签时调用的命令，参数为该导航项本身。</summary>
    public ICommand SelectItemCommand { get; }

    /// <summary>供模板点击回调：把被点的导航项设为选中项，并清掉外部选中态。</summary>
    public void SelectItem(NavItem? item)
    {
        if (item is null)
            return;

        var index = IndexOf(item);
        if (index < 0)
            return;

        _hasExternalSelection = false;
        _pendingSelectedItem = null;
        SelectedIndex = index;

        if (SelectedIndex == index)
            ApplyNormalSelection();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsResourceProperty)
        {
            Resubscribe(change.OldValue as ObservableCollection<NavItem>, change.NewValue as ObservableCollection<NavItem>);

            // 换集合时重新指向新的实例，否则标签仍绑在旧集合上。
            if (TabItems is not null)
                TabItems.ItemsSource = change.NewValue as ObservableCollection<NavItem>;

            ApplySelectionAfterItemsChanged();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            if (_hasExternalSelection)
            {
                UpdateSelection();
                return;
            }

            CommitSelectedItem();
            UpdateSelection();
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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplySelectionAfterItemsChanged();

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

    private int IndexOf(NavItem item)
    {
        var index = 0;
        foreach (var candidate in FlattenItems())
        {
            if (ReferenceEquals(candidate, item))
                return index;
            index++;
        }

        return -1;
    }

    /// <summary>
    /// 处理外部写入的选中项：命中导航项走正常选中，未命中则保留为外部项并清空高亮。
    /// </summary>
    private void SetSelectedItem(NavItem? value)
    {
        if (value is null)
        {
            SelectFirstAvailable();
            return;
        }

        var index = IndexOf(value);
        if (index >= 0)
        {
            SelectIndex(index);
            return;
        }

        _pendingSelectedItem = FlattenItems().Count == 0 ? value : null;
        _hasExternalSelection = true;
        SetSelectedItemInternal(value);

        if (SelectedIndex != -1)
            SelectedIndex = -1;

        UpdateSelection();
    }

    private void SetSelectedItemInternal(NavItem? value)
        => SetAndRaise(SelectedItemProperty, ref _selectedItem, value);

    private void SelectIndex(int index)
    {
        _hasExternalSelection = false;
        _pendingSelectedItem = null;
        SelectedIndex = index;

        if (SelectedIndex == index)
            ApplyNormalSelection();
    }

    private void SelectFirstAvailable()
    {
        _pendingSelectedItem = null;
        _hasExternalSelection = false;

        if (FlattenItems().Count == 0)
        {
            SelectedIndex = 0;
            SetSelectedItemInternal(null);
            UpdateSelection();
            return;
        }

        SelectIndex(0);
    }

    private void ApplyNormalSelection()
    {
        CommitSelectedItem();
        UpdateSelection();
    }

    /// <summary>集合换掉或增删后，先应用 pending 外部项，再保留已有外部项或走正常首项。</summary>
    private void ApplySelectionAfterItemsChanged()
    {
        if (_pendingSelectedItem is { } pending)
        {
            _pendingSelectedItem = null;
            _hasExternalSelection = false;

            var index = IndexOf(pending);
            if (index >= 0)
            {
                SelectIndex(index);
                return;
            }

            _hasExternalSelection = true;
            SetSelectedItemInternal(pending);
            SelectedIndex = -1;
            UpdateSelection();
            return;
        }

        if (_hasExternalSelection)
        {
            UpdateSelection();
            return;
        }

        CommitSelectedItem();
        UpdateSelection();
    }

    private void CommitSelectedItem()
    {
        if (_hasExternalSelection)
            return;

        var items = FlattenItems();
        var selected = SelectedIndex;

        SetSelectedItemInternal(selected >= 0 && selected < items.Count ? items[selected] : null);
    }

    private void UpdateSelection()
    {
        var items = FlattenItems();

        if (_hasExternalSelection)
        {
            foreach (var item in items)
                item.IsSelected = false;
            return;
        }

        var selected = SelectedIndex;

        for (var i = 0; i < items.Count; i++)
            items[i].IsSelected = i == selected;

        SetSelectedItemInternal(selected >= 0 && selected < items.Count ? items[selected] : null);
    }
}
