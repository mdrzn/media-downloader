// Appunti via Win32, senza polling: una finestra nascosta riceve WM_CLIPBOARDUPDATE (e WM_HOTKEY).
// Qui servono solo per riconoscere i link copiati.
using System.Runtime.InteropServices;

namespace MediaDownloader;

// Finestra message-only: AddClipboardFormatListener + RegisterHotKey
sealed class MessageWindow : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312, HotkeyId = 1;
    public event Action? ClipboardChanged;
    public event Action? HotkeyPressed;

    public MessageWindow()
    {
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
        if (!AddClipboardFormatListener(Handle))
            Log.Error($"AddClipboardFormatListener failed ({Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) ClipboardChanged?.Invoke();
        else if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId) HotkeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    // "" toglie la scorciatoia; false se il testo non e' valido o un'altra app la usa gia'
    public bool SetHotkey(string text)
    {
        UnregisterHotKey(Handle, HotkeyId);
        if (text == "") return true;
        return Hotkeys.TryParse(text, out var mods, out var key)
               && RegisterHotKey(Handle, HotkeyId, mods | 0x4000 /* MOD_NOREPEAT */, (uint)key);
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

static class Clip
{
    const uint CF_UNICODETEXT = 13;
    public static uint Sequence => GetClipboardSequenceNumber();

    // Il testo negli appunti (null se non c'e' o se gli appunti sono occupati)
    public static string? ReadText(IntPtr owner)
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        for (int i = 0; i < 10 && !OpenClipboard(owner); i++) Thread.Sleep(25);
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
}

// Scorciatoie come testo: "Ctrl+Alt+U"
static class Hotkeys
{
    public static bool Valid(Keys modifiers, Keys key) =>
        (modifiers & (Keys.Control | Keys.Alt)) != 0
        && key is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None);

    public static string Format(Keys modifiers, Keys key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((char)key).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        foreach (var m in parts[..^1])
            modifiers |= m.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 0x2u,
                "alt" => 0x1u,
                "shift" => 0x4u,
                "win" => 0x8u,
                _ => 0x10000u,
            };
        var last = parts[^1];
        if (last.Length == 1 && char.IsAsciiDigit(last[0])) key = Keys.D0 + (last[0] - '0');
        else if (!Enum.TryParse(last, true, out key)) return false;
        return modifiers is > 0 and < 0x10000;
    }
}
