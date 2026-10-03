# stockroom design notes

## Layering: four projects, one dependency rule

| Project | Knows about | Why |
| --- | --- | --- |
| `Inventory.Domain` | nothing (no NuGet packages) | Business rules can be unit tested without I/O. |
| `Inventory.Application` | Domain, FluentValidation, logging abstractions | Use cases (place order, receive stock...) plus *ports*: `IProductRepository`, `IStockRepository`, `IOrderRepository`, `IAlertRepository`, `IUnitOfWork`. |
| `Inventory.Infrastructure` | Application, EF Core, SQLite, Npgsql | Implements the ports and translates EF/provider exceptions into application exceptions. |
| `Inventory.Api` | everything (composition root) | HTTP only: routing, validation filter, auth, ProblemDetails, OpenAPI, health. |

**Alternatives considered.**

- *Single project with folders*: less ceremony, but nothing stops a controller from using `DbContext`
  directly. Then the invariants live in two places.
- *Application depends on `DbContext` directly* (a popular pragmatic variant): fewer files, but
  Application tests would need a database, and `DbUpdateConcurrencyException` would leak into use-case
  code. The repositories here are deliberately thin.
- *MediatR / CQRS pipeline*: adds indirection that a service with this many use cases does not need.
  Plain service classes are easier to trace when reading the code.

## Modelling stock: `StockItem` per (product, location)

`OnHand` is the physical count. `Reserved` is the part promised to open orders. `Available` is
derived, never stored. Every mutator (`Receive`, `Reserve`, `ReleaseReservation`, `ShipReserved`,
`RecordCount`) checks its precondition *before* changing state, so a failed call leaves the object
untouched. The invariant `0 <= Reserved <= OnHand` is also a pair of database CHECK constraints. That
is defence in depth in case someone writes SQL by hand (the recorded PostgreSQL constraint test rejects
`UPDATE "StockItems" SET "OnHand" = -1`).

**Capacity and overflow.** One location holds at most `StockItem.MaxQuantityPerLocation`
(100,000,000) units. `Receive` beyond that fails with 409 `location_capacity_exceeded`, and a count
above it fails validation (400). Totals across locations are summed as `long` in `AllocationPolicy`,
`LowStockMonitor` and `ProductResponse`. Summing `int`s with LINQ's checked `Sum` can make
a large count return 500 *after* the write commits and make every later read of the
product return 500. The regression tests use `int.MaxValue` inputs and 30 full locations (3 billion
units).

**Reservation timing.** Reserving on placement keeps on-hand stock aligned with the physical shelf
count until picking. Cancelling is a pure release, and cycle counts can
reconcile `OnHand` without touching commitments.

## All-or-nothing orders

`Order.Place` merges duplicate lines, asks the pure `AllocationPolicy` to plan *every* line (which may
throw `InsufficientStockException`), and only then calls `Reserve` on the planned stock items. A
2-line order whose second line is short therefore reserves nothing. Unit tests and an integration
test cover this.

Allocation is greedy "largest available location first", with ties broken by location code. That
keeps split picks low and is deterministic, which makes it testable. Alternatives: FIFO/FEFO by
receipt date (needs lots and expiry data), or nearest-to-dock (needs a layout model).

## Concurrency: optimistic tokens + retry

The race: two requests read "10 available", both reserve 7, both save, and 14 are reserved against
10. Options considered:

| Option | Verdict |
| --- | --- |
| Pessimistic locks (`SELECT ... FOR UPDATE`) | Not portable to SQLite. It holds locks across application code and risks deadlocks with multi-line orders. |
| Serializable transactions | PostgreSQL would surface serialization failures, which need retrying anyway. SQLite semantics differ. |
| Atomic SQL (`UPDATE ... SET Reserved = Reserved + @q WHERE OnHand - Reserved >= @q`) | Fast, but moves the business rule into SQL and is awkward for multi-line, multi-location allocation. |
| **Optimistic concurrency token + retry** (chosen) | Portable, keeps rules in C#, and no locks are held during allocation planning. |

Each `StockItem` and `Order` has an integer `Version` that the domain bumps on every mutation. EF
Core configures it with `IsConcurrencyToken()`, so updates become
`UPDATE ... WHERE "Id" = @id AND "Version" = @original`. Zero affected rows make EF throw
`DbUpdateConcurrencyException`. `EfUnitOfWork` converts that to `ConcurrencyConflictException`.
`ConcurrencyRetry` clears the change tracker and re-runs the whole read-plan-write operation with
jittered back-off (default 5 attempts). A retried reservation re-reads stock, so it either fits in
what is left or fails cleanly with `insufficient_stock`.

A portable `int Version`, rather than SQL Server `rowversion` or PostgreSQL `xmin`, lets one
model work on both providers.

**Product-level token (write skew).** Row tokens only protect the rows a request *writes*. A cycle
count at A-01 and one at B-01 each write a different row but read both to decide on the low-stock
alert. Without more protection both can commit a decision based on a stale total. The measured
outcomes were a missed alert (each count saw 22 available, the true total was 4) or two "raise"
decisions, where the unique index then rejected a perfectly valid count with 409. So `Product` also
has a `Version`, bumped by threshold changes and by `LowStockMonitor` on every stock change for that
product. Any two writers to one product's stock now conflict and re-decide. The cost is that stock
writes to one SKU are serialised even across locations (see [README limitations](README.md#limitations)). Locking only the product
row with `SELECT ... FOR UPDATE` would serialise the same way, but is not portable to SQLite.

**Unique-index races are retryable too.** EF orders the commands in `SaveChanges`, so a racing
request can hit a unique index (`IX_LowStockAlerts_ProductId_Open`, or
`IX_StockItems_ProductId_LocationCode` for two first receipts into a new bin) *before* its
version-checked UPDATE. `EfUnitOfWork` identifies the index: PostgreSQL gives
`PostgresException.ConstraintName`, and SQLite gives extended code 2067 plus the column list in the
message. It turns these two into `ConcurrencyConflictException`, so they are retried, and a
duplicate SKU into 409 `duplicate_sku`. Measured on PostgreSQL without this mapping, the double-raise
test and the new-bin receipt test each failed 5 of 5 runs.

**Read order matters when the domain checks consistency.** Fulfil and cancel read the order's stock
rows *before* the order itself. The winner of a fulfil-vs-cancel race commits the order and its stock
in one transaction, so if the loser's stock read already sees that commit, its later order read sees
it too and it gets a clean 409 `invalid_order_state`. Reading the order first lets the
loser pair a still-open order with already-released stock, and `StockItem` rejects that
impossible mix with `reservation_mismatch` before the version check at commit could trigger a retry.
No data is corrupted, but the error code is wrong. The regression test
`ConcurrentFulfilAndCancelOfOneOrder_ExactlyOneWins` fails in 4 of 5 recorded runs with that read order.

Measured evidence: with retries disabled, the concurrency suite failed in 10 of 10 runs with
`concurrency_conflict`, and the two-request test alone failed in a varying 4-6 of 10 runs across three
sessions. So the conflicts are real and the tokens catch them. With retries enabled, 10 of 10 runs
passed. The HTTP write-skew regression is probabilistic too (10 iterations of 2 racing requests): with the
product-token bump removed it failed 5/5, 9/10 and 10/10 in three sessions. The unit test
`EveryEvaluation_BumpsProductVersion_SoConcurrentEvaluationsConflict` plus `ModelTests` catch the
removal deterministically.

## Low-stock alerts

`LowStockPolicy.Evaluate(available, threshold, hasOpenAlert)` is a pure, edge-triggered rule: raise
once, resolve when stock recovers. `LowStockMonitor` runs inside the same unit of work as every
operation that can change the answer: stock changes, threshold changes, and product creation, since a
new product with a threshold above zero is low from the start. So an alert can never exist without
the change that caused it, and the reverse also holds. The decision reads all of a product's
locations, so concurrent decisions are serialised by the product-level token (see Concurrency). A partial
unique index (`ProductId WHERE ResolvedAt IS NULL`) is the database backstop for "at most one open
alert per product". A request that loses that race is retried, not rejected. The filter SQL is valid
on both SQLite and PostgreSQL.

Alternative: domain events plus an outbox for notifications (email, Slack). That is the right next
step when alerts need to leave the process, and overkill while they are only queried.

## Two providers, two migration histories

EF migrations are provider-specific (column types, identity strategy). The model lives once in the
abstract `InventoryDbContext`. `SqliteInventoryDbContext` and `PostgresInventoryDbContext` are empty
subclasses, each with its own `Migrations/<Provider>` folder. The SQLite context also converts
`DateTimeOffset` to an `INTEGER`, because SQLite has no native type for it. That converter keeps 0.1 ms
precision and packs local ticks with the offset, so it only sorts correctly when every offset is zero.
PostgreSQL keeps 1 µs. The domain therefore normalises every stored instant to UTC whole milliseconds
(`Timestamps.Normalize`): a 201 response and a later GET return the same value on both providers, and
the SQLite column sorts correctly by construction rather than by luck. CI runs
`dotnet ef migrations has-pending-model-changes` for both contexts, so a *schema* change without
migrations fails the build. It does not catch a removed `IsConcurrencyToken()`: that changes the
generated UPDATE SQL, not the schema. A negative-control test leaves this check green with the token deleted. So
`ModelTests` asserts the `Version` tokens directly, for both providers. The second migration,
`AddProductVersion`, shows the normal evolution path: an additive column with a default.

Alternative: separate migration assemblies per provider (Microsoft's documented approach for large
apps). The subclass approach is lighter for a two-provider project.

## HTTP surface

- **Minimal APIs** grouped per resource (`MapGroup`) with `TypedResults`, so OpenAPI metadata is
  inferred from return types.
- **Validation**: FluentValidation validators in Application. A generic `ValidationFilter<T>` turns
  failures into `400 ValidationProblemDetails` with per-field errors. The domain re-checks the same
  rules, so a skipped validator cannot corrupt data.
- **Errors**: one `IExceptionHandler` maps `InsufficientStockException`, `DomainException`,
  `NotFoundException` and `ConflictException` to RFC 9457 ProblemDetails, each with a stable `code`
  (`insufficient_stock`, `invalid_order_state`...). Every `DomainException` declares a
  `DomainErrorKind`: `InvalidInput` becomes 400 and `RuleViolation` 409. The kind is a required
  constructor argument, so a new rule cannot silently get the wrong status. Keeping a list of
  "input" codes in the API layer would require updating both layers. Unknown exceptions become a
  generic 500 and are logged without leaking internals. An `OperationCanceledException` caused by the client disconnecting is
  logged at Debug and answered with 499 and no body. Some errors never throw. Outside Development, Minimal APIs answer
  unreadable JSON with a bare 400 (`RouteHandlerOptions.ThrowOnBadRequest` is only on in Development).
  The auth challenge sets 401, and unknown routes get 404. `UseStatusCodePages` turns these into
  ProblemDetails, and `ProblemCodes.Apply` (the `CustomizeProblemDetails` hook) adds a `code` from the
  status (`bad_request`, `unauthorized`, `not_found`...) plus the `traceId` to every body. The
  `traceId` is `Activity.Current.Id`, a W3C `traceparent` whose trace-id part is what the JSON log
  scopes (`TraceId`) and Application Insights (operation id) record, so a client can quote it to find
  the request. `HttpContext.TraceIdentifier` is Kestrel's connection-scoped request id, which the
  telemetry systems do not index. Validation problems carry `code: validation_failed`, and the
  filter adds that code and the `traceId` itself, because `TypedResults.ValidationProblem` does not go through
  `IProblemDetailsService` in .NET 8. The handler's `BadHttpRequestException` branch covers the
  Development path, where binding throws, so both environments return the same body.
- **Null elements**: FluentValidation's `ChildRules` skips null collection elements, so
  `"lines": [null]` once passed validation and crashed with a 500. The validator now has an explicit
  `NotNull()` per line, and `OrderService` re-checks before touching the lines.
- **Paging**: every list endpoint takes `offset`/`limit` (default 50, stable order with a unique
  tie-breaker), so no query returns an unbounded result set. Out-of-range values are clamped, not
  rejected: a negative offset becomes 0 and `limit` is forced into 1-100, so `limit=0` returns one item.
  The OpenAPI summaries document this. Rejecting them with a 400 would be stricter; clamping
  accepts the request while keeping the query bounded.
- **Auth**: a custom `AuthenticationHandler` for `X-Api-Key`. No header means anonymous (reads work).
  A wrong key fails, and write endpoints require the `InventoryWrite` policy. Keys are compared as
  SHA-256 digests with `CryptographicOperations.FixedTimeEquals`, so timing leaks neither the content
  nor the length. Several keys can be configured, which allows rotation.
  JWT bearer (Entra ID) would provide per-user identity. API keys support service clients without
  requiring an identity provider. An `IOperationFilter` marks only operations with the `InventoryWrite` policy as secured in OpenAPI.
- **Observability**: console logs are JSON (`Logging:Console:FormatterName=json`) except in
  Development, with HTTP logging (method, path, status, duration) and `LoggerMessage` source-generated
  log methods. If `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, OpenTelemetry exports to Application
  Insights via `UseAzureMonitor()`. It is off otherwise, including in tests.

## Packaging and infrastructure

- **Dockerfile**: restore (`--locked-mode`, against the committed `packages.lock.json` files) is cached
  on `*.csproj` + lock files + `Directory.*.props`, then publish into the
  `aspnet:8.0` runtime image. It runs as the built-in non-root `app` user (uid 1654) and SQLite
  writes to a dedicated `/data` directory. The base images are build arguments, so they can be pinned
  by digest or mirrored.
- **Bicep** (`infra/main.bicep`): App Service plan (Linux B1) and Web App (`DOTNETCORE|8.0`, HTTPS
  only, FTPS disabled, TLS 1.2, health check `/health/ready`). Key Vault (RBAC) holds the DB
  connection string and API key, and the app settings use `@Microsoft.KeyVault(SecretUri=...)`
  references resolved through a **user-assigned** identity (`keyVaultReferenceIdentity`). The identity
  and its *Key Vault Secrets User* role assignment are created *before* the site (`dependsOn`). With
  a system-assigned identity the grant could only follow the site, and the first boot could see the
  literal reference strings and fail its startup migration. RBAC propagation can still lag by a few
  minutes. App Service retries unresolved references, and a restart forces it.
- The template also creates PostgreSQL Flexible Server 16 (Burstable B1ms) with an `inventory`
  database, and a Log Analytics workspace plus workspace-based Application Insights. Secrets are
  `@secure()` parameters read from environment variables by `main.bicepparam`.
- **Database resilience and access.** The PostgreSQL provider uses Npgsql's `EnableRetryOnFailure`, so
  a transient failover or dropped connection is retried instead of surfacing as a 500. This is safe
  because the app never opens its own transactions: each `SaveChanges` is one implicit transaction.
  `ConcurrencyRetry` still handles optimistic-concurrency conflicts, which are not transient. The
  connection string uses the server admin login, which is more privilege than the app needs; a
  dedicated role with only DML rights on the application tables (migrations run separately) is listed below.

## Planned work

1. JWT bearer auth (Entra ID) with separate read and write scopes. Per-client rate limiting with
   `Microsoft.AspNetCore.RateLimiting`.
2. A transactional outbox that publishes `LowStockRaised` / `OrderFulfilled` events (Azure Service
   Bus).
3. Migration bundle (`dotnet ef migrations bundle`) run by the release pipeline, then
   `MigrateOnStartup=false` everywhere.
4. Testcontainers so the PostgreSQL integration run is self-contained locally, as it already is in CI.
5. Partial fulfilment and backorders, and transfers between locations.
6. Private networking for PostgreSQL (VNet integration + private DNS), and a deployment workflow
   with OIDC federated credentials and a `what-if` gate.
7. A least-privileged PostgreSQL role for the app (DML only), with migrations applied by the release
   pipeline under the admin login. Better still, Entra ID authentication for PostgreSQL via the
   managed identity, so no database password exists at all.
