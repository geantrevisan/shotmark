using System;
using System.Collections.Generic;
using System.Windows.Interop;
using ShotMark.Native;

namespace ShotMark.Capture
{
    /// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        private int _nextId = 1;

        public HotkeyManager()
        {
            var p = new HwndSourceParameters("ShotMarkHotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = NativeMethods.HWND_MESSAGE,
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
        }

        /// <returns>false if another application already owns the combination.</returns>
        public bool Register(uint modifiers, uint virtualKey, Action action)
        {
            int id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
                return false;
            _actions[id] = action;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
