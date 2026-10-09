using DeployNest.Core.Localization;

namespace DeployNest.Core.Installation;

public sealed record FailureDescription(string Title, string Advice);

/// <summary>Turns a <see cref="FailureKind"/> into a plain-language explanation and next step.</summary>
public sealed class FailureDescriber(ILocalizer localizer)
{
    public FailureDescription Describe(FailureKind kind) =>
        new(localizer[$"Failure.{kind}.Title"], localizer[$"Failure.{kind}.Advice"]);

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
