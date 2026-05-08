# Cirreum.Invocation.SignalR 1.0.0 — SignalR joins the Invocation family

Initial release. This is the L3 Infrastructure package that makes SignalR a fully-supported invocation source within the Cirreum framework — the second source after HTTP. Once paired with the matching L5 package (`Cirreum.Runtime.Invocation.SignalR`), apps can host typed SignalR Hubs that flow through the same `IInvocationContext` seam as HTTP requests, with identity, items, services, cancellation, **and** the long-lived-source abstractions (`IInvocationConnection`, `IConnectionLifecycle`, `IConnectionSender`) all unified.

Anchored by [ADR-0002](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md). Release #11 in the [Invocation family rollout](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/03-MIGRATION.md).

---

## Why this release exists

The HTTP foundation (`Cirreum.Services.Server 1.2.0` + `Cirreum.Runtime.Server 1.1.0`) lit up the unified `IInvocationContext` seam end-to-end for HTTP. This release does the same for SignalR — same seam, same accessor, same downstream consumers. A single `UserStateAccessor` instance, a single conductor pipeline, a single authorizer chain serves both transports without a single `if (HttpContext != null)` branch anywhere. Plus full support for the long-lived-source abstractions that don't apply to HTTP: per-connection state, lifecycle callbacks, and server-initiated push.

---

## What's new

### `SignalRInvocationRegistrar`

The concrete L3 registrar that bootstraps SignalR as an invocation source.

**Services phase** (`RegisterSource`):

- `services.AddSignalR()` — idempotent, safe to call once per instance.
- Registers `InvocationContextHubFilter` and adds it to the global `HubOptions` filter pipeline so it applies to every Hub registered in the host.
- Registers `SignalRConnectionSender` as the scoped `IConnectionSender` impl.

**Validation** (`ValidateSettings`):

- Enforces `Path` starts with `/`. Base validates non-empty.

**Endpoints phase** (`MapSource`):

- Resolves the L5-stashed `SignalRHubMapping` for the instance to recover the concrete `THub` type.
- Dispatches through reflection to `endpoints.MapHub<THub>(settings.Path)` (the framework only ships a generic-only overload). Acceptable because endpoint mapping happens once at startup.
- Applies `RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = settings.Scheme })` when `Scheme` is configured. `Scheme` references a separately-configured Authorization instance under `Cirreum:Authorization:Providers:*:Instances:{Scheme}`.

### `SignalRHubMapping`

```csharp
public sealed record SignalRHubMapping(string InstanceKey, Type HubType);
```

Public DI-stashed record. Produced by the L5 `AddSignalR<THub>(instanceKey)` extension; consumed by the registrar's `MapSource`. Lives at L3 because both producer and consumer need to reference it, and L5 references L3.

### `InvocationContextHubFilter`

Internal `IHubFilter` covering all three SignalR lifecycle hooks:

- **`OnConnectedAsync`** — captures the SignalR caller proxy from the Hub's `Clients.Caller`, materializes a `SignalRConnection`, stashes it in `HubCallerContext.Items` for later retrieval, and runs registered `IConnectionLifecycle.OnConnectedAsync` hooks under a synthetic invocation scope so `IUserStateAccessor` and other ambient consumers work normally inside the callbacks. If any lifecycle hook returns `false`, the connection is aborted.
- **`InvokeMethodAsync`** — retrieves the cached connection, builds a per-invocation `SignalRInvocationContext`, and publishes it through `IInvocationContextAccessor` for the duration of the Hub method call. Per-message DI scope contract holds for SignalR the same way it does for HTTP.
- **`OnDisconnectedAsync`** — runs registered `IConnectionLifecycle.OnDisconnectedAsync` hooks under a synthetic scope, absorbing per-hook exceptions per the L2 contract.

### `SignalRConnection`

Internal `IInvocationConnection` adapter wrapping `HubCallerContext`:

| `IInvocationConnection` | sourced from |
|---|---|
| `ConnectionId` | `HubCallerContext.ConnectionId` |
| `User` | `HubCallerContext.User` (snapshotted at upgrade — immutable per the L2 contract) |
| `ConnectedAtUtc` | captured at construction |
| `Items` | **alias** of `HubCallerContext.Items` (same dictionary reference) |
| `Aborted` | `HubCallerContext.ConnectionAborted` |
| `InvocationSource` | `InvocationSources.SignalR` |

Items aliasing means any state set via SignalR-aware code (the HubFilter, the Hub itself) flows through to consumers reading `Connection.Items` without copying. Same load-bearing decision as `HttpInvocationContext` on the HTTP side.

The class also captures the SignalR `ISingleClientProxy` (from `Hub.Clients.Caller`) at upgrade time and exposes it internally to `SignalRConnectionSender`. This is what lets the L3 layer drive server-push without knowing `THub` at compile time.

### `SignalRInvocationContext`

Internal `IInvocationContext` adapter for SignalR. Carries per-method `User`, `Services`, `Aborted`, a fresh per-invocation `Items` dictionary, and the parent `IInvocationConnection`. Used both for in-flight Hub method invocations and for synthetic invocation scopes around `IConnectionLifecycle` callbacks.

### `SignalRConnectionSender`

Internal `IConnectionSender` impl, scoped per-invocation. Resolves the active connection from the ambient `IInvocationContextAccessor` and sends through the captured caller proxy:

```csharp
public sealed class ChatHub(IConnectionSender sender) : Hub {
    public async Task Echo(string text) {
        await sender.SendAsync("Echo", new { text, at = DateTime.UtcNow });
    }
}
```

The keyed `SendAsync<T>(method, payload)` overload sends to a specific client receive handler. The no-method `SendAsync<T>(payload)` overload uses the runtime type name as the SignalR method-routing convention — `SendAsync(new ChatMessage(...))` dispatches to `connection.on("ChatMessage", ...)` on the client. Throws `InvalidOperationException` if called outside an active SignalR-sourced invocation.

### Settings types

```csharp
public sealed class SignalRInvocationSettings
    : InvocationProviderSettings<SignalRInvocationInstanceSettings>;

public sealed class SignalRInvocationInstanceSettings
    : InvocationProviderInstanceSettings;
```

V1 carries no SignalR-specific fields beyond the base. The base `Section` property exposes the raw configuration section, so apps can override any standard `HubOptions` field through configuration without Cirreum re-defining them — same approach as the WebSocket and gRPC sources will use.

---

## Configuration

```json
{
  "Cirreum": {
    "Invocation": {
      "Providers": {
        "SignalR": {
          "Instances": {
            "chat":          { "Enabled": true, "Path": "/chat",          "Scheme": "oidc_primary" },
            "notifications": { "Enabled": true, "Path": "/notifications", "Scheme": "oidc_primary" }
          }
        }
      }
    }
  }
}
```

Each instance maps one Hub at one path. Multiple instances allow hosts to expose multiple Hubs through the same Cirreum framework wiring with potentially different auth schemes per Hub (e.g., a tenant-facing Hub gated by `oidc_tenant` and an operator-facing Hub gated by `oidc_operator`).

---

## Architecture position

```
L2 Core
  Cirreum.InvocationProvider               ← abstractions (1.0.1)

L3 Infrastructure
  Cirreum.Invocation.SignalR               ← THIS PACKAGE — registrar, settings, HubFilter, Connection, ConnectionSender
  Cirreum.Invocation.WebSockets            ← peer for raw WebSockets (release #12)

L4 Runtime
  Cirreum.Runtime.InvocationProvider       ← IInvocationBuilder seam (1.0.0)

L5 Runtime Extensions
  Cirreum.Runtime.Invocation.SignalR       ← AddSignalR<THub>(key) + MapSignalRInvocation() (release #13)
```

Apps reference only the L5 Runtime Extensions package (`Cirreum.Runtime.Invocation.SignalR`). This L3 package flows in transitively.

---

## Dependencies

- **`Cirreum.InvocationProvider`** `1.0.1` — L2 abstractions
- **`Microsoft.AspNetCore.App`** (framework reference) — SignalR + endpoint routing

---

## What this enables

This release unblocks **#13 — `Cirreum.Runtime.Invocation.SignalR`** (L5), which exposes:

- `AddSignalR<THub>(instanceKey)` extension on `IInvocationBuilder` — stashes the `SignalRHubMapping` and triggers `RegisterInvocationProvider` for the L3 registrar.
- `MapSignalRInvocation()` extension on `IEndpointRouteBuilder` — resolves SignalR-tagged `InvocationProviderMapping` entries and invokes them.

After #13 ships, a Cirreum app can compose SignalR alongside HTTP with full seam parity:

```csharp
var builder = DomainApplication.CreateBuilder(args);

builder.AddInvocation(b => b
    .AddSignalR<ChatHub>("chat")
    .AddSignalR<NotificationHub>("notifications"));

var app = builder.Build<MyDomainMarker>();
app.UseDefaultMiddleware();          // includes UseInvocationContext for HTTP
app.MapSignalRInvocation();          // maps the SignalR Hubs
await app.RunAsync();
```

---

## Compatibility

- **Initial release.** No prior version, no migration story.
- Stable public surface — `SignalRInvocationRegistrar`, `SignalRHubMapping`, and the settings types are intended to evolve only through additive minor bumps.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.0.0`.
- [`Cirreum.InvocationProvider 1.0.1`](https://www.nuget.org/packages/Cirreum.InvocationProvider) — L2 abstractions this package implements.
- [`Cirreum.Runtime.InvocationProvider 1.0.0`](https://www.nuget.org/packages/Cirreum.Runtime.InvocationProvider) — L4 helper the L5 SignalR package will use.
- [ADR-0002](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md) — the foundational design decision.
- [Provider-pattern integration](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/02-PROVIDER-PATTERN.md) — how registrars compose with the Provider track.
- [Migration & sequencing](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/03-MIGRATION.md) — full rollout plan.
