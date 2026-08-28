namespace CqrsFoundation.Domain.Common;

public sealed class ForbiddenAccessException(string message) : Exception(message);
