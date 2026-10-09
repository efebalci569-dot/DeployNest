using DeployNest.Core.Installation;

namespace DeployNest.Core.History;

public sealed record HistoryEntry(
    Guid SessionId,
    DateTimeOffset Timestamp,
    string SoftwareId,
    string SoftwareName,
    string Operation,
    InstallMethod? Method,
    OutcomeStatus Status,
    FailureKind Failure,
    string? Message,
    TimeSpan Duration);

public interface IHistoryStore
{
    /// <summary>Returns entries newest first. A corrupt file is backed up and treated as empty.</summary>
    IReadOnlyList<HistoryEntry> Load();

    void Append(IEnumerable<HistoryEntry> entries);

    void Clear();
}
