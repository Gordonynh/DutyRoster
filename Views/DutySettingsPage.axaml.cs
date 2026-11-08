using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.DutyRoster.Models;
using ClassIsland.DutyRoster.Services;
using ClassIsland.PluginShared;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.DutyRoster.Views;

/// <summary>「显示」那半里表格的一行。</summary>
public sealed record TodaySlotRow(string TimeText, string ProjectText, string PeopleText, string SweepText);

/// <summary>
/// 值日提醒的设置页。
/// </summary>
/// <remarks>
/// 两个 Tab：「常规设置」放原有的全部设置项，「值日表设置」放只读的「显示」和可视化「配置」。
/// <para/>
/// <b>注意类声明上重列了 <see cref="INotifyPropertyChanged"/>。</b>
/// 基类 <c>AvaloniaObject</c> 对它是显式实现，本页用显式接口实现重新提供了一遍事件；
/// 两处必须成对存在——删掉重列的接口或删掉显式实现，XAML 绑定就收不到本页的通知，
/// 表现为所有概览文字永久冻结，而且编译器不会报错。
/// </remarks>
[SettingsPageInfo("gordon.dutyroster", "值日提醒", "", "")]
public partial class DutySettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly DutyRosterService? _service;

    private readonly EventHandler _scheduleChangedHandler;
    private readonly PropertyChangedEventHandler _settingsChangedHandler;

    private TokenListEditor? _sweepEditor;

    public DutySettings Settings => DutySettings.Current;

    public DutySettingsPage()
    {
        _service = IAppHost.Host?.Services.GetService<DutyRosterService>();
        DataContext = this;
        InitializeComponent();

        _scheduleChangedHandler = (_, _) => RefreshSummaries();
        _settingsChangedHandler = (_, _) => RefreshSummaries();

        if (_service is not null)
        {
            _service.ScheduleChanged += _scheduleChangedHandler;
        }

        // 设置里任何一项变了都把派生的显示文字重算一遍。
        // 不挂这个的话，拖完滑块旁边那个数字不会变——绑定的是本页的只读属性，
        // 而本页从来没为它发过变更通知。
        Settings.PropertyChanged += _settingsChangedHandler;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        WireSweepEditor();

        // 逻辑树查找在构造期理论上就能拿到，但 TabItem 内容的挂载时机受模板影响。
        // 万一这时候还看不到，等挂进可视树后再试一次——不试的话就是「编辑器看着在、
        // 候选不出现、点了没反应」那种最难查的静默失效。
        if (_sweepEditor is null)
        {
            Loaded += OnPageLoadedOnce;
        }

        RefreshSummaries();
    }

    private void OnPageLoadedOnce(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoadedOnce;
        if (_sweepEditor is null)
        {
            WireSweepEditor();
            _sweepEditor?.Rebuild();
        }
    }

    /// <summary>
    /// 找到大扫除项目编辑器并接上数据。
    /// </summary>
    /// <remarks>
    /// <b>不能用 <c>FindControl("SweepProjectsEditor")</c>。</b><c>FindControl</c> 只查名字作用域，
    /// 而 <c>TabItem</c> 的内容是独立 NameScope——Tab 化之后它返回 null，
    /// 后面那句 <c>?.Configure(...)</c> 就静默跳过，大扫除项目编辑器不显示候选、点了也没反应、
    /// 而且不报任何错。必须走逻辑树。
    /// </remarks>
    private void WireSweepEditor()
    {
        _sweepEditor = this.GetLogicalDescendants().OfType<TokenListEditor>().FirstOrDefault();
        _sweepEditor?.Configure(
            () => Settings.SweepProjects.ToList(),
            list =>
            {
                Settings.SweepProjects = list;
                Settings.Save();
                RefreshSummaries();
            },
            () => _service?.AllProjectNames ?? [],
            "自定义项目名");
    }

    /// <summary>
    /// 页面离开可视树时摘掉事件订阅。
    /// </summary>
    /// <remarks>
    /// 不摘的话，单例服务会一直挂着这个页面，反复开关设置窗口就会稳定地漏内存。
    /// </remarks>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_service is not null)
        {
            _service.ScheduleChanged -= _scheduleChangedHandler;
        }

        Settings.PropertyChanged -= _settingsChangedHandler;
        base.OnDetachedFromVisualTree(e);
    }

    #region 概览文字

    /// <summary>整屏动画颗粒的当前值。</summary>
    public string SweepCellText => $"{Settings.SweepCellSize:F0} px";

    public string RosterSummary
    {
        get
        {
            if (_service is null)
            {
                return "插件未就绪。";
            }

            var schedule = _service.Schedule;
            var groups = schedule.Groups.Count;
            var slots = schedule.Groups.Sum(g => g.Days.Values.Sum(d => d.Count));
            return $"{_service.RosterPath}\n共 {groups} 个批次、{slots} 个时间点。";
        }
    }

    public bool HasWarnings => _service?.Schedule.Warnings.Count > 0;

    public string WarningText => _service is null
        ? string.Empty
        : string.Join("\n", _service.Schedule.Warnings.Take(8));

    /// <summary>今天由谁值日、有几个时间点。</summary>
    public string TodaySummary
    {
        get
        {
            if (_service is null)
            {
                return string.Empty;
            }

            var today = DateTime.Now.Date;
            var group = _service.Schedule.GroupOn(today);
            var slots = _service.Schedule.SlotsOn(today);
            if (group is null || slots.Count == 0)
            {
                return "今天不值日。";
            }

            var next = slots.FirstOrDefault(x => x.Start >= DateTime.Now.TimeOfDay);
            var nextText = next is null
                ? "今天的时间点已全部过去。"
                : $"下一个：{next.RangeText}　{string.Join("、", next.Items.Select(i => $"{i.Project} {i.PeopleText}"))}";
            return $"{group.Name}　今天 {slots.Count} 个时间点。\n{nextText}";
        }
    }

    /// <summary>今天的时间点清单，表格用。</summary>
    public ObservableCollection<TodaySlotRow> TodaySlots { get; } = [];

    public string TodaySlotsSummary
    {
        get
        {
            if (_service is null)
            {
                return string.Empty;
            }

            var slots = _service.Schedule.SlotsOn(DateTime.Now.Date);
            if (slots.Count == 0)
            {
                return "今天没有时间点。";
            }

            var sweeps = slots.Count(x => Settings.IsSweep(x));
            var people = slots.SelectMany(x => x.Items).SelectMany(x => x.People)
                .Distinct(StringComparer.Ordinal).Count();
            return sweeps == 0
                ? $"共 {slots.Count} 个时间点、{people} 人，没有大扫除。"
                : $"共 {slots.Count} 个时间点、{people} 人，其中 {sweeps} 个是大扫除。";
        }
    }

    public bool TodayHasNoSlots => TodaySlots.Count == 0;

    /// <summary>
    /// 重建今天的时间点表格。
    /// </summary>
    /// <remarks>
    /// 表格用 <see cref="TodaySlotRow"/> 而不是直接绑 <see cref="DutySlot"/>：
    /// 判断一行是不是大扫除要看设置里的关键词，绑不上纯模型。
    /// 每行只放「项目」和「人名」两列文字，多个项目按行拆开更易读。
    /// <para/>
    /// 内容没变就不动集合：拖动任何一个滑块都会把整页的通知刷一遍，
    /// 每次都 Clear + Add 会让列表无谓地闪。
    /// </remarks>
    private void RebuildTodaySlots()
    {
        var rows = new List<TodaySlotRow>();
        if (_service is not null)
        {
            foreach (var slot in _service.Schedule.SlotsOn(DateTime.Now.Date))
            {
                foreach (var item in slot.Items)
                {
                    rows.Add(new TodaySlotRow(
                        slot.RangeText,
                        item.Project,
                        item.PeopleText,
                        Settings.IsSweep(slot) ? "是" : string.Empty));
                }
            }
        }

        var unchanged = rows.Count == TodaySlots.Count &&
                        rows.Select(RowKey).SequenceEqual(TodaySlots.Select(RowKey));
        if (!unchanged)
        {
            TodaySlots.Clear();
            foreach (var row in rows)
            {
                TodaySlots.Add(row);
            }

            Raise(nameof(TodayHasNoSlots));
        }
    }

    private static string RowKey(TodaySlotRow row) =>
        $"{row.TimeText}\u0001{row.ProjectText}\u0001{row.PeopleText}\u0001{row.SweepText}";

    #endregion

    #region 下拉框索引

    public int PlacementIndex
    {
        get => (int)Settings.Placement;
        set
        {
            if (value >= 0 && value != (int)Settings.Placement)
            {
                Settings.Placement = (PopupPlacement)value;
            }
        }
    }

    public int ScaleIndex
    {
        get => (int)Settings.Scale;
        set
        {
            if (value >= 0 && value != (int)Settings.Scale)
            {
                Settings.Scale = (PopupScale)value;
            }
        }
    }

    public int FrequentStyleIndex
    {
        get => (int)Settings.FrequentStyle;
        set
        {
            if (value >= 0 && value != (int)Settings.FrequentStyle)
            {
                Settings.FrequentStyle = (DutyStyle)value;
                RefreshSummaries();
            }
        }
    }

    public int SweepStyleIndex
    {
        get => (int)Settings.SweepStyle;
        set
        {
            if (value >= 0 && value != (int)Settings.SweepStyle)
            {
                Settings.SweepStyle = (DutyStyle)value;
                RefreshSummaries();
            }
        }
    }

    #endregion

    #region 大扫除关键词

    /// <summary>按当前关键词，今天有几个时间点会被判成大扫除。用来自证设置是对的。</summary>
    public string SweepSummary
    {
        get
        {
            const string hint = "点击下方选择。";
            if (_service is null)
            {
                return hint;
            }

            var slots = _service.Schedule.SlotsOn(DateTime.Now.Date);
            var sweeps = slots.Where(Settings.IsSweep).ToList();
            if (slots.Count == 0)
            {
                return hint + "　今天不值日。";
            }

            return sweeps.Count == 0
                ? hint + $"　今天 {slots.Count} 个时间点，无大扫除。"
                : hint + $"　今天 {slots.Count} 个时间点，其中 {sweeps.Count} 个为大扫除："
                       + string.Join("、", sweeps.Select(x => x.RangeText));
        }
    }

    #endregion

    private void RefreshSummaries()
    {
        RebuildTodaySlots();

        // 大扫除关键词从别处变了也要跟着重建标签块，否则页面上还是旧的那组。
        _sweepEditor?.Rebuild();

        foreach (var name in new[]
                 {
                     nameof(RosterSummary), nameof(TodaySummary), nameof(HasWarnings),
                     nameof(WarningText), nameof(SweepSummary), nameof(SweepCellText),
                     nameof(TodaySlotsSummary), nameof(TodayHasNoSlots),
                     // A20：这四个 ComboBox 的下拉项是死的，索引属性不会自己发通知。
                     nameof(PlacementIndex), nameof(ScaleIndex),
                     nameof(FrequentStyleIndex), nameof(SweepStyleIndex)
                 })
        {
            Raise(name);
        }
    }

    private void OnOpenRoster(object? sender, RoutedEventArgs e)
    {
        if (_service is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_service.RosterPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 没有关联程序就算了，路径就写在上面。
        }
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        _service?.Reload();
        RefreshSummaries();
    }

    private void OnPreviewCard(object? sender, RoutedEventArgs e) => _service?.PreviewNow(fullscreen: false);

    private void OnPreviewSweep(object? sender, RoutedEventArgs e) => _service?.PreviewNow(fullscreen: true);

    #region 变更通知

    private PropertyChangedEventHandler? _propertyChangedHandlers;

    /// <summary>
    /// 显式实现 <see cref="INotifyPropertyChanged"/>。
    /// </summary>
    /// <remarks>
    /// <b>为什么不用 <c>public new event PropertyChangedEventHandler? PropertyChanged;</c></b>
    /// （旧写法）：那种写法靠「类声明里重列接口」触发 C# 的接口重实现才能工作，
    /// 谁把类声明上的接口当成多余删掉，本页所有概览文字就会静默冻结。
    /// 改成显式实现后，「有没有重列接口」这件事仍然必须成立，但至少不会再被 <c>new</c> 误导。
    /// <para/>
    /// 也不要改成 <c>OnPropertyChanged(nameof(X))</c>：<c>AvaloniaObject</c> 只有
    /// <c>OnPropertyChanged(AvaloniaPropertyChangedEventArgs)</c> 一个重载，
    /// 传字符串会 CS1503 编译不过。
    /// </remarks>
    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => _propertyChangedHandlers += value;
        remove => _propertyChangedHandlers -= value;
    }

    private void Raise(string name) =>
        _propertyChangedHandlers?.Invoke(this, new PropertyChangedEventArgs(name));

    #endregion
}
