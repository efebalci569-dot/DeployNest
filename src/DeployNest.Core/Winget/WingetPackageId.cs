using System.Text.RegularExpressions;

namespace DeployNest.Core.Winget;

/// <summary>
/// Validates winget package identifiers (e.g. "Google.Chrome", "Python.Python.3.14").
/// Identifiers are passed to winget as discrete process arguments, never through a shell,
/// but they are still restricted to the documented character set as defense in depth.
/// </summary>
public static partial class WingetPackageId
{
    public const int MaxLength = 128;

    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= MaxLength && IdRegex().IsMatch(id);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_+]*(\.[A-Za-z0-9\-_+]+)+$")]
    private static partial Regex IdRegex();
}
