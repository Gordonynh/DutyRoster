using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.DutyRoster.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.DutyRoster.Views;

/// <summary>编辑器里的一天。勾上就表示「下面的时间点这些天共用」。</summary>
public sealed partial class DutyDayOption : ObservableObject
{
    private bool _isSelected;

    public DutyDayOption(DayOfWeek day, string name)
    {
        Day = day;
        Name = name;
    }

    public DayOfWeek Day { get; }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                // 编辑器靠这个事件把「刚编辑的内容」先落到失去勾选的星期上，
                // 否则用户改勾选时，刚打的字就白打了。
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;
}

/// <summary>
/// 编辑器里的一行时间点。
/// </summary>
/// <remarks>
/// 它是从 <see cref="DutySlot"/> 投影出来的<b>可变</b>镜像。解析器会把同一个
/// <see cref="DutySlot"/> 实例挂到多个星期下面，直接改那个对象会「改周一、动周二到周五」；
/// 所以载入时按天各复制一份，编辑只碰这份副本，保存时才重新组装成 <see cref="DutySlot"/>。
/// <para/>
/// 这个类型刻意不依赖 Avalonia，纯逻辑就能单测——时间与项目/人名的拆分规则错一次，
/// 用户保存下去的就是错的数据。
/// </remarks>
public sealed partial class DutySlotRow : ObservableObject
{
    private TimeSpan? _startTime;
    private TimeSpan? _endTime;
    private string _project = string.Empty;
    private string _people = string.Empty;

    /// <summary>开始时间。必填。</summary>
    public TimeSpan? StartTime
    {
        get => _startTime;
        set
        {
            if (SetProperty(ref _startTime, value))
            {
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>结束时间。留空就是「只有开始这一个时刻」。</summary>
    public TimeSpan? EndTime
    {
        get => _endTime;
        set
        {
            if (SetProperty(ref _endTime, value))
            {
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>项目名，多个项目用分号隔开，比如 <c>擦黑板；倒垃圾</c>。</summary>
    public string Project
    {
        get => _project;
        set
        {
            if (SetProperty(ref _project, value))
            {
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>人名，和项目按分号一一对应，同一个项目里的多个人名用顿号或空格隔开。</summary>
    public string People
    {
        get => _people;
        set
        {
            if (SetProperty(ref _people, value))
            {
                ValidationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>任何一个字段变了都会触发，界面用它重新算「能不能保存」。</summary>
    public event EventHandler? ValidationChanged;

    /// <summary>从值日表里的一行复制出来。</summary>
    public static DutySlotRow From(DutySlot slot) => new()
    {
        _startTime = slot.Start,
        _endTime = slot.End,
        _project = string.Join("；", slot.Items.Select(x => x.Project)),
        _people = string.Join("；", slot.Items.Select(x => string.Join("、", x.People)))
    };

    /// <summary>复制一份，用于把当前编辑结果落到另外几天。</summary>
    public DutySlotRow Clone() => new()
    {
        _startTime = _startTime,
        _endTime = _endTime,
        _project = _project,
        _people = _people
    };

    /// <summary>
    /// 转成值日表里的那一行。格式不对时返回 <c>null</c>，并给出原因。
    /// </summary>
    public DutySlot? ToSlot(out string? error)
    {
        error = null;
        if (StartTime is not { } start)
        {
            error = "有一行没填开始时间。";
            return null;
        }

        if (EndTime is { } end && end <= start)
        {
            error = $"{start:hh\\:mm} 这一行的结束时间必须晚于开始时间（现在填的是 {end:hh\\:mm}）。";
            return null;
        }

        var items = BuildItems(Project, People, out var itemError);
        if (items.Count == 0)
        {
            error = itemError ?? "有一行既没填项目也没填人名。";
            return null;
        }

        return new DutySlot(start, EndTime, items);
    }

    /// <summary>
    /// 把「项目」框和「人名」框拼回一项项值日工作。
    /// </summary>
    /// <remarks>
    /// 两个框都用分号分隔，位置一一对应：<c>擦黑板；倒垃圾</c> 配
    /// <c>张三、李四；王五</c>，就是「擦黑板：张三、李四」和「倒垃圾：王五」。
    /// 只有一个项目时，人名框里写几个人都行。
    /// 项目比人名分组多时，多出来的项目复用第一组人名——比强行报错实用，
    /// 而且用户在界面上能直接看出结果不对。
    /// </remarks>
    private static List<DutyItem> BuildItems(string projectText, string peopleText, out string? error)
    {
        error = null;
        var projects = Split(projectText);
        var peopleGroups = Split(peopleText);

        if (projects.Count == 0)
        {
            error = peopleGroups.Count == 0 ? null : "只填了人名，没填项目。";
            return [];
        }

        if (peopleGroups.Count == 0)
        {
            error = $"「{projects[0]}」没填人名。";
            return [];
        }

        var result = new List<DutyItem>();
        for (var i = 0; i < projects.Count; i++)
        {
            var raw = i < peopleGroups.Count ? peopleGroups[i] : peopleGroups[0];
            var people = raw
                .Split([' ', '\t', '\u3000', '、', ',', '，', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (people.Count == 0)
            {
                error = $"「{projects[i]}」没填人名。";
                return [];
            }

            result.Add(new DutyItem(projects[i], people));
        }

        return result;
    }

    private static List<string> Split(string text) => (text ?? string.Empty)
        .Split([';', '；'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();
}
