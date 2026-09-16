using System.Data.Common;
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
    private const string Operation = "auth.register.v1";
    private static readonly Guid IdempotencyActor = Guid.Empty;

    public static async Task<AuthResult> Handle(
        RegisterUser command,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(command.Email);
        ValidatePassword(command.Password);
        var fingerprint = CommandIdempotency.Fingerprint(Operation, email);

        await using var session = store.LightweightSession(SystemTenancy.Id);
        var receipt = await CommandIdempotency.LoadExisting(
            session,
            IdempotencyActor,
            metadata,
            fingerprint,
            cancellationToken);
        if (receipt is not null)
        {
            return await ReplayRegistration(
                receipt,
                command.Password,
                store,
                passwordHasher,
                tokenService,
                cancellationToken);
        }

        var existing = await session.Query<Credential>()
            .Where(x => x.Email == email)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            throw new BusinessRuleException("A user with this email already exists.");
        }

        var userId = CommandIdempotency.NewResourceId(IdempotencyActor, Operation, metadata);
        var credential = new Credential
        {
            Id = userId,
            Email = email
        };
        credential.PasswordHash = passwordHasher.HashPassword(credential, command.Password);

        AuditMetadata.Apply(session, userId, metadata);
        session.Insert(credential);
        session.Events.StartStream<UserAggregate>(userId, new UserRegistered(userId, email));
        CommandIdempotency.Stage(session, IdempotencyActor, metadata, fingerprint, userId);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            var recovered = await CommandIdempotency.RecoverCommitted(
                store,
                SystemTenancy.Id,
                IdempotencyActor,
                metadata,
                fingerprint,
                cancellationToken);
            if (recovered is not null)
            {
                return await ReplayRegistration(
                    recovered,
                    command.Password,
                    store,
                    passwordHasher,
                    tokenService,
                    cancellationToken);
            }

            if (exception.GetBaseException() is DbException { SqlState: "23505" })
            {
                throw new BusinessRuleException("A user with this email already exists.");
            }

            throw;
        }

        return new AuthResult(userId, email, tokenService.Issue(userId, email));
    }

    private static async Task<AuthResult> ReplayRegistration(
        CommandReceipt receipt,
        string password,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        CancellationToken cancellationToken)
    {
        var userId = CommandIdempotency.RequireResourceId(receipt);
        await using var query = store.QuerySession(SystemTenancy.Id);
        var credential = await query.LoadAsync<Credential>(userId, cancellationToken)
            ?? throw new InvalidOperationException("The registration receipt references a missing credential.");

        if (passwordHasher.VerifyHashedPassword(credential, credential.PasswordHash, password) == PasswordVerificationResult.Failed)
        {
            throw new BusinessRuleException(
                "The idempotency key was already used with different registration credentials.");
        }

        return new AuthResult(
            credential.Id,
            credential.Email,
            tokenService.Issue(credential.Id, credential.Email));
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
