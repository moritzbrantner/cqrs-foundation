namespace CqrsFoundation.Application;

public sealed record CreatedResource(Guid ResourceId, long Version);

public sealed record MutationResult(long Version);
