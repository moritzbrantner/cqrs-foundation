# Agent rules

This repository is deliberately small. Preserve the architectural boundaries instead of adding frameworks around them.

## Commands

- Commands may load the current event-sourced aggregate and append events.
- Existing streams must use Marten `FetchForWriting<T>()` so optimistic concurrency remains explicit.
- Domain decisions return events. Do not mutate the aggregate returned by `FetchForWriting<T>()`.
- Application handlers are the authoritative authorization boundary. Middleware/endpoints may reject early, but direct handler invocation must remain safe.
- Authorization checks named permissions. Roles only bundle permissions; do not branch command behavior directly on role names.
- Mutable resource reads expose the current event-stream version. Existing-resource writes may carry an expected version from HTTP `If-Match`; reject stale expected versions before applying domain events.
- Keep Marten's normal optimistic concurrency check even when a client expected version is supplied. The client precondition does not replace commit-time concurrency protection.
- A no-op command with an expected version must still assert stream consistency before succeeding.
- Apply actor, correlation, and causation metadata to every write session.
- Preserve an incoming `X-Correlation-Id` across related HTTP commands; use the individual request trace identifier as causation.
- `Idempotency-Key` is optional. When present, store the command receipt in the same Marten transaction as the command's events/documents.
- Check the actor's current permission before replaying an idempotency receipt. A receipt is never an authorization capability.
- Scope receipts by tenant and actor. A matching key may replay only the same versioned command fingerprint; changed command input must fail closed.
- Include an expected resource version in an idempotency fingerprint when the command uses one. A committed receipt replays before re-evaluating its now-stale expected version.
- Resource-creating commands may derive a stable resource id only when an idempotency key is present so concurrent retries converge on the same identity.
- Keep versioned idempotency operation names stable. If the fingerprint semantics change incompatibly, increment the operation version.
- Never put passwords, hashes, MFA secrets, access tokens, refresh tokens, or other authentication secrets into immutable events or idempotency receipts.

## Queries

- Queries read projections or event history only.
- Query handlers authoritatively check the actor's named read permission before returning tenant data.
- Single mutable resource queries return the event-stream version together with the projection so HTTP can emit a strong ETag without changing the response body shape.
- Query handlers never append events or call command handlers.
- Do not load write aggregates merely to shape API responses.

## Tenancy

- Every tenant-owned stream or projection must use a tenant-scoped Marten session.
- `X-Tenant-Id` is the HTTP tenant selector for the MVP; authorization must still verify membership and permission server-side.
- Global authentication credentials are the intentional exception and remain single-tenanted documents.

## Simplicity

Do not add generic repositories, `IUnitOfWork`, mediator wrappers, aggregate base classes, or generic command/query buses unless a concrete requirement makes them necessary.
