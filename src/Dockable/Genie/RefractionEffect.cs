using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Dockable.Interop;

namespace Dockable.Genie;

/// <summary>
/// A WPF pixel-shader effect that refracts the element it's applied to (the captured backdrop) by
/// offsetting each pixel's UV using a displacement map — strongest at the rounded rim, ~zero in the
/// middle. The HLSL is compiled to ps_2_0 bytecode at runtime (no fxc needed) and loaded as a
/// <see cref="PixelShader"/>. If compilation/loading fails the effect is a no-op pass-through.
/// </summary>
public sealed class RefractionEffect : ShaderEffect
{
    private static readonly PixelShader? Shader = Build();

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(RefractionEffect), 0);

    public static readonly DependencyProperty DisplacementMapProperty =
        RegisterPixelShaderSamplerProperty("DisplacementMap", typeof(RefractionEffect), 1);

    public static readonly DependencyProperty DistortionAmountProperty =
        DependencyProperty.Register(nameof(DistortionAmount), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.06, PixelShaderConstantCallback(0)));

    /// <summary>True when the shader compiled and loaded — i.e. refraction is actually available.</summary>
    public static bool IsAvailable => Shader is not null;

    public RefractionEffect()
    {
        if (Shader is null)
            return; // pass-through; caller should check IsAvailable
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(DisplacementMapProperty);
        UpdateShaderValue(DistortionAmountProperty);
    }

    /// <summary>The content to refract (s0). Auto-bound to the element the effect is applied to.</summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>The displacement map (s1): R/G channels encode the UV offset (0.5 = none).</summary>
    public Brush DisplacementMap
    {
        get => (Brush)GetValue(DisplacementMapProperty);
        set => SetValue(DisplacementMapProperty, value);
    }

    /// <summary>How strongly the displacement map bends the image (UV units).</summary>
    public double DistortionAmount
    {
        get => (double)GetValue(DistortionAmountProperty);
        set => SetValue(DistortionAmountProperty, value);
    }

    private static PixelShader? Build()
    {
        byte[]? bytecode = ShaderCompiler.Compile(Hlsl, "main", "ps_2_0");
        if (bytecode is null)
            return null;
        try
        {
            // PixelShader loads compiled bytecode from a URI; write it to a temp .ps and point at it.
            string path = Path.Combine(Path.GetTempPath(), "Dockable.refraction.ps");
            File.WriteAllBytes(path, bytecode);
            var ps = new PixelShader { UriSource = new Uri(path) };
            return ps;
        }
        catch
        {
            return null;
        }
    }

    // s0 = content being refracted; s1 = displacement map; c0 = distortion strength.
    private const string Hlsl = @"
sampler2D inputSampler : register(s0);
sampler2D dispSampler  : register(s1);
float distortion : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 d = tex2D(dispSampler, uv).rg - 0.5;   // -0.5..0.5 offset direction
    return tex2D(inputSampler, uv + d * distortion);
}";

    /// <summary>
    /// Builds a rim-lens displacement map: neutral (no offset) through the middle, ramping up toward
    /// the four edges where it pulls the sampled UV inward — so the refraction concentrates at the
    /// rounded rim (the "glass edge" look). <paramref name="rimWidth"/> is a fraction of the half-size.
    /// </summary>
    public static ImageSource BuildRimMap(int width, int height, double rimWidth = 0.30, double strength = 0.9)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        int stride = width * 4;
        var buffer = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height;
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width;

                double edge = Math.Min(Math.Min(u, 1 - u), Math.Min(v, 1 - v)); // 0 at edge, 0.5 at center
                double t = Math.Clamp(1 - edge / rimWidth, 0, 1);
                t *= t; // sharpen toward the very edge

                double dx = 0.5 - u, dy = 0.5 - v;
                double len = Math.Sqrt(dx * dx + dy * dy) + 1e-5;
                double ox = dx / len * t * strength; // pull inward
                double oy = dy / len * t * strength;

                int i = y * stride + x * 4;
                buffer[i + 0] = 0;                                     // B (unused)
                buffer[i + 1] = Encode(oy);                            // G
                buffer[i + 2] = Encode(ox);                            // R
                buffer[i + 3] = 255;                                   // A
            }
        }

        bmp.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        bmp.Freeze();
        return bmp;

        static byte Encode(double offset) => (byte)Math.Clamp((0.5 + offset * 0.5) * 255.0, 0, 255);
    }
}
