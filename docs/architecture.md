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

Successful mutable-resource commands return the stream version produced by that command. HTTP exposes that version as the next strong ETag, including on `201 Created` and `204 No Content` responses, so a client can continue editing without an extra read just to discover the new version. No-op commands return the unchanged version only after a stream consistency assertion succeeds.

Every write carries explicit command metadata. The API preserves an incoming `X-Correlation-Id` across related commands, otherwise starts correlation from the ASP.NET Core request trace identifier. The individual request trace identifier is recorded as causation. Actor, correlation, and causation are stored with the appended events, and correlation is echoed in the response header.

### Client concurrency preconditions

Single mutable-resource reads expose the current event-stream version as a strong numeric HTTP ETag such as `"3"`. Existing-resource writes may send that value back in `If-Match`. The header is optional so unconditional commands remain supported, but when present the application rejects a stale stream version with HTTP 412 before applying domain events.

The explicit client precondition does not replace Marten's normal optimistic concurrency. `FetchForWriting<T>()` still pins the version observed by the command and `SaveChangesAsync()` still detects a race that happens after the precondition was checked. No-op commands that return a version also force a stream consistency assertion before succeeding.

The foundation keeps Marten 9's default Quick append mode. Stream versions are read from event-stream state for queries and from the `FetchForWriting<T>()` stream handle for mutations rather than switching inline projections to Rich mode merely to persist a version member on each projected document.

### Authorization

Permissions are the authorization primitive. Built-in tenant roles bundle named permissions such as `customers.read`, `customers.write`, `members.read`, `members.manage`, and `audit.read`; handlers ask for permissions rather than branching on role names.

Application handlers are authoritative. The HTTP tenant middleware and endpoints perform the same checks as an early rejection optimization, but direct handler invocation does not depend on them. Tenant-scoped command handlers validate permission from the event-sourced `TenantAggregate`; business writes pin the tenant stream version so a concurrent membership/role change invalidates the command transaction. Query handlers validate permission from the inline `TenantView` before returning tenant data.

The tenant owner remains a domain lifecycle concept: the owner cannot be removed or demoted even though owner/admin currently share the ordinary management permission bundle.

### Retry idempotency

Write requests may additionally provide `Idempotency-Key`. The key is bounded at the HTTP boundary and is scoped by Marten tenancy plus the current actor. A command stores a `CommandReceipt` in the same Marten transaction as its business events/documents. The receipt contains a versioned command fingerprint, the original mutation result version, and, for create commands, the created resource id.

Authorization is checked before receipt lookup. A retry therefore still requires the actor's current permission; an old receipt is not a capability after access has been revoked. Matching receipt replays also assert that the authorization stream has not changed during the replay check.

When a command uses a client expected version, that version is part of the idempotency fingerprint. A retry of an already committed command replays its receipt before evaluating the now-stale resource version, which is required for safe retry after a lost response. Replay returns the original command's result version even if subsequent commands have advanced the resource stream.

A retry with the same key and fingerprint returns the previously committed outcome without executing the domain decision again. Reusing the key with different command input fails closed. Concurrent duplicates converge through the same receipt identity and, for resource creation, the same deterministic UUID; if one request loses a commit race it re-reads the committed receipt before surfacing the persistence failure.

Registration follows the same rule without storing passwords, password-derived fingerprints, or access tokens in the receipt. A registration replay loads the existing credential and verifies the supplied password against its normal password hash before issuing a fresh access token. Registration receipts do not need a mutable-resource result version because registration does not participate in this ETag contract.

Without `Idempotency-Key`, command behavior and randomly generated resource identifiers remain unchanged.

## Read path

Queries use `IQuerySession` and persisted inline projections such as `CustomerView`, `TenantView`, and `UserProfile`. API responses do not depend on loading write aggregates. Tenant query handlers authorize the actor before exposing the requested projection/history data. Single mutable-resource queries pair the projection with the canonical event-stream version so HTTP can emit an ETag without changing the JSON body shape.

Collection queries are explicit and bounded. A collection that needs filtering or paging uses an HTTP `QUERY` operation with a request body; single resources and small fixed collections remain `GET`. The customer query normalizes its filters, enforces a default limit of 25 and a hard maximum of 100, orders by `Name` and then `Id` for deterministic tie-breaking, and fetches one extra row only to decide whether a continuation cursor exists. It deliberately does not calculate a total count by default.

Continuation is keyset-based rather than offset-based. The opaque cursor stores the last `Name + Id` tuple together with the tenant and normalized filters, so inserts or deletes before the cursor do not shift the next page and a cursor cannot silently be reused against another tenant/filter shape. The cursor is versioned so future ordering changes can fail closed instead of being interpreted under new semantics.

Keyset continuation is not snapshot isolation because `Name` is mutable. A rename across the cursor boundary between requests can move a row from one side of the seek position to the other. Workloads that require snapshot-stable traversal need either an immutable ordering key or an explicit snapshot/read-version mechanism rather than additional hidden paging state.

## Audit path

The event stream is the canonical history. Event metadata stores the tenant automatically and opts into correlation, causation, username, and headers. The foundation adds an `actor_id` header on writes. History responses expose actor, correlation, and causation independently of the current read model.

## Authentication and users

Passwords are not events. `Credential` is a normal Marten document containing the password hash and is globally scoped. User registration also creates an event-sourced `UserAggregate`/`UserProfile` stream in the reserved `system` tenancy.

The MVP issues local JWT bearer tokens. This is an authentication boundary rather than a domain dependency; a production application can replace token issuance with an external identity provider without changing tenant or business streams.

## Tenancy

Business streams and projections use Marten conjoined tenancy. A request may select a tenant with `X-Tenant-Id`; middleware loads that tenant's projection and verifies that the authenticated user is a member before tenant-scoped endpoints run.

Tenant membership and roles are themselves event-sourced. Membership-changing handlers repeat authorization against the current `TenantAggregate`; they do not trust middleware state or an idempotency receipt as authority.

## Deliberate non-features

There is no MediatR, generic repository, unit-of-work wrapper, event-store wrapper, aggregate base class, or separate microservice. Marten is the persistence/event-sourcing boundary and ASP.NET Core is the HTTP/authentication boundary.
