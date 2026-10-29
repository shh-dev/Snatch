using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Snatch;

/// <summary>
/// Espanso식 텍스트 자동 바꾸기. 어디서든 단축어(예: shh)를 치면 지우고 저장된 문장을 대신 입력.
/// 전역 저수준 키보드 훅으로 영문/숫자 키만 따라가고, 바꿀 땐 클립보드 없이 SendInput 유니코드로 직접 입력(히스토리 오염 없음).
/// shortcut: 단축어는 영문·숫자만, 한글 입력모드에선 동작 안 함 — 한글 단축어가 필요해지면 ToUnicodeEx+IME 조합 추적으로 확장.
/// </summary>
public sealed class TextExpander : IDisposable
{
    private readonly Func<IReadOnlyList<Snippet>> _snippets;
    private readonly Action<Action> _post;
    private readonly LowLevelKeyboardProc _proc;   // GC 방지용 참조 유지
    private IntPtr _hook;
    private readonly StringBuilder _buf = new();
    private IntPtr _bufWindow;
    private bool _ownWindow;

    public TextExpander(Func<IReadOnlyList<Snippet>> snippets, Action<Action> post)
    {
        _snippets = snippets;
        _post = post;
        SelfCheck();
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName), 0);
    }

    [Conditional("DEBUG")]
    internal static void SelfCheck()
    {
        var s = new List<Snippet> { new() { Trigger = "shh", Text = "a@b.c" }, new() { Trigger = "hh", Text = "x" } };
        Debug.Assert(Match("shh", s)?.Text == "a@b.c");
        Debug.Assert(Match("x shh", s)?.Text == "a@b.c");
        Debug.Assert(Match("pushh", s) == null);   // 단어 중간은 안 바뀜
        Debug.Assert(Match(" hh", s)?.Text == "x");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & LLKHF_INJECTED) == 0) OnKey(k.vkCode);   // 우리가 보낸 입력은 무시
            }
        }
        catch { }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnKey(uint vk)
    {
        var fg = GetForegroundWindow();
        if (fg != _bufWindow)
        {
            _buf.Clear(); _bufWindow = fg;
            GetWindowThreadProcessId(fg, out uint pid);
            _ownWindow = pid == (uint)Environment.ProcessId;   // 설정 창에서 단축어 입력할 땐 바꾸면 안 됨
        }
        if (_ownWindow) return;

        if (vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT or VK_CAPITAL) return;
        if (vk == VK_BACK) { if (_buf.Length > 0) _buf.Length--; return; }

        bool mod = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                   || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        char c = '\0';
        if (!mod && vk >= 'A' && vk <= 'Z') c = (char)(vk + 32);
        else if (!mod && !shift && vk >= '0' && vk <= '9') c = (char)vk;

        if (c == '\0') { _buf.Clear(); _buf.Append(' '); return; }   // 그 외 키 = 단어 경계
        _buf.Append(c);
        if (_buf.Length > 64) _buf.Remove(0, _buf.Length - 64);

        var hit = Match(_buf.ToString(), _snippets());
        if (hit == null) return;
        _buf.Clear();
        int erase = hit.Trigger.Length;
        string text = hit.Text;
        // 마지막 글자가 앱에 먼저 들어간 뒤 지워야 하므로 훅 밖에서 처리
        _post(() => { if (!IsHangulMode(fg)) Expand(erase, text); });
    }

    /// <summary>buffer 끝이 단축어로 끝나고, 그 앞이 단어 경계(시작/공백)면 매칭. 긴 단축어 우선.</summary>
    internal static Snippet? Match(string buffer, IReadOnlyList<Snippet> snippets)
    {
        Snippet? best = null;
        foreach (var s in snippets)
        {
            var t = s.Trigger.Trim().ToLowerInvariant();
            if (t.Length == 0 || !buffer.EndsWith(t, StringComparison.Ordinal)) continue;
            int start = buffer.Length - t.Length;
            if (start > 0 && buffer[start - 1] != ' ') continue;
            if (best == null || t.Length > best.Trigger.Trim().Length) best = s;
        }
        return best;
    }

    private static void Expand(int erase, string text)
    {
        var inputs = new List<INPUT>();
        for (int i = 0; i < erase; i++) AddVk(inputs, (ushort)VK_BACK);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { AddVk(inputs, VK_RETURN); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static bool IsHangulMode(IntPtr hwnd)
    {
        try
        {
            var ime = ImmGetDefaultIMEWnd(hwnd);
            if (ime == IntPtr.Zero) return false;
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var mode);
            return ((long)mode & IME_CMODE_NATIVE) != 0;
        }
        catch { return false; }
    }

    private static void AddVk(List<INPUT> l, ushort vk)
    {
        l.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } });
        l.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } });
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
    };

    // ---- Win32 ----
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint VK_BACK = 0x08, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const ushort VK_RETURN = 0x0D;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    private const uint WM_IME_CONTROL = 0x283, IMC_GETCONVERSIONMODE = 1, SMTO_ABORTIFHUNG = 2;
    private const long IME_CMODE_NATIVE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // union 크기를 MOUSEINPUT(가장 큼)에 맞춰야 SendInput이 cbSize를 받아줌
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint tid);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(uint vk);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
}
