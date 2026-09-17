namespace CqrsFoundation.Domain.Common;

public sealed class InvalidQueryException(string message) : Exception(message);
