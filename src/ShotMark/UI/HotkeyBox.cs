using System.Windows.Controls;
using System.Windows.Input;
using ShotMark.Core;

namespace ShotMark.UI
{
    /// <summary>Read-only text box that records the next key combination the user presses.</summary>
    public sealed class HotkeyBox : TextBox
    {
        private HotkeySetting _hotkey = new HotkeySetting();

        public HotkeyBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            Cursor = Cursors.Hand;
            MinWidth = 210;
            SetResourceReference(StyleProperty, typeof(TextBox));
            Text = _hotkey.ToString();
        }

        public HotkeySetting Hotkey
        {
            get => _hotkey.Clone();
            set
            {
                _hotkey = value?.Clone() ?? new HotkeySetting();
                Text = _hotkey.ToString();
            }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            Text = "Pressione a combinação…";
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            Text = _hotkey.ToString();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            switch (key)
            {
                case Key.LeftCtrl: case Key.RightCtrl:
                case Key.LeftAlt: case Key.RightAlt:
                case Key.LeftShift: case Key.RightShift:
                case Key.LWin: case Key.RWin:
                    return; // wait for the real key
                case Key.Escape:
                    Keyboard.ClearFocus();
                    return;
                case Key.Back:
                case Key.Delete:
                    _hotkey = new HotkeySetting();
                    Keyboard.ClearFocus();
                    return;
                case Key.Tab:
                    e.Handled = false;
                    return;
            }

            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeySetting.ModControl;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeySetting.ModAlt;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeySetting.ModShift;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeySetting.ModWin;

            bool functionKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !functionKey)
            {
                Text = "Use Ctrl, Alt ou Shift + tecla";
                return;
            }

            _hotkey = new HotkeySetting(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
            Keyboard.ClearFocus();
        }
    }
}
