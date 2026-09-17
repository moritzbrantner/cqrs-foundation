using System.Text;
using System.Text.Json;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;

namespace CqrsFoundation.Application;

internal sealed record CustomerCursorPosition(string Name, Guid Id);

internal static class CustomerQueryCursor
{
    private const string Prefix = "v1:";

    public static string Encode(
        Guid tenantId,
        CustomerListQuery query,
        CustomerView lastItem)
    {
        var payload = new CursorPayload(
            tenantId,
            lastItem.Name,
            lastItem.Id,
            query.NamePrefix,
            query.IsActive);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Prefix + ToBase64Url(json);
    }

    public static CustomerCursorPosition Decode(
        string cursor,
        Guid tenantId,
        CustomerListQuery query)
    {
        try
        {
            if (!cursor.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidQueryException("Customer continuation cursor is invalid.");
            }

            var payload = JsonSerializer.Deserialize<CursorPayload>(
                FromBase64Url(cursor[Prefix.Length..]))
                ?? throw new InvalidQueryException("Customer continuation cursor is invalid.");

            if (payload.TenantId != tenantId ||
                !string.Equals(payload.NamePrefix, query.NamePrefix, StringComparison.Ordinal) ||
                payload.IsActive != query.IsActive ||
                string.IsNullOrEmpty(payload.Name))
            {
                throw new InvalidQueryException(
                    "Customer continuation cursor does not match the current query.");
            }

            return new CustomerCursorPosition(payload.Name, payload.Id);
        }
        catch (InvalidQueryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidQueryException("Customer continuation cursor is invalid.");
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string encoded)
    {
        var base64 = encoded
            .Replace('-', '+')
            .Replace('_', '/');
        var remainder = base64.Length % 4;
        if (remainder != 0)
        {
            base64 = base64.PadRight(base64.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(base64);
    }

    private sealed record CursorPayload(
        Guid TenantId,
        string Name,
        Guid Id,
        string? NamePrefix,
        bool? IsActive);
}
