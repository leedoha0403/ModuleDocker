namespace DiskUsage.Core;

public sealed record DriveReading(string Name, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    /// <summary>Used share, 0..100.</summary>
    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Round(100.0 * UsedBytes / TotalBytes, 1);

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : $"{Name} ({Label})";
}

public interface IDiskService
{
    IReadOnlyList<DriveReading> Read();
}

public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {Units[i]}" : $"{v:0.#} {Units[i]}";
    }
}

/// <summary>Reads fixed and removable drives; drives that are not ready or fail are skipped.</summary>
public sealed class SystemDiskService : IDiskService
{
    public IReadOnlyList<DriveReading> Read()
    {
        var result = new List<DriveReading>();
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }

        foreach (var d in drives)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                result.Add(new DriveReading(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a drive can disappear between enumeration and query
            }
        }
        return result;
    }
}
