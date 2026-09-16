using System.Security.Cryptography;
using System.Text;
using CqrsFoundation.Domain.Common;
using Marten;

namespace CqrsFoundation.Infrastructure;

public sealed record CommandReceipt(
    Guid Id,
    Guid ActorId,
    string Fingerprint,
    Guid? ResourceId);

public static class CommandIdempotency
{
    public static string Fingerprint(string operation, params string[] values)
    {
        var builder = new StringBuilder();
        AppendPart(builder, operation);
        foreach (var value in values)
        {
            AppendPart(builder, value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static Guid NewResourceId(Guid actorId, string operation, CommandMetadata metadata) =>
        metadata.IdempotencyKey is null
            ? Guid.NewGuid()
            : DeterministicGuid("resource", actorId, operation, metadata.IdempotencyKey);

    public static async Task<CommandReceipt?> LoadExisting(
        IDocumentSession session,
        Guid actorId,
        CommandMetadata metadata,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (metadata.IdempotencyKey is null)
        {
            return null;
        }

        var receipt = await session.LoadAsync<CommandReceipt>(
            ReceiptId(actorId, metadata.IdempotencyKey),
            cancellationToken);
        return RequireMatching(receipt, fingerprint);
    }

    public static void Stage(
        IDocumentSession session,
        Guid actorId,
        CommandMetadata metadata,
        string fingerprint,
        Guid? resourceId = null)
    {
        if (metadata.IdempotencyKey is null)
        {
            return;
        }

        session.Insert(new CommandReceipt(
            ReceiptId(actorId, metadata.IdempotencyKey),
            actorId,
            fingerprint,
            resourceId));
    }

    public static async Task<CommandReceipt?> RecoverCommitted(
        IDocumentStore store,
        string tenancyId,
        Guid actorId,
        CommandMetadata metadata,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        if (metadata.IdempotencyKey is null)
        {
            return null;
        }

        await using var query = store.QuerySession(tenancyId);
        var receipt = await query.LoadAsync<CommandReceipt>(
            ReceiptId(actorId, metadata.IdempotencyKey),
            cancellationToken);
        return RequireMatching(receipt, fingerprint);
    }

    public static Guid RequireResourceId(CommandReceipt receipt) =>
        receipt.ResourceId
        ?? throw new InvalidOperationException("The idempotency receipt does not contain a resource id.");

    private static CommandReceipt? RequireMatching(CommandReceipt? receipt, string fingerprint)
    {
        if (receipt is null)
        {
            return null;
        }

        if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new BusinessRuleException("The idempotency key was already used for a different command.");
        }

        return receipt;
    }

    private static Guid ReceiptId(Guid actorId, string idempotencyKey) =>
        DeterministicGuid("receipt", actorId, string.Empty, idempotencyKey);

    private static Guid DeterministicGuid(string purpose, Guid actorId, string operation, string idempotencyKey)
    {
        var material = string.Concat(
            purpose,
            "\0",
            actorId.ToString("D"),
            "\0",
            operation,
            "\0",
            idempotencyKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);

        // RFC 9562 version 8 is reserved for application-defined UUID layouts.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static void AppendPart(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');
}
