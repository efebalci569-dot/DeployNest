using System.Globalization;

namespace DeployNest.Core.Formatting;

public static class SizeFormatter
{
    /// <summary>Formats a byte count using binary units ("1,5 GB" with Turkish culture).</summary>
    public static string Format(long bytes, CultureInfo culture)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : "0.#";
        return value.ToString(format, culture) + " " + units[unit];
    }
}
