using CqrsFoundation.Domain.Common;
using Marten;

namespace CqrsFoundation.Application;

public static class ConcurrencyPreconditions
{
    public static async Task EnsureExpectedVersion(
        IDocumentSession session,
        Guid streamId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (expectedVersion is null)
        {
            return;
        }

        var state = await session.Events.FetchStreamStateAsync(streamId, cancellationToken)
            ?? throw new KeyNotFoundException("Resource not found.");
        if (state.Version != expectedVersion.Value)
        {
            throw new StaleResourceVersionException(expectedVersion.Value, state.Version);
        }
    }
}
