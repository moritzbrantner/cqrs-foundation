# cqrs-foundation

A small, opinionated .NET 10 foundation for business software using strict CQRS, event sourcing, multitenancy, authentication, and auditable history.

## What is included

- ASP.NET Core / C# 14
- PostgreSQL + Marten 9
- event-sourced users, tenant membership/roles, and an example `Customer` aggregate
- inline read projections separated from write aggregates
- Marten optimistic stream concurrency through `FetchForWriting<T>()`
- conjoined tenant isolation
- JWT bearer authentication with password hashing via ASP.NET Core Identity primitives
- immutable event history with actor, correlation, causation, and tenant metadata
- Problem Details error handling
- PostgreSQL Docker Compose setup
- unit tests plus a real PostgreSQL/Marten projection-isolation test in CI

It intentionally does **not** contain MediatR, generic repositories, a unit-of-work abstraction, an event-store wrapper, or a generic aggregate hierarchy.

## Architecture

```text
WRITE                         READ                         AUDIT

Command                       Query                        History query
   |                            |                              |
   v                            v                              v
Aggregate/decider          Read projection               Event stream
   |
   v
Events
   |
   +----------------------> inline projection
```

Commands and queries are separated in `Application/`. Commands append events; queries only read projections/history. See [`docs/architecture.md`](docs/architecture.md) and [`AGENTS.md`](AGENTS.md) for the rules.

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
  -d '{"email":"alice@local.dev","password":"alice123"}'
```

Use the returned bearer token to create a tenant:

```bash
curl -X POST http://localhost:5000/api/tenants \
  -H 'authorization: Bearer <token>' \
  -H 'content-type: application/json' \
  -d '{"name":"Acme"}'
```

For tenant-scoped requests add the returned tenant id:

```text
Authorization: Bearer <token>
X-Tenant-Id: <tenant-guid>
```

You can then manage tenant membership and roles, create/rename/deactivate customers, query customer projections, and inspect `/api/customers/{id}/history` independently of the current read model.

## Persistence policy

Marten's event store is the source of truth for business state. Password hashes are deliberately kept out of immutable events in the global `Credential` document. Read models are rebuildable projections and should never become the source of truth.
