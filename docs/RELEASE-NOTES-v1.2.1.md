# Cirreum.Invocation.SignalR 1.2.1 — auth slots flow through to Hub method invocations

Closes a real defect that turned every Hub method invocation into a fresh `IApplicationUserResolver` call for audience-auth long-lived connections — IdP hammered, every method, every connection. The auth slots that ASP.NET's auth pipeline + the Cirreum forward selector + the audience-auth claims-transformer wrote onto `HttpContext.Items` during the upgrade request were getting stranded there, never reaching `IInvocationContext.Items` where consumers like `UserStateAccessor` look.

---

## Why this release exists

Trace the lifecycle of a SignalR connection:

```
1. Client makes HTTP upgrade request to /chat
2. ASP.NET pipeline runs:
   - UseAuthentication() → forward selector stamps HttpContext.Items[AuthenticatedScheme]
   - IClaimsTransformation (audience-auth only) → ApplicationUserRoleResolverAdapter writes
     HttpContext.Items[ApplicationUserCache] = appUser
   - UseAuthorization() → policy check
   - InvocationContextHttpMiddleware → wraps HttpContext as HttpInvocationContext
3. SignalR upgrade completes, Hub.OnConnectedAsync runs through the pipeline
4. ► InvocationContextHubFilter.OnConnectedAsync materializes SignalRConnection
5. Per-Hub-method invocations: InvocationContextHubFilter.InvokeMethodAsync constructs
   a fresh SignalRInvocationContext (per-invocation Items = new Dictionary<>())
6. Hub method runs Conductor command → UserStateAccessor.GetUser()
   → reads invocation.Items[ApplicationUserCache] → MISS (per-invocation Items is fresh)
   → calls IApplicationUserResolver again → IdP hit
```

For HTTP, this works transparently because `HttpInvocationContext.Items` aliases `HttpContext.Items` directly — same dictionary, request lifetime = invocation lifetime. For SignalR, the per-invocation `Items` was correctly fresh per [ADR-0002 invariant #6](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md), but the auth slots that should have been seeded from connection-lifetime state never made it onto `Connection.Items` — so even per-invocation seeding wouldn't have helped.

This release fixes it in two parts.

---

## What's fixed

### 1. Upgrade-time copy onto `Connection.Items`

`InvocationContextHubFilter.OnConnectedAsync` now reads `hubLifetimeContext.Context.GetHttpContext().Items` (the upgrade request's bag, still alive at this point per the SignalR pipeline) and copies the two well-known auth slots onto the freshly-materialized `SignalRConnection.Items`:

```csharp
var httpContext = hubLifetimeContext.Context.GetHttpContext();
if (httpContext is not null) {
    if (httpContext.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
        connection.Items[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
    }
    if (httpContext.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
        connection.Items[AuthenticationContextKeys.ApplicationUserCache] = appUser;
    }
}
```

Honors [ADR-0002 transport-adapter invariant #2](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/01-DESIGN.md#2-upgrade-time-items-slot-copy) which calls for exactly this copy at long-lived adapter upgrade time. The framework reading `GetHttpContext()` from the HubFilter at upgrade is explicitly endorsed (see the design doc's anti-pattern note for why consumer code should NOT do this from later Hub method bodies).

### 2. Per-invocation seed from `Connection.Items`

`SignalRInvocationContext` constructor now seeds the fresh per-invocation `Items` bag with the same two slots from `Connection.Items` via a private `SeedAuthSlots` helper:

```csharp
private static Dictionary<object, object?> SeedAuthSlots(SignalRConnection connection) {
    var dict = new Dictionary<object, object?>();
    if (connection.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
        dict[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
    }
    if (connection.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
        dict[AuthenticationContextKeys.ApplicationUserCache] = appUser;
    }
    return dict;
}
```

Snapshot copy — per-invocation writes do NOT propagate back to `Connection.Items`, preserving per-message isolation per ADR-0002 invariant #6. Per-invocation `Items` is still genuinely a fresh dictionary; it just starts with the connection-lifetime auth slots already in place so consumers reading `invocation.Items` hit naturally.

### 3. Local `AuthenticationContextKeys` consts

A new internal `Cirreum.Invocation.SignalR.AuthenticationContextKeys` static class duplicates the two const values that live canonically in `Cirreum.Security.AuthenticationContextKeys` (Cirreum.Core L2). Cirreum.Core is intentionally NOT added as a PackageReference — preserves the L2-peers-don't-cross-reference rule, mirrors the same workaround pattern used by `AudienceProviderRoleClaimsTransformer` in `Cirreum.Runtime.AuthorizationProvider`. Const values must match Cirreum.Core's exactly; comments on both sides note the duplication.

---

## What this means per auth shape

| Auth shape on long-lived connection | Before this release | After this release |
|---|---|---|
| **Audience (MSAL/OIDC)** with matching `IApplicationUserResolver` | Cache miss every Hub method → resolver re-runs → IdP hammered | Cache hit on every Hub method (seeded from upgrade); resolver runs once at upgrade |
| **Header-auth (API key / signed request)** — no resolver matches the scheme | UserStateAccessor early-returns null every method (no resolver, no work) | Same — no behavior change. The seeded scheme slot lets defense-in-depth checks read it; no resolver fires, no DB hit. |
| **Anonymous** | Same no-op | Same no-op |
| **AI/LLM act-on-behalf-of (future Piece 2)** with a null-scheme resolver | Cache write would land on per-invocation only; subsequent invocations re-resolve | Foundation in place; the parallel patch to `Cirreum.Services.Server`'s `UserStateAccessor` adds the connection-bag double-write so this scenario also caches correctly when it ships |

---

## Coordinated work

Ships in lockstep with:

- **`Cirreum.Invocation.WebSockets 1.2.1`** — same fix for the WebSocket adapter (`WebSocketOrchestrator` upgrade-time copy + `WebSocketInvocationContext` per-invocation seed).
- **`Cirreum.Services.Server` patch** — `UserStateAccessor` double-writes the resolved `IApplicationUser` to both per-invocation `Items` AND `invocation.Connection?.Items` on lazy resolve. Future-proofs for the AI/LLM Piece 2 seam (null-scheme resolvers); dead-code today for current resolver registrations.

---

## Compatibility

- **Source- and binary-compatible** for all consumers — no public API change.
- **Behavior-compatible** for HTTP-sourced invocations (unaffected by the original defect).
- **Behavior-changing for SignalR-sourced invocations**: per-Hub-method `IUserStateAccessor.GetUser()` now reliably hits the cache on audience-auth long-lived connections instead of re-invoking the resolver. The change is strictly a performance fix; same resolved user, fewer IdP hits.
- No package reference changes.

---

## See also

- `CHANGELOG.md` — condensed change list.
- [`Cirreum.Invocation.WebSockets 1.2.1`](https://www.nuget.org/packages/Cirreum.Invocation.WebSockets) — parallel adapter fix.
- [`Cirreum.Services.Server` patch](https://www.nuget.org/packages/Cirreum.Services.Server) — `UserStateAccessor` double-write.
- [ADR-0002](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md) — the foundational seam decision; transport-adapter invariants #2 and #6 are directly relevant here.
