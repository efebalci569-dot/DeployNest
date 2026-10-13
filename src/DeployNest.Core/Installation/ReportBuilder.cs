using System.Text;
using DeployNest.Core.Localization;

namespace DeployNest.Core.Installation;

/// <summary>Builds the plain-text report shown after a session and offered for export.</summary>
public sealed class ReportBuilder(ILocalizer localizer, string? platform = null)
{
    private readonly FailureDescriber _describer = new(localizer, platform);

    public string Build(SessionResult session, string appVersion, string? logDirectory)
    {
        var culture = localizer.Culture;
        var text = new StringBuilder();
        var title = localizer["Report.Title"];
        text.AppendLine(title);
        text.AppendLine(new string('=', title.Length));
        text.AppendLine(localizer.Format("Report.Date", session.StartedAt.ToLocalTime().ToString("g", culture)));
        text.AppendLine(localizer.Format("Report.Duration", FormatDuration(session.Duration)));
        text.AppendLine(localizer.Format("Report.Version", appVersion));
        text.AppendLine(localizer.Format("Report.Session", session.SessionId));
        if (session.CancelRequested)
            text.AppendLine(localizer["Report.CancelRequested"]);
        if (session.RebootRequired)
            text.AppendLine(localizer["Report.RebootRequired"]);
        text.AppendLine();

        AppendSection(text, localizer["Report.Section.Succeeded"], session.Succeeded.ToList(), "+");
        AppendSection(text, localizer["Report.Section.Failed"], session.Failed.ToList(), "x");
        AppendSection(text, localizer["Report.Section.Skipped"], session.Skipped.ToList(), "-");
        AppendSection(text, localizer["Report.Section.Canceled"], session.Canceled.ToList(), "!");

        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            text.AppendLine(localizer.Format("Report.Logs", logDirectory));
        }

        return text.ToString();
    }

    private void AppendSection(StringBuilder text, string heading, IReadOnlyList<ItemResult> items, string marker)
    {
        text.AppendLine($"{heading} ({items.Count})");
        if (items.Count == 0)
        {
            text.AppendLine("  " + localizer["Report.None"]);
            text.AppendLine();
            return;
        }

        foreach (var item in items)
        {
            var method = item.Method switch
            {
                InstallMethod.Winget => $"winget: {item.Software.Winget?.PackageId}",
                InstallMethod.Direct => localizer["Method.Direct"],
                InstallMethod.Flatpak => $"Flatpak: {item.Software.Linux?.FlatpakId}",
                InstallMethod.NativePackage => localizer["Method.NativePackage"],
                _ => localizer["Method.None"],
            };
            var duration = item.Duration > TimeSpan.Zero ? $" — {FormatDuration(item.Duration)}" : string.Empty;
            text.AppendLine($"  [{marker}] {item.Software.Name} ({method}){duration}");

            if (item.Outcome.Status != Installation.OutcomeStatus.Succeeded)
            {
                var description = _describer.Describe(item.Outcome.Failure);
                text.AppendLine("      " + _describer.Summarize(item.Outcome));
                text.AppendLine("      " + localizer.Format("Report.Advice", description.Advice));
            }
            else
            {
                if (item.Outcome.RebootRequired)
                    text.AppendLine("      " + localizer["Outcome.SucceededRebootRequired"]);
                if (item.PostInstallVerified == false)
                    text.AppendLine("      " + localizer["Report.NotVerifiedAfterInstall"]);
            }

            foreach (var note in item.Notes)
                text.AppendLine("      " + note);
        }
        text.AppendLine();
    }

    public string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return localizer.Format("Duration.Seconds", 0);
        if (duration.TotalMinutes < 1)
            return localizer.Format("Duration.Seconds", (int)duration.TotalSeconds);
        if (duration.TotalHours < 1)
            return localizer.Format("Duration.MinutesSeconds", (int)duration.TotalMinutes, duration.Seconds);
        return localizer.Format("Duration.HoursMinutes", (int)duration.TotalHours, duration.Minutes);
    }
}
