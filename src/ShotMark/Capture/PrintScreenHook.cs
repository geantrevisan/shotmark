using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
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
    ///
    /// The hook lives on its own thread with its own message loop. Low-level hooks are called on
    /// the thread that installed them, so if that were the UI thread, any slow work there (saving a
    /// big capture, OCR…) would delay every key press in Windows, and Windows silently removes a
    /// hook that keeps timing out — Print Screen would stop working until ShotMark restarted.
    /// </summary>
    public sealed class PrintScreenHook : IDisposable
    {
        private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep a reference: the GC must not collect the callback
        private readonly Dispatcher _dispatcher;
        private readonly Action<PrintScreenAction> _onPress;
        private readonly Thread _thread;
        private IntPtr _hook;
        private uint _threadId;
        private bool _disposed;

        public PrintScreenHook(Dispatcher dispatcher, Action<PrintScreenAction> onPress)
        {
            _dispatcher = dispatcher;
            _onPress = onPress;
            _proc = Callback;

            int error = 0;
            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                _threadId = NativeMethods.GetCurrentThreadId();
                _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
                ready.Set();
                if (_hook == IntPtr.Zero) return;

                // The hook is only called while this thread pumps messages.
                while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            })
            {
                IsBackground = true,
                Name = "ShotMark Print Screen hook",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            ready.Wait();
            if (error != 0 || _hook == IntPtr.Zero) throw new Win32Exception(error);
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
                            // Never do real work inside the hook: hand it to the UI thread and return at once.
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
            if (_disposed) return;
            _disposed = true;
            if (_threadId != 0)
            {
                NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(1000);
            }
        }
    }
}
