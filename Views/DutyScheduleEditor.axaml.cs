using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ClassIsland.DutyRoster.Models;
using ClassIsland.DutyRoster.Services;
using ClassIsland.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace ClassIsland.DutyRoster.Views;

/// <summary>
/// 值日表设置里的「配置」那半：可视化编辑批次、星期和时间点。
/// </summary>
/// <remarks>
/// 三条硬约束，都来自实测：
/// <list type="number">
/// <item>
/// <b>载入时按天展开成独立副本。</b>解析器把同一个 <see cref="DutySlot"/> 实例挂到多个星期下面，
/// 直接改会「改周一、动周二到周五」——用户会当成严重 bug。
/// </item>
/// <item>
/// <b>保存时按内容合并回星期范围。</b>不合并的话，用户原本 2 行的
/// <c>周一~周五</c> 会被写成 5 段，文件膨胀约 3 倍。
/// </item>
/// <item>
/// <b>只在用户点「保存到文件」时写盘。</b>写回不受文件监视和设置项变更触发，
/// 否则会和「用记事本改文件」形成写回循环。
/// </item>
/// </list>
/// </remarks>
public partial class DutyScheduleEditor : UserControl, INotifyPropertyChanged
{
    private readonly DutyRosterService? _service;

    /// <summary>全部批次的编辑副本。批次名保持原样，保存时顺序不变。</summary>
    private readonly List<GroupEdit> _groups = [];

    private readonly List<DayOfWeek> _selectedDays = [];

    private bool _loading;
    private bool _suppressSelectionHandling;

    /// <summary>界面上的这份时间点被用户改过没有。</summary>
    /// <remarks>
    /// 换批次、改勾选时只有改过才回写。无条件回写会把「本来有内容、但这次没在编辑的那几天」
    /// 用空白覆盖掉——用户只是点了一下别的批次，回来就发现自己周五的排班没了。
    /// </remarks>
    private bool _visibleDirty;

    /// <summary>整张表相对磁盘上的内容有没有改动。用来避免外部变更冲掉正在编辑的东西。</summary>
    private bool _dirty;

    public DutyScheduleEditor()
    {
        _service = IAppHost.Host?.Services.GetService<DutyRosterService>();

        // DayOptions / Slots / GroupNames 都是字段初始化器里就建好的空集合，
        // 所以下面设 DataContext 时绑定读到的不会是 null。
        // 反过来（先设 DataContext、后赋值）的话，这几个只读属性不会自己发变更通知，
        // 绑定会永远停在 null 上——表现是星期复选框一个都不出现，而且不报错。
        InitializeComponent();
        DataContext = this;

        Slots.CollectionChanged += OnSlotsChanged;

        foreach (var (day, name) in new (DayOfWeek, string)[]
                 {
                     (DayOfWeek.Monday, "周一"), (DayOfWeek.Tuesday, "周二"),
                     (DayOfWeek.Wednesday, "周三"), (DayOfWeek.Thursday, "周四"),
                     (DayOfWeek.Friday, "周五"), (DayOfWeek.Saturday, "周六"),
                     (DayOfWeek.Sunday, "周日")
                 })
        {
            DayOptions.Add(NewDay(day, name));
        }

        Reload();

        if (_service is not null)
        {
            _service.ScheduleChanged += OnServiceScheduleChanged;
        }
    }

    private DutyDayOption NewDay(DayOfWeek day, string name)
    {
        var option = new DutyDayOption(day, name);
        option.SelectionChanged += OnDaySelectionChanged;
        return option;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>页面离开可视树时摘掉订阅，免得单例服务一直挂着这个控件。</summary>
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_service is not null)
        {
            _service.ScheduleChanged -= OnServiceScheduleChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// 重新从磁盘载入前调用，丢掉未保存的编辑。
    /// </summary>
    public void DiscardChanges() => Reload();

    #region 绑定属性

    /// <summary>星期一 ~ 星期日。构造时就把七项都填好，绑定时才拿得到。</summary>
    public ObservableCollection<DutyDayOption> DayOptions { get; } = [];

    /// <summary>当前正在编辑的时间点。它是「已勾选的这几天」共用的一份。</summary>
    public ObservableCollection<DutySlotRow> Slots { get; } = [];

    public ObservableCollection<string> GroupNames { get; } = [];

    private int _groupIndex;

    /// <summary>当前编辑第几个批次。</summary>
    public int GroupIndex
    {
        get => _groupIndex;
        set
        {
            if (value == _groupIndex || value < 0 || value >= _groups.Count)
            {
                return;
            }

            // 切批次前先把当前编辑结果落到已经勾选的星期上，否则这些改动会丢。
            CommitVisibleSlots();
            _groupIndex = value;
            Raise(nameof(GroupIndex));
            // 换批次后重新挑一天：上一个批次勾的那天在这个批次里可能根本没有排班，
            // 保留勾选会让用户看到一个空白列表，以为新批次是空的。
            ApplyGroupToUi(preserveSelection: false);
        }
    }

    /// <summary>勾选状态的说明文字。</summary>
    public string SelectionHint
    {
        get
        {
            if (_selectedDays.Count == 0)
            {
                return "还没勾选任何一天。先勾一天，再在下面加时间点。";
            }

            return $"当前编辑：{DutyRosterWriter.DescribeDays(_selectedDays)}（共 {_selectedDays.Count} 天）。这些天用的是同一份时间点，改一处就一起改。";
        }
    }

    /// <summary>没有任何可编辑的行时给一句提示，免得看着像坏了。</summary>
    public bool HasNoSlots => Slots.Count == 0;

    public bool HasValidationError => _validationError is { Length: > 0 };

    private string? _validationError;

    public string ValidationText => _validationError ?? string.Empty;

    /// <summary>存在格式问题时不给保存，避免把半成品写进文件。</summary>
    public bool CanSave => !HasValidationError && _selectedDays.Count > 0 && _groups.Count > 0;

    public string RosterPathText => _service is null ? "插件未就绪。" : _service.RosterPath;

    private bool _isConfirmingSave;

    /// <summary>「保存会重写整个文件」的确认条是否展开。</summary>
    public bool IsConfirmingSave
    {
        get => _isConfirmingSave;
        private set
        {
            if (_isConfirmingSave == value)
            {
                return;
            }

            _isConfirmingSave = value;
            Raise(nameof(IsConfirmingSave));
        }
    }

    private string? _saveResultText;

    public bool HasSaveResult => _saveResultText is { Length: > 0 };

    public string SaveResultText => _saveResultText ?? string.Empty;

    private string? _saveErrorText;

    public bool HasSaveError => _saveErrorText is { Length: > 0 };

    public string SaveErrorText => _saveErrorText ?? string.Empty;

    #endregion

    #region 载入

    /// <summary>
    /// 从当前解析出来的值日表重建编辑状态。
    /// </summary>
    /// <remarks>
    /// 按天复制成独立的 <see cref="DutySlotRow"/>，绕开解析器共享实例的坑。
    /// </remarks>
    public void Reload()
    {
        if (_service is null)
        {
            return;
        }

        _loading = true;
        try
        {
            var schedule = _service.Schedule;
            _groups.Clear();

            if (schedule.Groups.Count == 0)
            {
                _groups.Add(new GroupEdit("第1批"));
            }
            else
            {
                foreach (var group in schedule.Groups)
                {
                    var edit = new GroupEdit(group.Name);
                    foreach (var day in group.SkipDays)
                    {
                        edit.SkipDays.Add(day);
                    }

                    foreach (var (day, slots) in group.Days)
                    {
                        edit.Days[day] = slots.Select(DutySlotRow.From).ToList();
                    }

                    _groups.Add(edit);
                }
            }

            GroupNames.Clear();
            foreach (var group in _groups)
            {
                GroupNames.Add(group.Name);
            }

            _groupIndex = 0;
            Raise(nameof(GroupIndex));
            Raise(nameof(GroupNames));

            ApplyGroupToUi(preserveSelection: false);
        }
        finally
        {
            _loading = false;
        }

        _dirty = false;
        _visibleDirty = false;
        ClearSaveMessages();
    }

    private void OnServiceScheduleChanged(object? sender, EventArgs e)
    {
        // 用户正在编辑时不要把他刚打的字冲掉——只有没在改的时候才跟着外部变更走。
        if (HasPendingChanges())
        {
            return;
        }

        Reload();
    }

    /// <summary>
    /// 把当前批次的勾选星期与时间点铺到界面上。
    /// </summary>
    private void ApplyGroupToUi(bool preserveSelection = true)
    {
        if (_groups.Count == 0)
        {
            return;
        }

        var group = _groups[Math.Clamp(_groupIndex, 0, _groups.Count - 1)];

        _suppressSelectionHandling = true;
        try
        {
            if (!preserveSelection || _selectedDays.Count == 0)
            {
                // 默认勾上最早一个有内容的那天；都没有就勾周一，省得用户面对一个空白页。
                // 必须用可空来判「没找到」：DayOfWeek.Sunday 的值是 0，
                // 也就是 default(DayOfWeek)——拿 == default 当哨兵的话，
                // 只有周日有排班的表会被误判成「没有内容」，从而默认勾上周一。
                DayOfWeek? firstWithContent = WeekOrder
                    .Cast<DayOfWeek?>()
                    .FirstOrDefault(d => group.Days.TryGetValue(d!.Value, out var list) && list.Count > 0);
                _selectedDays.Clear();
                _selectedDays.Add(firstWithContent ?? DayOfWeek.Monday);
            }

            foreach (var option in DayOptions)
            {
                option.IsSelected = _selectedDays.Contains(option.Day);
            }
        }
        finally
        {
            _suppressSelectionHandling = false;
        }

        LoadSlotsFromSelection();
        Raise(nameof(SelectionHint));
    }

    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    private GroupEdit? CurrentGroup =>
        _groups.Count == 0 ? null : _groups[Math.Clamp(_groupIndex, 0, _groups.Count - 1)];

    /// <summary>把界面上这份时间点换成「已勾选的第一天有内容的那天」的内容。</summary>
    private void LoadSlotsFromSelection()
    {
        var group = CurrentGroup;
        Slots.Clear();
        if (group is null || _selectedDays.Count == 0)
        {
            RaiseSlotState();
            return;
        }

        var source = WeekOrder
            .Where(d => _selectedDays.Contains(d))
            .Select(d => group.Days.TryGetValue(d, out var list) ? list : null)
            .FirstOrDefault(list => list is { Count: > 0 });

        foreach (var row in source ?? [])
        {
            // 订阅的是放进界面的那一份副本，不是源对象——用户改的是副本，
            // 订错了对象的话改了也不会重新校验，保存按钮的状态就不跟手。
            var copy = row.Clone();
            copy.ValidationChanged += OnRowValidationChanged;
            Slots.Add(copy);
        }

        RaiseSlotState();
    }

    /// <summary>把界面上这份时间点写回「已勾选的每一天」。</summary>
    /// <remarks>
    /// 只在 <see cref="_visibleDirty"/> 为真时调用，见它的注释。
    /// </remarks>
    private void CommitVisibleSlots()
    {
        var group = CurrentGroup;
        if (!_visibleDirty || group is null || _selectedDays.Count == 0)
        {
            return;
        }

        foreach (var day in _selectedDays)
        {
            group.Days[day] = Slots.Select(x => x.Clone()).ToList();
        }

        _visibleDirty = false;
    }

    private void OnDaySelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressSelectionHandling || _loading)
        {
            return;
        }

        // 勾选变化时：先把当前编辑结果落到「变之前勾着的那几天」，再换成新选中那天的内容。
        CommitVisibleSlots();

        // 取消勾选、且本来就没内容的那天，把键删掉，免得写文件时留一行空的星期。
        var group = CurrentGroup;
        if (group is not null)
        {
            foreach (var day in _selectedDays)
            {
                if (group.Days.TryGetValue(day, out var list) && list.Count == 0)
                {
                    group.Days.Remove(day);
                }
            }
        }

        _suppressSelectionHandling = true;
        try
        {
            _selectedDays.Clear();
            foreach (var option in DayOptions)
            {
                if (option.IsSelected)
                {
                    _selectedDays.Add(option.Day);
                }
            }

            _selectedDays.Sort((a, b) => Array.IndexOf(WeekOrder, a).CompareTo(Array.IndexOf(WeekOrder, b)));
        }
        finally
        {
            _suppressSelectionHandling = false;
        }

        LoadSlotsFromSelection();

        // 新勾上的天要立刻拿到界面上这份内容。不然用户「勾上周二 → 直接点保存」时，
        // 因为这次没有编辑、脏标记还是 false，周二会被静默漏掉。
        var current = CurrentGroup;
        if (current is not null && Slots.Count > 0)
        {
            var added = false;
            foreach (var day in _selectedDays)
            {
                if (!current.Days.ContainsKey(day))
                {
                    current.Days[day] = Slots.Select(x => x.Clone()).ToList();
                    added = true;
                }
            }

            if (added)
            {
                _dirty = true;
            }
        }

        Raise(nameof(SelectionHint));
    }

    #endregion

    #region 时间点增删与校验

    private void OnAddSlot(object? sender, RoutedEventArgs e)
    {
        if (_selectedDays.Count == 0)
        {
            SetValidationError("先勾选至少一天，再添加时间点。");
            return;
        }

        var row = new DutySlotRow();
        // 上一行有开始时间就顺手加一小时，省得每次都从头选。
        if (Slots.LastOrDefault()?.StartTime is { } last)
        {
            row.StartTime = last.Add(TimeSpan.FromHours(1));
        }

        MarkEdited();
        row.ValidationChanged += OnRowValidationChanged;
        Slots.Add(row);
        Validate();
    }

    private void OnRemoveSlot(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DutySlotRow row })
        {
            MarkEdited();
            row.ValidationChanged -= OnRowValidationChanged;
            Slots.Remove(row);
            Validate();
        }
    }

    private void OnSlotsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseSlotState();

    private void OnRowValidationChanged(object? sender, EventArgs e)
    {
        // 行里任何一个字段被改都会走到这里：既重新校验，也把「改过了」记下来，
        // 这样换批次/换勾选时才知道该不该把界面上的内容回写。
        MarkEdited();
        Validate();
    }

    /// <summary>记下「有未保存的改动」。</summary>
    private void MarkEdited()
    {
        if (_loading)
        {
            return;
        }

        _visibleDirty = true;
        _dirty = true;
    }

    private void RaiseSlotState()
    {
        Raise(nameof(HasNoSlots));
        Validate();
    }

    /// <summary>把「能不能保存」重算一遍，顺便把第一条问题当提示文字。</summary>
    private void Validate()
    {
        string? error = null;
        if (_selectedDays.Count == 0)
        {
            error = Slots.Count > 0 ? "先勾选这些时间点属于哪几天。" : null;
        }
        else
        {
            foreach (var row in Slots)
            {
                _ = row.ToSlot(out var rowError);
                if (rowError is not null)
                {
                    error = rowError;
                    break;
                }
            }
        }

        _validationError = error;
        Raise(nameof(HasValidationError));
        Raise(nameof(ValidationText));
        Raise(nameof(CanSave));
    }

    private void SetValidationError(string message)
    {
        _validationError = message;
        Raise(nameof(HasValidationError));
        Raise(nameof(ValidationText));
        Raise(nameof(CanSave));
    }

    #endregion

    #region 保存

    private void OnRequestSave(object? sender, RoutedEventArgs e)
    {
        ClearSaveMessages();
        Validate();
        if (!CanSave)
        {
            SetValidationError(_validationError ?? "有地方还没填对，先改好再保存。");
            return;
        }

        // 只展开确认条，不直接写盘：保存会重写整个文件，必须先让用户知道。
        IsConfirmingSave = true;
    }

    private void OnCancelSave(object? sender, RoutedEventArgs e) => IsConfirmingSave = false;

    private void OnConfirmSave(object? sender, RoutedEventArgs e)
    {
        IsConfirmingSave = false;
        if (_service is null)
        {
            ShowSaveError("插件未就绪，没法保存。");
            return;
        }

        Validate();
        if (!CanSave)
        {
            return;
        }

        CommitVisibleSlots();

        var schedule = BuildSchedule();
        try
        {
            DutyRosterWriter.Save(_service.RosterPath, schedule);
        }
        catch (DutyRosterWriteException ex)
        {
            ShowSaveError(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ShowSaveError($"保存时出错：{ex.Message}");
            return;
        }

        // 写盘成功后再让服务重读一遍。此时文件监视也会被触发，但防抖之后读到的就是这份新内容。
        _service.Reload();
        Reload();
        ShowSaveResult($"已写入 {_service.RosterPath}，原文件已备份在 {_service.RosterPath}{DutyRosterWriter.BackupExtension}。");
    }

    private void OnReloadFromFile(object? sender, RoutedEventArgs e)
    {
        _service?.Reload();
        Reload();
        ClearSaveMessages();
        ShowSaveResult("已按磁盘上的值日表重新载入，未保存的修改被丢弃。");
    }

    /// <summary>
    /// 把编辑状态组装成一张完整的 <see cref="DutySchedule"/>。
    /// </summary>
    /// <remarks>
    /// 没被编辑到的批次、以及轮换设置都原样带过去——保存是「改一部分」，不是「重建整张表」。
    /// </remarks>
    private DutySchedule BuildSchedule()
    {
        var previous = _service?.Schedule ?? new DutySchedule();
        var schedule = new DutySchedule
        {
            RotationStart = previous.RotationStart,
            RotationPeriodDays = previous.RotationPeriodDays
        };

        foreach (var edit in _groups)
        {
            var group = new DutyGroup { Name = edit.Name };
            foreach (var day in edit.SkipDays)
            {
                group.SkipDays.Add(day);
            }

            foreach (var (day, rows) in edit.Days)
            {
                var slots = new List<DutySlot>();
                foreach (var row in rows)
                {
                    var slot = row.ToSlot(out _);
                    if (slot is not null)
                    {
                        slots.Add(slot);
                    }
                }

                if (slots.Count > 0)
                {
                    slots.Sort((a, b) => a.Start.CompareTo(b.Start));
                    group.Days[day] = slots;
                }
            }

            schedule.Groups.Add(group);
        }

        return schedule;
    }

    private void ClearSaveMessages()
    {
        _saveResultText = null;
        _saveErrorText = null;
        Raise(nameof(HasSaveResult));
        Raise(nameof(SaveResultText));
        Raise(nameof(HasSaveError));
        Raise(nameof(SaveErrorText));
    }

    private void ShowSaveResult(string text)
    {
        _saveResultText = text;
        _saveErrorText = null;
        Raise(nameof(HasSaveResult));
        Raise(nameof(SaveResultText));
        Raise(nameof(HasSaveError));
        Raise(nameof(SaveErrorText));
    }

    private void ShowSaveError(string text)
    {
        _saveErrorText = text;
        _saveResultText = null;
        Raise(nameof(HasSaveResult));
        Raise(nameof(SaveResultText));
        Raise(nameof(HasSaveError));
        Raise(nameof(SaveErrorText));
    }

    /// <summary>
    /// 界面上有没有还没落盘的改动。
    /// </summary>
    /// <remarks>
    /// 用来避免「外部改了文件 → 自动重载 → 冲掉用户正在打的字」。
    /// 直接看脏标记，<b>不要</b>在这里回写——那等于替用户做了保存，
    /// 还是在他看不见的时候。
    /// </remarks>
    private bool HasPendingChanges() => _dirty || _visibleDirty;

    #endregion

    #region 通知

    private PropertyChangedEventHandler? _propertyChangedHandlers;

    /// <summary>
    /// 显式实现。
    /// </summary>
    /// <remarks>
    /// 基类 <c>AvaloniaObject</c> 对 <see cref="INotifyPropertyChanged"/> 是显式实现，
    /// 这里必须用「重列接口 + 显式实现」这一对组合（两者缺一，XAML 绑定就收不到通知，
    /// 而且编译器不会报错）。不要改成 <c>new event</c>，也不要删掉类声明上的接口。
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

/// <summary>一个批次的编辑副本。</summary>
internal sealed class GroupEdit(string name)
{
    public string Name { get; } = name;

    public HashSet<DayOfWeek> SkipDays { get; } = [];

    public Dictionary<DayOfWeek, List<DutySlotRow>> Days { get; } = [];
}
