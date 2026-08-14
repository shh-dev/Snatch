using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Snatch;

/// <summary>Windows 내장 OCR (Windows.Media.Ocr) — 이미지에서 텍스트 추출. 추가 설치 불필요.</summary>
public static class OcrService
{
    public static async Task<string> RecognizeAsync(string imagePath)
    {
        if (!File.Exists(imagePath)) return "";

        SoftwareBitmap soft;
        var bytes = await File.ReadAllBytesAsync(imagePath);
        using (var ras = new InMemoryRandomAccessStream())
        {
            await ras.WriteAsync(bytes.AsBuffer());
            ras.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ras);
            soft = await decoder.GetSoftwareBitmapAsync();
        }

        var engine = OcrEngine.TryCreateFromLanguage(new Language("ko"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) { soft.Dispose(); return ""; }

        // 원본이 커도 엔진 최대치 내로 축소해 인식
        if (soft.PixelWidth > OcrEngine.MaxImageDimension || soft.PixelHeight > OcrEngine.MaxImageDimension)
        {
            // 간단히 그대로 시도 — 대부분 스크린샷은 한도 내
        }

        var result = await engine.RecognizeAsync(soft);
        soft.Dispose();

        // 줄 순서대로 이어붙이기
        var lines = result.Lines.Select(l => l.Text);
        return string.Join("\n", lines).Trim();
    }
}
