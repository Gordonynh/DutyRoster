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
/// <param name="Start">开始提醒的时间。</param>
/// <param name="End">
/// 结束时间。为 <c>null</c> 表示只有开始这一个时刻：提醒弹出来以后按「停留秒数」自己消失。
/// 有值时提醒会一直显示到这一刻。
/// </param>
/// <param name="Items">这个时间点要做的事。</param>
/// <remarks>
/// 名单里写 <c>08:40</c> 是老写法（单时刻），写 <c>08:40-08:50</c> 是新写法（时间段）。
/// <para/>
/// <b>必须保持不可变。</b>解析器会把同一个实例挂到多个星期下面
/// （<c>周一~周五</c> 是一份数据被五天共享），任何可写属性都会同时改掉五天。
/// </remarks>
public sealed record DutySlot(TimeSpan Start, TimeSpan? End, IReadOnlyList<DutyItem> Items)
{
    /// <summary>开始时间，比如 <c>08:40</c>。只关心「几点提醒」的地方用这个。</summary>
    public string TimeText => $"{Start:hh\\:mm}";

    /// <summary>这个时间点是不是一个时间段。</summary>
    public bool IsRange => End is { } end && end > Start;

    /// <summary>
    /// 显示用的时间文字：单时刻是 <c>08:40</c>，时间段是 <c>08:40-08:50</c>。
    /// </summary>
    public string RangeText => IsRange ? $"{Start:hh\\:mm}-{End!.Value:hh\\:mm}" : TimeText;

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
