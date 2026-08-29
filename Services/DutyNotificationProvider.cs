using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;

namespace ClassIsland.DutyRoster.Services;

/// <summary>
/// 值日提醒的 ClassIsland 提醒提供方。
/// </summary>
/// <remarks>
/// <c>INotificationHostService.ShowNotification</c> 是 internal 的，插件够不着；
/// 注册成一个提醒提供方就能拿到基类公开的 <c>ShowNotification</c>。
/// 注意这条通道**默认是关的**（见 <see cref="Models.DutySettings.AlsoSendClassIslandNotification"/>）：
/// 主界面那条太窄、一晃就过，值日提醒的主力是浮窗。
/// </remarks>
[NotificationProviderInfo("3F6C1D82-4A57-4E90-B1C3-8D2E5A70F419", "值日提醒", "",
    "到点在主界面上显示值日安排。默认关闭，主要提醒方式是浮窗。")]
public class DutyNotificationProvider : NotificationProviderBase
{
}
