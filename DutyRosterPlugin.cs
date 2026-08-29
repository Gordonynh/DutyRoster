using System;
using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.DutyRoster.Models;
using ClassIsland.DutyRoster.Services;
using ClassIsland.DutyRoster.Views;
using ClassIsland.UltraCodeShared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.DutyRoster;

/// <summary>
/// 值日提醒插件入口。
/// </summary>
public class DutyRosterPlugin : PluginBase
{
    private static readonly Assembly SelfAssembly = typeof(DutyRosterPlugin).Assembly;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        EnsureAssemblyResolvable();

        DutySettings.Initialize(PluginConfigFolder);

        // 浮窗用的是和 UltraCode 插件同一套像素场（按源码共享）。
        // 调参读 UltraCode 自己的 options.json，这样用户在那边拖滑块，这里跟着变。
        // UltraCode 没装也不影响，JsonTuningSource 会退回出厂默认值。
        UltraCodeTuning.Current = JsonTuningSource.FromSiblingOf(PluginConfigFolder);

        // 可选的 ClassIsland 提醒通道。默认不用，但要先注册好，用户在设置里一开就能用。
        services.AddNotificationProvider<DutyNotificationProvider>();

        var configFolder = PluginConfigFolder;
        services.AddSingleton(_ => new DutyRosterService(configFolder));
        services.AddHostedService(sp => sp.GetRequiredService<DutyRosterService>());

        services.AddSettingsPage<DutySettingsPage>();
    }

    /// <summary>
    /// 让 <c>avares://ClassIsland.DutyRoster/...</c> 能被解析到。
    /// </summary>
    /// <remarks>
    /// Avalonia 的资源加载器是用 <see cref="Assembly.Load(AssemblyName)"/> 按名字找程序集的，
    /// 走的是默认 <see cref="AssemblyLoadContext"/>；而插件是被宿主用独立的 PluginLoadContext 加载的，
    /// 默认上下文里没有它。这里挂个解析回调把本程序集交回去。
    /// </remarks>
    private static void EnsureAssemblyResolvable()
    {
        var selfName = SelfAssembly.GetName().Name;
        AssemblyLoadContext.Default.Resolving += (_, requested) =>
            requested.Name == selfName ? SelfAssembly : null;
    }
}
