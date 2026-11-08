using System;
using System.Runtime.InteropServices;

namespace ClassIsland.DutyRoster.Interop;

/// <summary>
/// 监听全屏范围内的按键，用来实现「按 Esc 就能关掉提醒」这条兜底退路。
/// </summary>
/// <remarks>
/// 为什么不能用窗口自己的 <c>KeyDown</c>：卡片浮窗是 <c>ShowActivated = false</c>
/// 且带 <c>WS_EX_NOACTIVATE</c> 的，它<b>永远拿不到键盘焦点</b>，
/// 挂在它身上的键盘事件一次都不会触发。要让 Esc 在这种情况下依然有效，
/// 只能挂一个低级键盘钩子 <c>WH_KEYBOARD_LL</c>。
/// <para/>
/// 纪律和 <see cref="GlobalClickWatcher"/> 完全一致：只关心 Esc 一个键、
/// 回调里无条件 <c>CallNextHookEx</c> 不吞任何按键、委托自己持引用防 GC、
/// <c>Dispose</c> 幂等、挂不上就静默降级（提醒还有别的方式能关）。
/// </remarks>
internal sealed class GlobalKeyWatcher : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkEscape = 0x1B;

    private readonly Action _onEscape;

    // 委托必须自己拿住引用：只传给非托管层的话会被 GC 回收，钩子随后就崩了。
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public GlobalKeyWatcher(Action onEscape)
    {
        _onEscape = onEscape;
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
            // WH_KEYBOARD_LL 是全局钩子，模块句柄传 0、线程 ID 传 0 即可。
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, IntPtr.Zero, 0);
        }
        catch (Exception)
        {
            // 挂不上就算了，整屏窗口还有自己的按键路径。
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = (int)wParam;
            if (message is WmKeyDown or WmSysKeyDown && IsEscape(lParam))
            {
                try
                {
                    _onEscape();
                }
                catch (Exception)
                {
                    // 回调里绝对不能抛：异常穿回非托管钩子链会把整个消息循环带崩。
                }
            }
        }

        // 永远放行，不吞按键。
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsEscape(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        return info.VirtualKeyCode == VkEscape;
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

    /// <summary><c>KBDLLHOOKSTRUCT</c> 的头两个字段。只用到虚拟键码，后面用不着。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
