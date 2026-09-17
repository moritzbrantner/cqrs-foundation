namespace CqrsFoundation.Domain.Common;

public sealed class StaleResourceVersionException(long expectedVersion, long actualVersion)
    : Exception($"The resource changed from version {expectedVersion} to version {actualVersion} before this command was applied.")
{
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}
