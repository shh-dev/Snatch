using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Snatch;

/// <summary>
/// 셸 COM을 전혀 쓰지 않는 순수 WPF 경로 입력창. 셸 폴더 다이얼로그가 멈추는 환경(샌드박스 Job 등)에서도 안전.
/// 탐색기 주소창에서 경로 복사해 붙여넣기. "열기"로 탐색기에서 폴더 확인 가능.
/// </summary>
public static class InputBox
{
    public static string? AskFolder(Window owner, string title, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x2B)),
            WindowStyle = WindowStyle.SingleBorderWindow,
        };

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = "폴더 경로 입력 (탐색기 주소창에서 복사해 붙여넣기)",
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var box = new TextBox
        {
            Text = initial,
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
        };
        root.Children.Add(box);

        var status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xB0, 0xB8)), FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(status);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var openBtn = new Button { Content = "📂 탐색기에서 열기", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        var okBtn = new Button { Content = "확인", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "취소", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        root.Children.Add(btnRow);

        win.Content = root;

        string? result = null;

        openBtn.Click += (_, _) =>
        {
            var p = box.Text.Trim().Trim('"');
            try { if (Directory.Exists(p)) System.Diagnostics.Process.Start("explorer.exe", p); else status.Text = "없는 폴더 — 경로 확인"; }
            catch { }
        };

        okBtn.Click += (_, _) =>
        {
            var p = box.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(p)) { status.Text = "경로를 입력하세요"; return; }
            if (!Directory.Exists(p))
            {
                var ans = MessageBox.Show(win, $"폴더가 없습니다:\n{p}\n\n새로 만들까요?", "폴더 없음", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes) return;
                try { Directory.CreateDirectory(p); }
                catch (Exception ex) { status.Text = "생성 실패: " + ex.Message; return; }
            }
            result = p;
            win.DialogResult = true;
        };

        box.Focus();
        box.SelectAll();
        return win.ShowDialog() == true ? result : null;
    }
}
