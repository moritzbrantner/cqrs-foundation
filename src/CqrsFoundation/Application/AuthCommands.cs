using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Users;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace CqrsFoundation.Application;

public sealed record RegisterUser(string Email, string Password);
public sealed record LoginUser(string Email, string Password);
public sealed record AuthResult(Guid UserId, string Email, string AccessToken);

public static class RegisterUserHandler
{
    public static async Task<AuthResult> Handle(
        RegisterUser command,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(command.Email);
        ValidatePassword(command.Password);

        await using var session = store.LightweightSession(SystemTenancy.Id);
        var existing = await session.Query<Credential>()
            .Where(x => x.Email == email)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            throw new BusinessRuleException("A user with this email already exists.");
        }

        var userId = Guid.NewGuid();
        var credential = new Credential
        {
            Id = userId,
            Email = email
        };
        credential.PasswordHash = passwordHasher.HashPassword(credential, command.Password);

        AuditMetadata.Apply(session, userId, correlationId);
        session.Store(credential);
        session.Events.StartStream<UserAggregate>(userId, new UserRegistered(userId, email));
        await session.SaveChangesAsync(cancellationToken);

        return new AuthResult(userId, email, tokenService.Issue(userId, email));
    }

    private static string NormalizeEmail(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length == 0 || !normalized.Contains('@', StringComparison.Ordinal))
        {
            throw new BusinessRuleException("A valid email address is required.");
        }

        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 8)
        {
            throw new BusinessRuleException("Password must contain at least 8 characters.");
        }
    }
}

public static class LoginUserHandler
{
    public static async Task<AuthResult> Handle(
        LoginUser command,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        CancellationToken cancellationToken)
    {
        var email = command.Email.Trim().ToLowerInvariant();
        await using var query = store.QuerySession(SystemTenancy.Id);
        var credential = await query.Query<Credential>()
            .Where(x => x.Email == email)
            .FirstOrDefaultAsync(cancellationToken);

        if (credential is null ||
            passwordHasher.VerifyHashedPassword(credential, credential.PasswordHash, command.Password) == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        return new AuthResult(credential.Id, credential.Email, tokenService.Issue(credential.Id, credential.Email));
    }
}
