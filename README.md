# cqrs-foundation

A small, opinionated .NET 10 foundation for business software using strict CQRS, event sourcing, multitenancy, authentication, and auditable history.

## What is included

- ASP.NET Core / C# 14
- PostgreSQL + Marten 9
- event-sourced users, tenant membership/roles, and an example `Customer` aggregate
- named tenant permissions with roles as permission bundles
- handler-authoritative authorization for both commands and tenant queries
- inline read projections separated from write aggregates
- Marten optimistic stream concurrency through `FetchForWriting<T>()`
- conjoined tenant isolation
- JWT bearer authentication with password hashing via ASP.NET Core Identity primitives
- immutable event history with actor, correlation, causation, and tenant metadata
- caller-propagated `X-Correlation-Id` with request-specific causation
- transactional `Idempotency-Key` receipts for safe command retries
- Problem Details error handling
- PostgreSQL Docker Compose setup
- unit tests plus real PostgreSQL/Marten integration coverage in CI

It intentionally does **not** contain MediatR, generic repositories, a unit-of-work abstraction, an event-store wrapper, or a generic aggregate hierarchy.

## Architecture

```text
WRITE                         READ                         AUDIT

Command                       Query                        History query
   |                            |                              |
   v                            v                              v
permission check           permission check              permission check
   |                            |                              |
   v                            v                              v
Aggregate/decider          Read projection               Event stream
   |
   v
Events
   |
   +----------------------> inline projection
```

Commands and queries are separated in `Application/`. Commands append events; queries only read projections/history. Tenant permissions are checked inside handlers, so middleware and endpoint checks are only fast-fail optimizations. See [`docs/architecture.md`](docs/architecture.md) and [`AGENTS.md`](AGENTS.md) for the rules.

## Run locally

```bash
cp .env.example .env
docker compose up -d
dotnet run --project src/CqrsFoundation
```

The checked-in `appsettings.json` contains development-only credentials and a development JWT key. Override all secrets in a real deployment.

## MVP flow

Register a user:

```bash
curl -X POST http://localhost:5000/api/auth/register \
  -H 'content-type: application/json' \
  -H 'Idempotency-Key: register-alice-1' \
  -d '{"email":"alice@local.dev","password":"alice123"}'
```

Use the returned bearer token to create a tenant:

```bash
curl -X POST http://localhost:5000/api/tenants \
  -H 'authorization: Bearer <token>' \
  -H 'content-type: application/json' \
  -H 'Idempotency-Key: create-acme-1' \
  -d '{"name":"Acme"}'
```

For tenant-scoped requests add the returned tenant id:

```text
Authorization: Bearer <token>
X-Tenant-Id: <tenant-guid>
```

For a business operation that spans multiple commands, send the same correlation id on each write:

```text
X-Correlation-Id: <operation-id>
```

If omitted, the request trace identifier starts the correlation. Each write records that request trace separately as causation and echoes the effective `X-Correlation-Id` response header.

For a command that may be retried after a timeout or lost response, send a stable key for that one logical command:

```text
Idempotency-Key: <unique-command-key>
```

The key is optional and scoped to the current tenant and actor. The same key with the same command returns the already committed outcome instead of appending another event; using it for different command input is rejected. The receipt is committed atomically with the business write. Current permission is checked before replay, so revoking access also prevents an old key from being reused as authority. Registration retries revalidate the password and issue a fresh access token rather than storing authentication secrets in the receipt.

You can then manage tenant membership and roles, create/rename/deactivate customers, query customer projections, and inspect `/api/customers/{id}/history` independently of the current read model. History includes actor, correlation, and causation metadata for each event.

## Persistence policy

Marten's event store is the source of truth for business state. Password hashes are deliberately kept out of immutable events in the global `Credential` document. Read models are rebuildable projections and should never become the source of truth.
