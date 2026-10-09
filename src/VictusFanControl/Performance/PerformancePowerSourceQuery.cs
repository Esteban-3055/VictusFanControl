using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal readonly record struct PerformancePowerSourceObservation(
    bool Succeeded,
    PerformancePowerSourceKind Source,
    byte? RawAcLineStatus,
    byte? BatteryPercent,
    byte? BatteryFlags,
    string Status);

internal interface IPerformancePowerSourceReader
{
    PerformancePowerSourceObservation Read();
}

/// <summary>
/// Direct Windows AC/DC query used after a future power-source notification.
///
/// The notification itself is only a wake-up signal. This reader is the source
/// of truth. ACLineStatus 255 is preserved as Unknown and never coerced to
/// Battery.
/// </summary>
internal sealed class WindowsPerformancePowerSourceReader :
    IPerformancePowerSourceReader
{
    public PerformancePowerSourceObservation Read()
    {
        try
        {
            var sample =
                SystemPowerStatusReader.Read();

            var source =
                MapRawAcLineStatus(
                    sample.RawAcLineStatus);

            return new PerformancePowerSourceObservation(
                Succeeded: true,
                Source: source,
                RawAcLineStatus:
                    sample.RawAcLineStatus,
                BatteryPercent:
                    sample.BatteryPercent,
                BatteryFlags:
                    sample.BatteryFlags,
                Status:
                    source switch
                    {
                        PerformancePowerSourceKind.Ac =>
                            "WINDOWS_POWER_SOURCE_CONFIRMED_AC",

                        PerformancePowerSourceKind.Battery =>
                            "WINDOWS_POWER_SOURCE_CONFIRMED_BATTERY",

                        _ =>
                            "WINDOWS_POWER_SOURCE_UNKNOWN__NO_PRESET_AUTHORITY"
                    });
        }
        catch (Exception ex)
        {
            return new PerformancePowerSourceObservation(
                Succeeded: false,
                Source:
                    PerformancePowerSourceKind.Unknown,
                RawAcLineStatus: null,
                BatteryPercent: null,
                BatteryFlags: null,
                Status:
                    "WINDOWS_POWER_SOURCE_QUERY_FAILED: " +
                    ex.Message);
        }
    }

    internal static PerformancePowerSourceKind MapRawAcLineStatus(
        byte raw) =>
        raw switch
        {
            1 => PerformancePowerSourceKind.Ac,
            0 => PerformancePowerSourceKind.Battery,
            _ => PerformancePowerSourceKind.Unknown
        };
}
