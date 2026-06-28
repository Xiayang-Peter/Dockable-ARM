using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Dockable.Shell;

/// <summary>
/// Launches dock shortcuts via the shell and extracts crisp, alpha-correct icons
/// using <c>IShellItemImageFactory</c> (works for .exe, .lnk, documents, and folders).
/// </summary>
public static class ShortcutService
{
    /// <summary>HRESULT E_PENDING: the shell is still extracting the image.</summary>
    private const uint EPending = 0x8000000A;
    private const int MaxIconAttempts = 12;
    private const int IconRetryDelayMs = 100;


    /// <summary>
    /// Launches the target of a shortcut item using the shell, so .lnk files,
    /// documents, folders, and executables all resolve correctly.
    /// </summary>
    public static bool Launch(DockItem item)
    {
        if (item.Kind != DockItemKind.Shortcut || string.IsNullOrWhiteSpace(item.TargetPath))
            return false;
        return Launch(item.TargetPath, item.Arguments, item.WorkingDirectory);
    }

    /// <summary>Launches a path (exe, .lnk, document, folder) through the shell.</summary>
    public static bool Launch(string targetPath, string arguments = "", string workingDirectory = "")
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = true,
            };

            if (!string.IsNullOrWhiteSpace(arguments))
                psi.Arguments = arguments;

            psi.WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : SafeDirectoryOf(targetPath);

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Launch failed for '{targetPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Opens an Explorer window at the file's folder with the file itself selected.</summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            // /select, opens the containing folder and highlights the item.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] RevealInExplorer failed for '{path}': {ex.Message}");
        }
    }

    private static string SafeDirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(path) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Extracts an icon for <paramref name="path"/> at the requested pixel size on a
    /// background thread (shell icon extraction must never run on the UI thread).
    /// Returns a frozen, cross-thread-usable bitmap, or null if extraction fails.
    /// </summary>
    public static Task<ImageSource?> LoadIconAsync(string path, int pixelSize)
        => Task.Run(() => LoadIcon(path, pixelSize));

    private static unsafe ImageSource? LoadIcon(string path, int pixelSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Internet shortcuts (.url) — e.g. Steam game shortcuts — aren't images themselves, so the
        // shell hands back a blank page icon. Resolve the icon the desktop actually shows by reading
        // the [InternetShortcut] IconFile / IconIndex and extracting from there.
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = LoadUrlShortcutIcon(path, pixelSize);
            if (resolved is not null)
                return resolved;
            // Fall through to the default shell extraction (a blank page beats nothing).
        }

        object? shellItem = null;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            var hr = PInvoke.SHCreateItemFromParsingName(path, null!, iid, out shellItem);
            if (hr.Failed || shellItem is not IShellItemImageFactory factory)
                return null;

            var size = new Windows.Win32.Foundation.SIZE(pixelSize, pixelSize);

            // RESIZETOFIT + BIGGERSIZEOK lets the shell hand back the largest available
            // asset (up to 256px) and scale to fit, giving crisp results on HiDPI.
            const SIIGBF flags = SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK;

            // For uncached items the shell extracts the image asynchronously and the first
            // call returns E_PENDING. Poll briefly until the image becomes available.
            for (int attempt = 0; attempt < MaxIconAttempts; attempt++)
            {
                HBITMAP hbmp;
                try
                {
                    factory.GetImage(size, flags, &hbmp);
                }
                catch (COMException ex) when ((uint)ex.HResult == EPending)
                {
                    Thread.Sleep(IconRetryDelayMs);
                    continue;
                }

                try
                {
                    return ConvertHBitmap(hbmp);
                }
                finally
                {
                    PInvoke.DeleteObject((HGDIOBJ)(void*)hbmp);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Icon load failed for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
                Marshal.ReleaseComObject(shellItem);
        }
    }

    /// <summary>
    /// Reads an Internet shortcut's (.url) <c>IconFile</c>/<c>IconIndex</c> and extracts that icon —
    /// the same one Explorer shows on the desktop. Returns null if the .url has no usable icon
    /// reference (the caller then falls back to the generic shell icon).
    /// </summary>
    private static ImageSource? LoadUrlShortcutIcon(string urlPath, int pixelSize)
    {
        try
        {
            string? iconFile = null;
            int iconIndex = 0;

            foreach (string raw in File.ReadAllLines(urlPath))
            {
                string line = raw.Trim();
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    iconFile = line["IconFile=".Length..].Trim();
                else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(line["IconIndex=".Length..].Trim(), out int idx))
                    iconIndex = idx;
            }

            if (string.IsNullOrWhiteSpace(iconFile))
                return null;

            iconFile = Environment.ExpandEnvironmentVariables(iconFile);
            if (!File.Exists(iconFile))
                return null;

            // Extract straight from the referenced icon resource (.ico/.exe/.dll at the given index).
            // We deliberately avoid the IShellItemImageFactory path here: for a raw .ico it returns
            // the image in an orientation our DIB flip heuristic gets wrong (icons come out upside
            // down), whereas PrivateExtractIcons + CreateBitmapSourceFromHIcon is orientation-correct.
            return ExtractIconAtIndex(iconFile, iconIndex, pixelSize);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] .url icon resolve failed for '{urlPath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Extracts a single icon at a given index/size from an .exe/.dll/.ico via the shell.</summary>
    private static unsafe ImageSource? ExtractIconAtIndex(string file, int index, int pixelSize)
    {
        uint extracted = PInvoke.PrivateExtractIcons(file, index, pixelSize, pixelSize, out var hicon, null, 1, 0);
        try
        {
            if (extracted == 0 || hicon is null || hicon.IsInvalid)
                return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hicon.DangerousGetHandle(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dockable] Icon extract failed for '{file}' [{index}]: {ex.Message}");
            return null;
        }
        finally
        {
            hicon?.Dispose(); // the SafeHandle owns the HICON and DestroyIcon's it
        }
    }

    /// <summary>
    /// Copies a 32bpp DIB (as produced by GetImage, premultiplied BGRA) into a frozen
    /// WriteableBitmap, preserving the alpha channel and handling row orientation.
    /// </summary>
    private static unsafe ImageSource? ConvertHBitmap(HBITMAP hbmp)
    {
        DIBSECTION ds = default;
        int written = PInvoke.GetObject((HGDIOBJ)(void*)hbmp, sizeof(DIBSECTION), &ds);
        if (written < sizeof(DIBSECTION) || ds.dsBm.bmBits is null)
            return null;

        int width = ds.dsBm.bmWidth;
        int height = ds.dsBm.bmHeight;
        int stride = ds.dsBm.bmWidthBytes;
        if (width <= 0 || height <= 0 || ds.dsBm.bmBitsPixel != 32)
            return null;

        int byteCount = stride * height;
        byte[] buffer = new byte[byteCount];
        Marshal.Copy((IntPtr)ds.dsBm.bmBits, buffer, 0, byteCount);

        // A negative biHeight means the DIB is top-down (the common case for GetImage).
        // A positive biHeight is bottom-up and must be flipped row-by-row.
        if (ds.dsBmih.biHeight > 0)
            FlipRowsInPlace(buffer, stride, height);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void FlipRowsInPlace(byte[] buffer, int stride, int height)
    {
        byte[] tmp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(buffer, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(buffer, bottom * stride, buffer, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, buffer, bottom * stride, stride);
        }
    }
}
