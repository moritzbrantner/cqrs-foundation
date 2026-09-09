# Architecture

The foundation has three independent paths.

```text
command -> aggregate -> events -> inline projection
                         |
                         +-> immutable history

query ---------------------> projection
```

## Write path

Commands use a tenant-scoped Marten `IDocumentSession`. New aggregates start a typed stream. Existing aggregates use `FetchForWriting<T>()`, then the domain decides which events to append. Marten performs optimistic stream concurrency when the session commits.

Aggregates are immutable projections of their streams. Domain methods return events rather than changing the loaded aggregate instance.

## Read path

Queries use `IQuerySession` and persisted inline projections such as `CustomerView`, `TenantView`, and `UserProfile`. API responses do not depend on loading write aggregates.

## Audit path

The event stream is the canonical history. Event metadata stores the tenant automatically and opts into correlation, causation, username, and headers. The foundation adds an `actor_id` header on writes.

## Authentication and users

Passwords are not events. `Credential` is a normal Marten document containing the password hash and is globally scoped. User registration also creates an event-sourced `UserAggregate`/`UserProfile` stream in the reserved `system` tenancy.

The MVP issues local JWT bearer tokens. This is an authentication boundary rather than a domain dependency; a production application can replace token issuance with an external identity provider without changing tenant or business streams.

## Tenancy

Business streams and projections use Marten conjoined tenancy. A request may select a tenant with `X-Tenant-Id`; middleware loads that tenant's projection and verifies that the authenticated user is a member before tenant-scoped endpoints run.

Tenant membership and roles are themselves event-sourced. The owner/admin authorization check for membership changes is repeated inside the command handler against the current `TenantAggregate`, not trusted solely from middleware state.

## Deliberate non-features

There is no MediatR, generic repository, unit-of-work wrapper, event-store wrapper, aggregate base class, or separate microservice. Marten is the persistence/event-sourcing boundary and ASP.NET Core is the HTTP/authentication boundary.
