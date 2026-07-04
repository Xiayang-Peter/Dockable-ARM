using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// Reads the Windows taskbar's contents so the dock can mirror it: the pinned shortcuts and
/// the set of "taskbar-eligible" application windows currently open. This lets the user manage
/// the dock by pinning/unpinning on the taskbar (even while the dock is closed).
/// </summary>
public static class TaskbarApps
{
    public readonly record struct RunningWindow(IntPtr Hwnd, string ExePath, string Title, string Aumid);

    // System.AppUserModel.ID property key: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5.
    private static readonly Guid AppUserModelIdFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private static readonly ConcurrentDictionary<string, string?> LinkAumidCache = new();

    /// <summary>Folder where Explorer stores taskbar-pinned shortcuts (one .lnk per app).</summary>
    public static string PinnedFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private static readonly ConcurrentDictionary<string, string?> TargetCache = new();

    public static IReadOnlyList<string> GetPinnedLinkPaths()
    {
        try
        {
            if (!Directory.Exists(PinnedFolder))
                return Array.Empty<string>();
            return Directory.GetFiles(PinnedFolder, "*.lnk")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The pinned-taskbar shortcuts in their actual taskbar order, read from the
    /// <c>Taskband\Favorites</c> registry blob. Each entry is
    /// <c>[1 flag byte][DWORD pidl size][pidl]</c>; the PIDL resolves to the .lnk path
    /// (which may live in TaskBar or ImplicitAppShortcuts). Empty if it can't be read.
    /// </summary>
    public static IReadOnlyList<string> GetPinnedOrder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            if (key?.GetValue("Favorites") is not byte[] blob)
                return Array.Empty<string>();

            var ordered = new List<string>();
            int pos = 0;
            while (pos < blob.Length)
            {
                pos += 1; // per-item flag byte (0x00)
                if (pos + 4 > blob.Length)
                    break;
                int size = BitConverter.ToInt32(blob, pos);
                pos += 4;
                if (size <= 0 || pos + size > blob.Length)
                    break;

                string? path = ResolvePidlPath(blob, pos, size);
                if (!string.IsNullOrEmpty(path))
                    ordered.Add(path!);
                pos += size;
            }
            return ordered;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static unsafe string? ResolvePidlPath(byte[] blob, int offset, int size)
    {
        try
        {
            fixed (byte* p = &blob[offset])
            {
                char* buffer = stackalloc char[260];
                if (!PInvoke.SHGetPathFromIDList((ITEMIDLIST*)p, new PWSTR(buffer)))
                    return null;
                string path = new string(buffer);
                return string.IsNullOrEmpty(path) ? null : path;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolves a .lnk to its target executable path (cached). Empty for UWP/store pins.</summary>
    public static string? ResolveLinkTarget(string lnkPath)
    {
        return TargetCache.GetOrAdd(lnkPath, static path =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                    return null;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(path);
                string target = shortcut.TargetPath;
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>
    /// If <paramref name="path"/> is a .lnk whose target is a real file, returns that target (the
    /// shortcut's actual destination); otherwise returns the path unchanged (non-.lnk paths, or
    /// shortcuts that don't resolve to a file such as UWP/store pins).
    /// </summary>
    public static string ResolveToTarget(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string? target = ResolveLinkTarget(path);
            if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                return target;
        }
        return path;
    }

    /// <summary>Enumerates the windows that would appear as taskbar buttons.</summary>
    public static IReadOnlyList<RunningWindow> EnumerateAppWindows(uint ownProcessId)
    {
        var result = new List<RunningWindow>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (IsTaskbarApp(hwnd, ownProcessId, out uint pid))
            {
                var id = ResolveIdentity(hwnd, pid);
                result.Add(new RunningWindow(hwnd, id.Exe, GetWindowTitle(hwnd), id.Aumid));
            }
            return true;
        }, default);
        EvictDeadIdentities();
        return result;
    }

    /// <summary>A window's identity, resolved once per window lifetime. <see cref="Pid"/> detects
    /// HWND recycling (a reused handle in a new process must not inherit the old identity);
    /// <see cref="AumidRetries"/> covers apps that set their AUMID a beat after the window appears.</summary>
    private sealed class WindowIdentity
    {
        public uint Pid;
        public string Exe = string.Empty;
        public string Aumid = string.Empty;
        public int AumidRetries;
    }

    private static readonly ConcurrentDictionary<IntPtr, WindowIdentity> IdentityCache = new();

    /// <summary>The window's cached exe path + AUMID. Both are stable per window lifetime, and
    /// resolving them fresh (an opened process handle + a COM property-store round trip, per window,
    /// on the ~1 s taskbar refresh) was the enumeration's main cost. An empty exe (elevated process)
    /// is cached too — the old per-tick resolve returned the same value every time.</summary>
    private static WindowIdentity ResolveIdentity(HWND hwnd, uint pid)
    {
        if (!IdentityCache.TryGetValue((IntPtr)hwnd, out var id) || id.Pid != pid) // miss, or a recycled hwnd
        {
            id = new WindowIdentity { Pid = pid, Exe = GetProcessPath(pid), Aumid = GetWindowAumid(hwnd) };
            IdentityCache[(IntPtr)hwnd] = id;
        }
        else if (id.Aumid.Length == 0 && id.AumidRetries < 3)
        {
            // Some apps set their AUMID shortly after the window first appears — re-query a bounded
            // number of times so late-set AUMIDs are still picked up without a per-tick COM cost.
            id.AumidRetries++;
            string aumid = GetWindowAumid(hwnd);
            if (aumid.Length > 0)
                id.Aumid = aumid;
        }
        return id;
    }

    private static void EvictDeadIdentities()
    {
        foreach (var key in IdentityCache.Keys)
            if (!PInvoke.IsWindow((HWND)key))
                IdentityCache.TryRemove(key, out _);
    }

    private static unsafe bool IsTaskbarApp(HWND hwnd, uint ownProcessId, out uint pid)
    {
        pid = 0;

        // Shared "normal app window" test (visible, titled, not a tool window, not our process)…
        if (!WindowFilter.IsEligibleAppWindow(hwnd, ownProcessId))
            return false;

        // …plus the taskbar-specific extras.
        if (!PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
            return false; // owned windows aren't their own taskbar button

        // Skip cloaked windows (suspended UWP, other virtual desktops).
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        if (cloaked != 0)
            return false;

        uint p;
        PInvoke.GetWindowThreadProcessId(hwnd, &p); // re-read: WindowFilter doesn't expose it
        pid = p;
        return true;
    }

    /// <summary>True for a packaged (UWP/Store) app's AppUserModelID (the <c>PackageFamily!AppId</c> form).</summary>
    public static bool IsPackagedAumid(string? aumid)
        => !string.IsNullOrEmpty(aumid) && aumid.Contains('!');

    /// <summary>The window's explicit AppUserModelID, or empty (many windows don't set one).</summary>
    public static unsafe string GetWindowAumid(IntPtr hwnd)
    {
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            if (PInvoke.SHGetPropertyStoreForWindow((HWND)hwnd, iid, out object obj).Failed
                || obj is not IPropertyStore store)
                return string.Empty;
            try
            {
                var key = new PROPERTYKEY { fmtid = AppUserModelIdFmtId, pid = 5 };
                store.GetValue(&key, out PROPVARIANT value);
                try
                {
                    Span<char> buffer = stackalloc char[512];
                    if (PInvoke.PropVariantToString(value, buffer).Failed)
                        return string.Empty;
                    int end = buffer.IndexOf('\0');
                    return end >= 0 ? new string(buffer[..end]) : new string(buffer);
                }
                finally
                {
                    PInvoke.PropVariantClear(ref value);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>The AppUserModelID declared by a .lnk (via its shell property store), if any.</summary>
    public static string? GetLinkAumid(string lnkPath)
    {
        return LinkAumidCache.GetOrAdd(lnkPath, static path =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null)
                    return null;
                dynamic app = Activator.CreateInstance(shellType)!;
                dynamic folder = app.Namespace(Path.GetDirectoryName(path));
                dynamic? item = folder?.ParseName(Path.GetFileName(path));
                string? aumid = item?.ExtendedProperty("System.AppUserModel.ID");
                return string.IsNullOrWhiteSpace(aumid) ? null : aumid;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>The executable backing a window, or empty (process exited / access denied).</summary>
    public static unsafe string GetWindowExePath(IntPtr hwnd)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId((HWND)hwnd, &pid);
        return pid == 0 ? string.Empty : GetProcessPath(pid);
    }

    private static string GetProcessPath(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access denied (e.g. elevated processes when we're not), or process exited.
            return string.Empty;
        }
    }

    /// <summary>The window's title bar text, or empty.</summary>
    public static string GetWindowTitle(IntPtr hwnd) => GetWindowTitle((HWND)hwnd);

    private static unsafe string GetWindowTitle(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetWindowText(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
