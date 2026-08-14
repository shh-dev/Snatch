using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Snatch;

/// <summary>Multiply-blend shader: the element it's applied to (highlighter strokes) is multiplied over <see cref="Bg"/>.</summary>
public class MultiplyEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Multiply.ps", UriKind.Absolute),
    };

    public MultiplyEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(BgProperty);
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(MultiplyEffect), 0);
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }

    public static readonly DependencyProperty BgProperty =
        RegisterPixelShaderSamplerProperty("Bg", typeof(MultiplyEffect), 1);
    public Brush Bg { get => (Brush)GetValue(BgProperty); set => SetValue(BgProperty, value); }
}
