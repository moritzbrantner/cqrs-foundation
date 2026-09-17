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
  "cursor": null,
  "limit": 25
}
```

The response is bounded and includes an opaque continuation cursor only when another page exists:

```json
{
  "items": [
    {
      "id": "...",
      "name": "Acme",
      "isActive": true
    }
  ],
  "nextCursor": "v1:..."
}
```

Send `nextCursor` back as `cursor` with the same filters to continue. The cursor is versioned, tenant-bound, and filter-bound; malformed cursors or cursors reused with a different tenant/filter shape fail as invalid queries rather than silently seeking into a different result set.

`limit` defaults to 25 and cannot exceed 100. A blank `namePrefix` is normalized to no name filter. Results are ordered by `Name` and then `Id`, with the unique id providing deterministic tie-breaking. Continuation uses a keyset predicate on that same tuple instead of `Skip`, so inserting or deleting rows before the current cursor does not shift later pages. No total count is calculated by default because that would add a separate database operation without a demonstrated UI requirement.

Keyset paging is stronger than offset paging but is still not snapshot isolation. `Name` is mutable, so renaming a row across the cursor boundary between page requests can make that row appear later again or disappear from a later page. If a workload needs snapshot-stable traversal under sort-key mutation, it needs an immutable ordering key or an explicit snapshot/read-version mechanism rather than pretending this cursor provides one.
