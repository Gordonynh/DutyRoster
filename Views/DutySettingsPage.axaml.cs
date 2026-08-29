using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.DutyRoster.Models;
using ClassIsland.DutyRoster.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.DutyRoster.Views;

/// <summary>
/// 值日提醒的设置页。
/// </summary>
[SettingsPageInfo("gordon.dutyroster", "值日提醒", "", "")]
public partial class DutySettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly DutyRosterService? _service;

    public DutySettings Settings => DutySettings.Current;

    public DutySettingsPage()
    {
        _service = IAppHost.Host?.Services.GetService<DutyRosterService>();
        DataContext = this;
        InitializeComponent();

        if (_service is not null)
        {
            _service.ScheduleChanged += (_, _) => RefreshSummaries();
        }

        // 设置里任何一项变了都把派生的显示文字重算一遍。
        // 不挂这个的话，拖完滑块旁边那个数字不会变——绑定的是本页的只读属性，
        // 而本页从来没为它发过变更通知。
        Settings.PropertyChanged += (_, _) => RefreshSummaries();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        this.FindControl<ClassIsland.PluginShared.TokenListEditor>("SweepProjectsEditor")?.Configure(
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

    #region 概览文字

    /// <summary>值日表文件的概览：路径 + 批次数。</summary>
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
                : $"下一个：{next.TimeText}　{string.Join("、", next.Items.Select(i => $"{i.Project} {i.PeopleText}"))}";
            return $"{group.Name}　今天 {slots.Count} 个时间点。\n{nextText}";
        }
    }

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
                       + string.Join("、", sweeps.Select(x => x.TimeText));
        }
    }

    #endregion

    private void RefreshSummaries()
    {
        foreach (var name in new[]
                 {
                     nameof(RosterSummary), nameof(TodaySummary), nameof(HasWarnings),
                     nameof(WarningText), nameof(SweepSummary), nameof(SweepCellText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

    public new event PropertyChangedEventHandler? PropertyChanged;
}
