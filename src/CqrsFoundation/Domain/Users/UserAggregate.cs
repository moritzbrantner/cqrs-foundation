namespace CqrsFoundation.Domain.Users;

public sealed record UserRegistered(Guid UserId, string Email);

public sealed record UserAggregate(Guid Id, string Email)
{
    public static UserAggregate Create(UserRegistered @event) =>
        new(@event.UserId, @event.Email);
}

public sealed record UserProfile(Guid Id, string Email)
{
    public static UserProfile Create(UserRegistered @event) =>
        new(@event.UserId, @event.Email);
}
