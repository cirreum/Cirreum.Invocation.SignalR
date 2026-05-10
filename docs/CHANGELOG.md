# Changelog

All notable changes to **Cirreum.Invocation.SignalR** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Auth slots now propagate from the upgrade-time `HttpContext` onto `SignalRConnection.Items` and per-Hub-method `IInvocationContext.Items`** — closes a real defect where `IUserStateAccessor` (and any other consumer of `AuthenticationContextKeys.AuthenticatedScheme` / `ApplicationUserCache`) saw an empty per-invocation `Items` bag on every Hub method dispatch, causing the `IApplicationUserResolver` to be re-invoked per invocation (IdP hammering for audience-auth long-lived connections). Two coordinated changes to land the fix:
  - `InvocationContextHubFilter.OnConnectedAsync` now reads `hubLifetimeContext.Context.GetHttpContext().Items` (the upgrade request's bag, populated by ASP.NET's auth pipeline + the Cirreum forward selector + any audience-auth claims-transformer that ran) and copies the two well-known auth slots onto the freshly-materialized `SignalRConnection.Items` (which aliases `HubCallerContext.Items`, persisting for the connection's lifetime). Honors ADR-0002 transport-adapter invariant #2.
  - `SignalRInvocationContext` constructor now seeds the fresh per-invocation `Items` bag with the same two slots from `Connection.Items`. Snapshot copy — per-invocation writes do NOT propagate back to `Connection.Items`, preserving per-message isolation per ADR-0002 invariant #6.
- **Local `internal static class AuthenticationContextKeys`** added under `Cirreum.Invocation.SignalR` to hold the two const string keys (`AuthenticatedScheme`, `ApplicationUserCache`) that mirror their canonical definitions in `Cirreum.Security.AuthenticationContextKeys` (Cirreum.Core L2). Consts are duplicated rather than taking a Cirreum.Core PackageReference to preserve the L2-peers-don't-cross-reference rule. Same pattern already used by `AudienceProviderRoleClaimsTransformer` in `Cirreum.Runtime.AuthorizationProvider`.

The fix is scoped to long-lived adapters (this package + `Cirreum.Invocation.WebSockets`) and `Cirreum.Services.Server`'s `UserStateAccessor` (separate patch). HTTP-sourced invocations were unaffected by the original defect because `HttpInvocationContext.Items` aliases `HttpContext.Items` directly, so the cache lifetime trivially aligned with the request lifetime.

## [1.2.0] - 2026-05-09

### Added

- **`SignalRConnection.SendAsync<T>` overloads (2)** — implementations of the new `IInvocationConnection.SendAsync<T>` members from the upcoming `Cirreum.InvocationProvider` release. Both overloads forward to the captured `ISingleClientProxy.SendAsync(method, payload, ct)` so the SignalR pipeline owns serialization through the configured `IHubProtocol` (JSON or MessagePack — controlled by app via `AddSignalR().AddJsonProtocol(...)` / `.AddMessagePackProtocol()`). The no-method overload uses the runtime payload type name as the SignalR method (matching the convention `connection.on("ChatMessage", ...)`); the keyed overload accepts an explicit method name.

### Changed

- **`SignalRConnectionSender` consolidated into `SignalRConnection.SendAsync`** — the standalone scoped service and its DI registration in `SignalRInvocationRegistrar.RegisterSource` are gone; the same forwarding logic now lives on the `SignalRConnection` itself, satisfying the new `IInvocationConnection.SendAsync<T>` contract from the upcoming `Cirreum.InvocationProvider` release. Cross-cutting code that previously injected `IConnectionSender` now reads the connection from the ambient `IInvocationContextAccessor` and calls `SendAsync` directly — same target, one indirection fewer. Apps see no behavior difference; the wire bytes are identical. Captured as `### Changed` (not `### Removed`) under the same window-of-no-consumers, framework-owned-implementer-set precedent as the L2 1.1.0 / 1.2.0 cascades — this is a v1.x pre-adoption surface; the consolidation is a Minor.

- Bumped `Cirreum.InvocationProvider` dependency to consume the `IInvocationConnection.SendAsync` interface widening and the corresponding consolidation of `IConnectionSender`.

### Migration

App-side: see the migration block in the upcoming `Cirreum.InvocationProvider` release notes — replace `IConnectionSender` injections with `accessor.Current?.Connection?.SendAsync(...)`. SignalR Hub method bodies that already used `Clients.Caller.SendAsync(...)` directly are unaffected; the change targets cross-cutting code paths (Conductor command/query handlers, validators) that pushed via the framework abstraction.

## [1.1.0] - 2026-05-09

### Added

- **`SignalRConnection.Abort()`** implementation of the new `IInvocationConnection.Abort()` member from `Cirreum.InvocationProvider` 1.2.0. Wraps SignalR's native `HubCallerContext.Abort()` — cancels `ConnectionAborted`, drains the connection, and triggers the Hub's `OnDisconnectedAsync`. Idempotent.

### Changed

- Bumped `Cirreum.InvocationProvider` dependency to 1.2.0 to consume the new `IInvocationConnection.Abort()` contract.

## [1.0.2] - 2026-05-08

### Fixed

- **`SignalRInvocationRegistrar.MapSource` now binds `HttpConnectionDispatcherOptions` from the instance's `HttpOptions` sub-section.** SignalR's per-mapping `MapHub<THub>(path, options => ...)` overload exposes transport-level config (`Transports`, `ApplicationMaxBufferSize`, `TransportMaxBufferSize`, `LongPolling.PollTimeout`, `WebSockets.CloseTimeout`, `MinimumProtocolVersion`, `CloseOnAuthenticationExpiration`) that v1.0.0 / v1.0.1 had no way to drive from configuration — the registrar called the 2-param `MapHub<THub>(path)` overload only. v1.0.2 switches the reflection helper to the 3-param overload and passes a configure delegate that binds the instance section's `HttpOptions` sub-section. Cirreum-defined sub-sections under each instance (`HubOptions`, `HttpOptions`) cleanly separate the three SignalR option surfaces (Cirreum framework fields at instance root, per-Hub `HubOptions<THub>`, per-mapping `HttpConnectionDispatcherOptions`) — paired with the L5 `Cirreum.Runtime.Invocation.SignalR` package's matching binding from the `HubOptions` sub-section. No behavior change for v1.0.1 instances that didn't declare an `HttpOptions` sub-section — the binder runs over zero properties and `HttpConnectionDispatcherOptions` retains its defaults.

## [1.0.1] - 2026-05-08

### Fixed

- **Promoted the SignalR provider name from an internal string literal to a public `const`.** The `SignalRInvocationRegistrar.ProviderName` property's value was effectively a magic string (`"SignalR"`) at the L3 layer with no public anchor for consumers to reference. v1.0.0 left filtering-by-provider-name consumers (most notably the L5 `MapSignalRInvocation` implementation, which resolves `IEnumerable<InvocationProviderMapping>` and filters by `ProviderName == "SignalR"`) to hardcode the same literal — a string-drift hazard. v1.0.1 exposes the value as `public const string ProviderKey = "SignalR"` on the registrar and routes `ProviderName` through it. L5 packages and any other consumer can now reference `SignalRInvocationRegistrar.ProviderKey` instead of the literal, eliminating the drift risk. No behavior change — runtime value is unchanged.

## [1.0.0] - 2026-05-08

### Added

Initial release of the Cirreum SignalR invocation source — the L3 Infrastructure package that makes SignalR a fully-supported invocation source within the Cirreum framework, with full per-method `IInvocationContext` publication, per-connection `IInvocationConnection` materialization, `IConnectionLifecycle` callback dispatch, and `IConnectionSender` server-push. Anchored by ADR-0002 (Unified `IInvocationContext` Seam).

**Registrar:**

- `SignalRInvocationRegistrar` (`Cirreum.Invocation`) — concrete `InvocationProviderRegistrar<SignalRInvocationSettings, SignalRInvocationInstanceSettings>`. `RegisterSource` calls `services.AddSignalR()`, registers the `InvocationContextHubFilter` against the global `HubOptions`, and registers `SignalRConnectionSender` as the scoped `IConnectionSender` impl. `MapSource` resolves the L5-stashed `SignalRHubMapping` for each enabled instance, dispatches through reflection to `endpoints.MapHub<THub>(path)`, and applies `RequireAuthorization` with the configured scheme. `ValidateSettings` enforces that `Path` starts with `/` (the base validates non-empty).

**Configuration types (`Cirreum.Invocation.Configuration`):**

- `SignalRInvocationSettings` — root settings bound from `Cirreum:Invocation:Providers:SignalR`.
- `SignalRInvocationInstanceSettings` — per-instance settings; v1 carries no SignalR-specific fields beyond the base.

**SignalR-specific types (`Cirreum.Invocation.SignalR`):**

- `SignalRHubMapping(string InstanceKey, Type HubType)` — public DI-stashed record produced by the L5 `AddSignalR<THub>(instanceKey)` extension and consumed by `SignalRInvocationRegistrar.MapSource`.
- `InvocationContextHubFilter` (internal) — `IHubFilter` that overrides all three lifecycle hooks. `OnConnectedAsync` materializes a `SignalRConnection`, captures the SignalR caller proxy for server-push, runs registered `IConnectionLifecycle.OnConnectedAsync` hooks under a synthetic invocation scope, and stashes the connection in `HubCallerContext.Items` for later retrieval. `InvokeMethodAsync` retrieves the cached connection and publishes a per-invocation `IInvocationContext` for the duration of the Hub method call. `OnDisconnectedAsync` runs registered `IConnectionLifecycle.OnDisconnectedAsync` hooks under a synthetic scope, absorbing per-hook exceptions per the L2 contract.
- `SignalRInvocationContext` (internal) — `IInvocationContext` adapter for SignalR. Per-invocation `Items` (distinct from the per-connection `Connection.Items`); `User`, `Services`, `Aborted` snapshotted from the SignalR `HubInvocationContext` (or `HubLifetimeContext` for synthetic-scope lifecycle hooks); `Connection` populated.
- `SignalRConnection` (internal) — `IInvocationConnection` adapter wrapping `HubCallerContext`. Aliases `HubCallerContext.Items` (same dictionary reference) for connection-scoped state. Captures `ISingleClientProxy` (the SignalR caller proxy) at upgrade time so the L3 layer can drive server-push without knowing `THub`.
- `SignalRConnectionSender` (internal) — `IConnectionSender` impl resolved per-invocation from DI. Sends through the active connection's captured caller proxy. The no-method `SendAsync<T>(T payload)` overload uses the runtime type name as the SignalR method-routing convention (e.g. `SendAsync(new ChatMessage(...))` dispatches to client `connection.on("ChatMessage", ...)`); the keyed overload accepts an explicit method name.

### Architecture position

This package is the **L3 Infrastructure** counterpart to the L5 Runtime Extensions package `Cirreum.Runtime.Invocation.SignalR` (which exposes `AddSignalR<THub>(instanceKey)` on `IInvocationBuilder` and the `MapSignalRInvocation()` endpoint-mapping method). Apps reference only the L5 package; this L3 package flows in transitively.
