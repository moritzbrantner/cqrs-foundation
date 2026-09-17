# Client concurrency

Mutable resource reads expose the canonical event-stream version as a strong numeric `ETag`.

```text
ETag: "3"
```

A client that wants stale-edit protection sends the version it read on the subsequent existing-resource write:

```text
If-Match: "3"
```

`If-Match` is optional. When it is absent, the command remains unconditional apart from Marten's normal commit-time optimistic concurrency. When it is present, the application compares it with the current event-stream version and rejects a mismatch with HTTP 412 before applying domain events.

Only one strong numeric ETag is accepted. Weak tags, wildcard matching, and ETag lists are intentionally outside this foundation's command contract.

This client precondition is additive: `FetchForWriting<T>()` still pins the stream version observed by the command, so a race after the precondition check is detected when the Marten session commits.

If the command also uses `Idempotency-Key`, the expected version is part of the command fingerprint. An already committed retry is recognized by its receipt before the now-stale expected version is evaluated, allowing safe retry after a lost response without weakening current authorization checks.
