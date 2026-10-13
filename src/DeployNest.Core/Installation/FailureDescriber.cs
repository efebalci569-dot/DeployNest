using DeployNest.Core.Localization;

namespace DeployNest.Core.Installation;

public sealed record FailureDescription(string Title, string Advice);

/// <summary>
/// Turns a <see cref="FailureKind"/> into a plain-language explanation and next step. With a
/// <paramref name="platform"/> (e.g. "Linux"), platform-specific texts
/// (<c>Failure.&lt;Kind&gt;.&lt;Platform&gt;.Title</c>) are preferred when they exist.
/// </summary>
public sealed class FailureDescriber(ILocalizer localizer, string? platform = null)
{
    public FailureDescription Describe(FailureKind kind) =>
        new(Text(kind, "Title"), Text(kind, "Advice"));

    private string Text(FailureKind kind, string part)
    {
        if (platform is not null)
        {
            var key = $"Failure.{kind}.{platform}.{part}";
            var specific = localizer[key];
            if (specific != key)
                return specific;
        }
        return localizer[$"Failure.{kind}.{part}"];
    }

    /// <summary>One-line explanation including the technical code when there is one.</summary>
    public string Summarize(InstallOutcome outcome)
    {
        if (outcome.Status == OutcomeStatus.Succeeded)
        {
            return outcome.RebootRequired
                ? localizer["Outcome.SucceededRebootRequired"]
                : localizer["Outcome.Succeeded"];
        }

        var description = Describe(outcome.Failure);
        return string.IsNullOrWhiteSpace(outcome.TechnicalDetail) || outcome.Failure == FailureKind.Canceled
            ? description.Title
            : localizer.Format("Outcome.WithCode", description.Title, outcome.TechnicalDetail);
    }
}
