using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snatch;

/// <summary>스크롤 캡처: 선택 영역 위에서 휠을 자동으로 굴리며 프레임을 찍고, 겹치는 줄을 찾아 한 장으로 이어붙인다.</summary>
public static class ScrollCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    private const uint MOUSEEVENTF_WHEEL = 0x0800, WM_MOUSEWHEEL = 0x020A;

    /// <summary>
    /// 휠 굴리기. 기본은 커서 아래 창에 WM_MOUSEWHEEL을 직접 보냄 — ButterScroll 같은 스크롤 보정 앱이
    /// 가짜 휠 입력(mouse_event)을 가로채서 페이지가 안 움직이는 문제 회피. viaInput=true면 예전 방식.
    /// </summary>
    private static void Wheel(int x, int y, int delta, bool viaInput)
    {
        SetCursorPos(x, y);   // 캡처 중 마우스가 밀려도 매번 영역 가운데로 (앱은 커서 아래에만 스크롤을 줌)
        if (viaInput) { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, delta, UIntPtr.Zero); return; }
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return;
        PostMessage(hwnd, WM_MOUSEWHEEL, new IntPtr(delta << 16), new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF)));
    }
    private const int WHEEL_DELTA = 120, VK_ESCAPE = 0x1B;

    private const int MaxFrames = 200;
    private const int MaxHeight = 30000;   // GDI+/뷰어 부담 방지
    private const int MinOverlap = 24;     // 이보다 적게 겹치면 이어붙이기 신뢰 불가

    /// <summary>영역(물리 px)을 끝까지 스크롤하며 캡처. Esc 누르면 거기까지 저장. 프레임 1장뿐이면 그 1장.</summary>
    public static async Task<Bitmap> RunAsync(int x, int y, int w, int h, Action<int, int>? progress = null)
    {
        GetCursorPos(out var oldCursor);
        SetCursorPos(x + w / 2, y + h / 2);   // 휠은 커서 아래 창으로 감 (일부 앱은 커서 위치로 대상 판단)
        await Task.Delay(120);

        var strips = new List<Bitmap>();
        Bitmap prev = Capture.GrabRegion(x, y, w, h);
        var prevRows = RowHashes(prev);
        int top = -1, bottom = 0;   // 고정 헤더/푸터 줄 수 (첫 비교에서 확정)
        int total = 0;
        int notches = Math.Clamp(h / 3 / 100, 1, 8);   // 한 번에 굴릴 칸 (1칸 ≈ 100px), 화면 1/3 정도
        bool firstStripPending = true;
        bool viaInput = false;
        int misses = 0;   // 직접 메시지로 안 움직이면 첫 단계에서 한 번만 예전 방식으로 재시도
        int cx = x + w / 2, cy = y + h / 2;

        try
        {
            for (int i = 0; i < MaxFrames; i++)
            {
                if (EscDown()) break;
                Wheel(cx, cy, -WHEEL_DELTA * notches, viaInput);
                var (cur, curRows) = await GrabSettled(x, y, w, h);
                if (EscDown()) { cur.Dispose(); break; }

                if (top < 0)
                {
                    top = CountSame(prevRows, curRows, fromTop: true);
                    bottom = CountSame(prevRows, curRows, fromTop: false);
                    if (top >= h)   // 첫 휠에 안 움직임
                    {
                        cur.Dispose();
                        if (!viaInput) { viaInput = true; top = -1; continue; }   // 다른 방식으로 한 번 더
                        break;   // 그래도 안 움직임 = 스크롤할 게 없음
                    }
                    top = Math.Min(top, h / 3); bottom = Math.Min(bottom, h / 3);
                }

                int d = FindShift(prevRows, curRows, top, h - bottom);
                if (d <= 0)   // 변화 없음/매칭 실패 — 화면이 늦게 바뀌었을 수 있어서 3번 연속일 때만 "끝"으로 봄
                {
                    cur.Dispose();
                    if (++misses >= 3) break;
                    continue;
                }
                misses = 0;

                if (firstStripPending)
                {
                    strips.Add(Slice(prev, 0, h - bottom));   // 첫 프레임: 헤더 포함, 푸터 제외
                    total += h - bottom;
                    firstStripPending = false;
                }
                strips.Add(Slice(cur, h - bottom - d, d));   // 새로 드러난 줄만
                total += d;
                progress?.Invoke(strips.Count, total);

                // 스크롤량이 겹침을 너무 먹으면 칸 수 줄이기
                if (d > (h - top - bottom) * 2 / 3 && notches > 1) notches--;

                prev.Dispose();
                prev = cur; prevRows = curRows;
                if (total >= MaxHeight) break;
            }

            if (firstStripPending) return prev.Clone(new Rectangle(0, 0, w, h), prev.PixelFormat);   // 스크롤 안 됨 → 단일 캡처

            if (bottom > 0) { strips.Add(Slice(prev, h - bottom, bottom)); total += bottom; }   // 마지막 프레임의 푸터
            return Compose(strips, w, total);
        }
        finally
        {
            prev.Dispose();
            foreach (var s in strips) s.Dispose();
            SetCursorPos(oldCursor.X, oldCursor.Y);
        }
    }

    private static bool EscDown() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    /// <summary>부드러운 스크롤 애니메이션이 멈출 때까지 기다렸다가 캡처.</summary>
    private static async Task<(Bitmap, ulong[])> GrabSettled(int x, int y, int w, int h)
    {
        await Task.Delay(220);
        var a = Capture.GrabRegion(x, y, w, h);
        var ah = RowHashes(a);
        for (int tries = 0; tries < 8; tries++)
        {
            await Task.Delay(90);
            var b = Capture.GrabRegion(x, y, w, h);
            var bh = RowHashes(b);
            bool same = ah.AsSpan().SequenceEqual(bh);
            a.Dispose();
            a = b; ah = bh;
            if (same) break;
        }
        return (a, ah);
    }

    private static int CountSame(ulong[] a, ulong[] b, bool fromTop)
    {
        int n = a.Length, c = 0;
        for (int i = 0; i < n; i++)
        {
            int k = fromTop ? i : n - 1 - i;
            if (a[k] != b[k]) break;
            c++;
        }
        return c;
    }

    /// <summary>cur[i] == prev[i + d] 를 가장 잘 만족하는 스크롤 거리 d. 단색 줄(빈 여백)은 판정에서 뺀다. 0 = 못 찾음/안 움직임.</summary>
    private static int FindShift(ulong[] prev, ulong[] cur, int lo, int hi)
    {
        int span = hi - lo;
        if (span < MinOverlap * 2) return 0;

        bool moved = false;
        for (int i = lo; i < hi; i++) if (prev[i] != cur[i]) { moved = true; break; }
        if (!moved) return 0;

        int bestD = 0, bestHits = 0; double bestScore = 0;
        for (int d = 1; d <= span - MinOverlap; d++)
        {
            int hits = 0, considered = 0;
            for (int i = lo; i < hi - d; i++)
            {
                ulong c = cur[i];
                if ((c & 1UL) != 0) continue;   // 단색 줄 표시 비트 → 무시
                considered++;
                if (c == prev[i + d]) hits++;
            }
            if (considered < 8) continue;
            double score = (double)hits / considered;
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && hits > bestHits))
            { bestScore = score; bestHits = hits; bestD = d; }
        }
        return bestScore >= 0.8 ? bestD : 0;
    }

    /// <summary>줄마다 해시 (오른쪽 스크롤바 영역 제외). 최하위 비트 1 = 단색 줄.</summary>
    private static ulong[] RowHashes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int x0 = Math.Min(2, w - 1);
        int x1 = Math.Max(x0 + 1, w - Math.Clamp(w / 12, 0, 28));   // 스크롤바 썸이 움직여도 같은 줄로 보이게
        var res = new ulong[h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[w];
            for (int yy = 0; yy < h; yy++)
            {
                Marshal.Copy(data.Scan0 + yy * data.Stride, row, 0, w);
                ulong hash = 14695981039346656037UL;
                int first = row[x0] & 0xFFFFFF;
                bool uniform = true;
                for (int xx = x0; xx < x1; xx++)
                {
                    int px = row[xx] & 0xFFFFFF;
                    if (px != first) uniform = false;
                    hash = (hash ^ (uint)px) * 1099511628211UL;
                }
                res[yy] = uniform ? (hash | 1UL) : (hash & ~1UL);
            }
        }
        finally { bmp.UnlockBits(data); }
        return res;
    }

    private static Bitmap Slice(Bitmap src, int y, int height) =>
        src.Clone(new Rectangle(0, y, src.Width, height), PixelFormat.Format32bppArgb);

    private static Bitmap Compose(List<Bitmap> strips, int w, int total)
    {
        var outBmp = new Bitmap(w, Math.Max(1, total), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(outBmp);
        int yy = 0;
        foreach (var s in strips)
        {
            g.DrawImage(s, new Rectangle(0, yy, w, s.Height), new Rectangle(0, 0, w, s.Height), GraphicsUnit.Pixel);
            yy += s.Height;
        }
        return outBmp;
    }
}
