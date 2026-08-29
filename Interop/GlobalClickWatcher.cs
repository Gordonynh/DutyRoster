using System;
using System.Runtime.InteropServices;

namespace ClassIsland.DutyRoster.Interop;

/// <summary>
/// 监听全屏范围内的鼠标点击，用来实现「点屏幕任意位置就关掉提醒」。
/// </summary>
/// <remarks>
/// 提醒浮窗只覆盖屏幕中间一小块（刻意的，铺满整屏会挡住别的东西），
/// 所以点在别处的鼠标消息根本到不了它。要听见这些点击，只能挂一个
/// 低级鼠标钩子 <c>WH_MOUSE_LL</c>。
/// <para/>
/// 这个钩子<b>只在提醒浮窗显示的那十几秒里存在</b>，窗口一关就卸掉；
/// 而且回调里无条件 <c>CallNextHookEx</c>，不吞任何一次点击——
/// 用户点的那一下照常送到它本来要去的窗口，只是顺带把提醒关掉。
/// </remarks>
internal sealed class GlobalClickWatcher : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private readonly Action _onClick;

    // 委托必须自己拿住引用：只传给非托管层的话会被 GC 回收，钩子随后就崩了。
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;

    public GlobalClickWatcher(Action onClick)
    {
        _onClick = onClick;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _hook != IntPtr.Zero)
        {
            return;
        }

        try
        {
            // WH_MOUSE_LL 是全局钩子，模块句柄传 0、线程 ID 传 0 即可。
            _hook = SetWindowsHookEx(WhMouseLl, _proc, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // 挂不上就算了，浮窗还有自动消失和点自身关闭两条路。
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown)
            {
                try
                {
                    _onClick();
                }
                catch (Exception)
                {
                    // 回调里绝对不能抛：异常穿回非托管钩子链会把整个消息循环带崩。
                }
            }
        }

        // 永远放行，不吞点击。
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        try
        {
            UnhookWindowsHookEx(_hook);
        }
        catch (Exception)
        {
            // 卸不掉也没别的办法了。
        }

        _hook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
