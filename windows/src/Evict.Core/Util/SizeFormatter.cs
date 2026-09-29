using System.Globalization;

namespace Evict.Core.Util;

public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Format(long? bytes, string emptyText = "—")
    {
        if (bytes is null || bytes < 0) return emptyText;
        double v = bytes.Value;
        int unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        string num = unit == 0 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
            : v >= 10 ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{num} {Units[unit]}";
    }

    public const long MB = 1024L * 1024L;
    public const long GB = 1024L * MB;
}
