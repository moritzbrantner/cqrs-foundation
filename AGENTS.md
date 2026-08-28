# Agent rules

This repository is deliberately small. Preserve the architectural boundaries instead of adding frameworks around them.

## Commands

- Commands may load the current event-sourced aggregate and append events.
- Existing streams must use Marten `FetchForWriting<T>()` so optimistic concurrency remains explicit.
- Domain decisions return events. Do not mutate the aggregate returned by `FetchForWriting<T>()`.
- Apply actor and correlation metadata to every write session.
- Never put passwords, hashes, MFA secrets, access tokens, or refresh tokens into immutable events.

## Queries

- Queries read projections or event history only.
- Query handlers never append events or call command handlers.
- Do not load write aggregates merely to shape API responses.

## Tenancy

- Every tenant-owned stream or projection must use a tenant-scoped Marten session.
- `X-Tenant-Id` is the HTTP tenant selector for the MVP; authorization must still verify membership server-side.
- Global authentication credentials are the intentional exception and remain single-tenanted documents.

## Simplicity

Do not add generic repositories, `IUnitOfWork`, mediator wrappers, aggregate base classes, or generic command/query buses unless a concrete requirement makes them necessary.
