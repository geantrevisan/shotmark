using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ShotMark.Native;

namespace ShotMark.Capture
{
    public enum PrintScreenAction
    {
        Region,
        ActiveWindow,
        FullScreen,
    }

    /// <summary>
    /// Takes over the Print Screen key system-wide with a low-level keyboard hook, so it works in
    /// every application even when another tool (Windows Snipping Tool, other capture apps) has
    /// registered it as a hotkey. Win+PrtSc and Ctrl+PrtSc are left to Windows.
    /// </summary>
    public sealed class PrintScreenHook : IDisposable
    {
        private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep a reference: the GC must not collect the callback
        private readonly Dispatcher _dispatcher;
        private readonly Action<PrintScreenAction> _onPress;
        private IntPtr _hook;

        public PrintScreenHook(Dispatcher dispatcher, Action<PrintScreenAction> onPress)
        {
            _dispatcher = dispatcher;
            _onPress = onPress;
            _proc = Callback;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (k.vkCode == NativeMethods.VK_SNAPSHOT)
                {
                    bool ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
                    bool win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);
                    if (!ctrl && !win)
                    {
                        int msg = wParam.ToInt32();
                        if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                        {
                            bool alt = (k.flags & NativeMethods.LLKHF_ALTDOWN) != 0;
                            bool shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
                            var action = alt ? PrintScreenAction.ActiveWindow
                                       : shift ? PrintScreenAction.FullScreen
                                       : PrintScreenAction.Region;
                            // Never do real work inside the hook: Windows drops slow hooks.
                            _dispatcher.BeginInvoke(new Action(() => _onPress(action)));
                        }
                        return new IntPtr(1); // swallow both key down and key up
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
