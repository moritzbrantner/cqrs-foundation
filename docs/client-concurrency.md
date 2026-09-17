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

Successful mutable-resource commands also return the stream version they produced as the response `ETag`. A client can therefore chain edits without issuing a read only to discover the new version. No-op commands return the unchanged version after asserting stream consistency.

This client precondition is additive: `FetchForWriting<T>()` still pins the stream version observed by the command, so a race after the precondition check is detected when the Marten session commits.

If the command also uses `Idempotency-Key`, the expected version is part of the command fingerprint. The receipt stores the version produced by the original command. An already committed retry is recognized before the now-stale expected version is evaluated and returns that original result version even if later commands have advanced the stream.
