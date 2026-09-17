# Querying collections

Single resources remain ordinary `GET` endpoints. Collections that need filters or paging use the explicit HTTP `QUERY` method so the query shape is carried in a request body without turning the URL into an unbounded ad-hoc filter surface.

Customer collection query:

```http
QUERY /api/customers
Authorization: Bearer <token>
X-Tenant-Id: <tenant-guid>
Content-Type: application/json

{
  "namePrefix": "Ac",
  "isActive": true,
  "offset": 0,
  "limit": 25
}
```

The response is bounded and includes the next offset only when another page exists:

```json
{
  "items": [
    {
      "id": "...",
      "name": "Acme",
      "isActive": true
    }
  ],
  "nextOffset": 25
}
```

`limit` defaults to 25 and cannot exceed 100. `offset` cannot be negative. A blank `namePrefix` is normalized to no name filter. Results are ordered by `Name` and then `Id`, giving deterministic tie-breaking for a fixed data set. No total count is calculated by default because that would add a separate database operation without a demonstrated UI requirement.

This first paging contract uses offsets deliberately because it is simple and transparent. It is not snapshot isolation: inserts, renames, or deletes between requests can shift later pages. If a concrete workload requires stable continuation under concurrent mutations, replace the offset with a keyset/continuation token rather than pretending this contract already provides snapshot semantics.
