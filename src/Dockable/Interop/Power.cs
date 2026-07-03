using Windows.Win32;

namespace Dockable.Interop;

/// <summary>Reads the system battery level and charge state for the menu bar's battery indicator.</summary>
public static class Power
{
    /// <param name="Present">False on desktops / when there's no system battery — the indicator hides.</param>
    /// <param name="Percent">Charge level 0–100 (0 when unknown).</param>
    /// <param name="Charging">The battery is currently charging.</param>
    /// <param name="OnAc">Running on AC power (plugged in).</param>
    public readonly record struct BatteryStatus(bool Present, int Percent, bool Charging, bool OnAc);

    // BatteryFlag bits (SYSTEM_POWER_STATUS): 128 = no system battery, 255 = unknown, 8 = charging.
    private const int NoBatteryFlag = 128;
    private const int UnknownFlag = 255;
    private const int ChargingFlag = 8;
    private const int AcOnline = 1; // ACLineStatus: 0 offline, 1 online, 255 unknown

    public static BatteryStatus Read()
    {
        if (!PInvoke.GetSystemPowerStatus(out var s))
            return default; // Present == false

        int flag = s.BatteryFlag;
        bool present = flag != NoBatteryFlag && flag != UnknownFlag;
        int percent = s.BatteryLifePercent;
        if (percent > 100)
            percent = 0; // 255 = unknown
        return new BatteryStatus(present, percent, (flag & ChargingFlag) != 0, s.ACLineStatus == AcOnline);
    }
}
