namespace CqrsFoundation.Domain.Common;

public sealed class BusinessRuleException(string message) : Exception(message);
