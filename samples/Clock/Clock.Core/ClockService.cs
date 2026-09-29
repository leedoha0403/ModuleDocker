namespace Clock.Core;

public sealed record ClockReading(string ZoneId, string ZoneName, DateTimeOffset Now)
{
    public string Time => Now.ToString("HH:mm:ss");
    public string ShortTime => Now.ToString("HH:mm");
    public string Date => Now.ToString("yyyy-MM-dd ddd");
}

public interface IClockService
{
    IReadOnlyList<(string Id, string Name)> Zones { get; }
    ClockReading Read(string zoneId, DateTimeOffset utcNow);
}

public sealed class SystemClockService : IClockService
{
    public IReadOnlyList<(string Id, string Name)> Zones { get; } =
        TimeZoneInfo.GetSystemTimeZones().Select(z => (z.Id, z.DisplayName)).ToList();

    public ClockReading Read(string zoneId, DateTimeOffset utcNow)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Local; // a saved zone may no longer exist on this machine
        }
        return new ClockReading(zone.Id, zone.StandardName, TimeZoneInfo.ConvertTime(utcNow, zone));
    }
}
