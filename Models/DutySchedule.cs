using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.DutyRoster.Models;

/// <summary>一项值日工作：做什么、谁做。</summary>
/// <param name="Project">项目名，比如「擦黑板」。</param>
/// <param name="People">负责人，可以有多个。</param>
public sealed record DutyItem(string Project, IReadOnlyList<string> People)
{
    public string PeopleText => string.Join("、", People);
}

/// <summary>一个值日时间点。</summary>
/// <param name="Start">提醒时间。</param>
/// <param name="Items">这个时间点要做的事。</param>
/// <remarks>
/// 只有开始时间，没有结束时间——提醒就发生在这一个点上。
/// 名单里写成 <c>08:40-08:50</c> 也能解析，但后半截会被忽略：
/// 那是原来那份数据为了迁就别的软件才带上的，对提醒没有意义。
/// </remarks>
public sealed record DutySlot(TimeSpan Start, IReadOnlyList<DutyItem> Items)
{
    public string TimeText => $"{Start:hh\\:mm}";

    /// <summary>这个时间点一共涉及多少人（去重）。</summary>
    public int PeopleCount => Items.SelectMany(x => x.People).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>一个值日批次（比如「第1批」），按星期存排班。</summary>
public sealed class DutyGroup
{
    public required string Name { get; init; }

    /// <summary>不值日的星期。</summary>
    public HashSet<DayOfWeek> SkipDays { get; init; } = [];

    /// <summary>星期 → 该天的时段列表。</summary>
    public Dictionary<DayOfWeek, List<DutySlot>> Days { get; init; } = [];

    /// <summary>取某天的排班。这天被跳过或没配就返回空。</summary>
    public IReadOnlyList<DutySlot> SlotsOn(DayOfWeek day) =>
        SkipDays.Contains(day) || !Days.TryGetValue(day, out var slots) ? [] : slots;
}

/// <summary>
/// 整张值日表。
/// </summary>
public sealed class DutySchedule
{
    public List<DutyGroup> Groups { get; init; } = [];

    /// <summary>轮换起始日。多个批次时从这天起按 <see cref="RotationPeriodDays"/> 依次轮。</summary>
    public DateTime RotationStart { get; set; } = DateTime.Today;

    /// <summary>轮换周期（天）。</summary>
    public int RotationPeriodDays { get; set; } = 7;

    /// <summary>解析过程中遇到的问题，显示在设置页里，方便用户自己改对。</summary>
    public List<string> Warnings { get; init; } = [];

    public bool IsEmpty => Groups.Count == 0 || Groups.All(g => g.Days.Count == 0);

    /// <summary>
    /// 算出某一天该由哪个批次值日。
    /// </summary>
    /// <remarks>
    /// 只有一个批次时永远是它。多个批次时按整周期数取模——
    /// 也就是「从轮换起始日算起，每过一个周期换下一批，循环」。
    /// </remarks>
    public DutyGroup? GroupOn(DateTime date)
    {
        if (Groups.Count == 0)
        {
            return null;
        }

        if (Groups.Count == 1 || RotationPeriodDays <= 0)
        {
            return Groups[0];
        }

        var elapsed = (date.Date - RotationStart.Date).Days;
        // 起始日之前也要能算：C# 的 % 对负数返回负值，这里补一次回正。
        var periods = (int)Math.Floor(elapsed / (double)RotationPeriodDays);
        var index = ((periods % Groups.Count) + Groups.Count) % Groups.Count;
        return Groups[index];
    }

    /// <summary>取某天的全部时段。</summary>
    public IReadOnlyList<DutySlot> SlotsOn(DateTime date) =>
        GroupOn(date)?.SlotsOn(date.DayOfWeek) ?? [];
}
