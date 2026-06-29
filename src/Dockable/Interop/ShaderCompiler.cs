using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;

namespace Dockable.Interop;

/// <summary>
/// Compiles HLSL to Direct3D pixel-shader bytecode at runtime via d3dcompiler_47 (<c>D3DCompile</c>),
/// so we don't need fxc/the DirectX SDK at build time. The bytecode is what WPF's
/// <see cref="System.Windows.Media.Effects.PixelShader"/> consumes.
/// </summary>
public static class ShaderCompiler
{
    /// <summary>Compiles <paramref name="hlsl"/> to bytecode for <paramref name="target"/> (e.g. "ps_2_0"),
    /// or null on failure (the error is written to the debug log).</summary>
    public static unsafe byte[]? Compile(string hlsl, string entryPoint, string target)
    {
        byte[] src = Encoding.ASCII.GetBytes(hlsl);
        ID3DBlob? code = null;
        ID3DBlob? errors = null;
        try
        {
            HRESULT hr;
            fixed (byte* pSrc = src)
            {
                hr = PInvoke.D3DCompile(pSrc, (nuint)src.Length, "Dockable.hlsl", null, null,
                    entryPoint, target, 0, 0, out code, out errors);
            }

            if (hr.Failed || code is null)
            {
                if (errors is not null)
                {
                    string msg = Marshal.PtrToStringAnsi((IntPtr)errors.GetBufferPointer()) ?? string.Empty;
                    Debug.WriteLine($"[Dockable] Shader compile failed (0x{(uint)hr.Value:X8}): {msg}");
                }
                return null;
            }

            int len = (int)code.GetBufferSize();
            var bytecode = new byte[len];
            Marshal.Copy((IntPtr)code.GetBufferPointer(), bytecode, 0, len);
            return bytecode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Shader compile error: {ex.Message}");
            return null;
        }
        finally
        {
            if (errors is not null && Marshal.IsComObject(errors)) Marshal.ReleaseComObject(errors);
            if (code is not null && Marshal.IsComObject(code)) Marshal.ReleaseComObject(code);
        }
    }
}
