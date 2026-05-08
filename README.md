# Cirreum Invocation SignalR

[![NuGet Version](https://img.shields.io/nuget/v/Cirreum.Invocation.SignalR.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Invocation.SignalR/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Cirreum.Invocation.SignalR.svg?style=flat-square&labelColor=1F1F1F&color=003D8F)](https://www.nuget.org/packages/Cirreum.Invocation.SignalR/)
[![GitHub Release](https://img.shields.io/github/v/release/cirreum/Cirreum.Invocation.SignalR?style=flat-square&labelColor=1F1F1F&color=FF3B2E)](https://github.com/cirreum/Cirreum.Invocation.SignalR/releases)
[![License](https://img.shields.io/github/license/cirreum/Cirreum.Invocation.SignalR?style=flat-square&labelColor=1F1F1F&color=F2F2F2)](https://github.com/cirreum/Cirreum.Invocation.SignalR/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-003D8F?style=flat-square&labelColor=1F1F1F)](https://dotnet.microsoft.com/)

**SignalR invocation source for the Cirreum Invocation provider family.**

## Overview

`Cirreum.Invocation.SignalR` is the L3 Infrastructure package that makes SignalR a registered invocation source within the Cirreum framework. It supplies a concrete `InvocationProviderRegistrar` that:

- Maps SignalR Hubs from configuration (`Cirreum:Invocation:Providers:SignalR:Instances:{key}`).
- Registers a `HubFilter` that publishes `IInvocationContext` per Hub method invocation, putting SignalR on the same unified seam as HTTP.
- Wires up the per-instance auth scheme via `RequireAuthorization()` when one is configured.

Apps do **not** reference this package directly — they install the matching L5 Runtime Extensions package (`Cirreum.Runtime.Invocation.SignalR`) which exposes the `AddSignalR<THub>(instanceKey)` extension on `IInvocationBuilder` and the `MapSignalRInvocation()` endpoint-mapping method, and pulls this package in transitively.

## Architectural position

```
L2 Core
  Cirreum.InvocationProvider               ← abstractions: IInvocationContext, registrar base, ...

L3 Infrastructure
  Cirreum.Invocation.SignalR               ← THIS PACKAGE — registrar, settings, HubFilter
  Cirreum.Invocation.WebSockets            ← peer for raw WebSockets

L4 Runtime
  Cirreum.Runtime.InvocationProvider       ← IInvocationBuilder seam, AddInvocation, RegisterInvocationProvider

L5 Runtime Extensions
  Cirreum.Runtime.Invocation.SignalR       ← AddSignalR<THub>(key) + MapSignalRInvocation()
```

## What's in the box

| Type | Role |
|---|---|
| `SignalRInvocationRegistrar` | Concrete registrar — maps Hubs, wires the HubFilter, registers the `IConnectionSender`, validates settings |
| `SignalRInvocationSettings` / `SignalRInvocationInstanceSettings` (`Cirreum.Invocation.Configuration`) | Typed settings bound from `Cirreum:Invocation:Providers:SignalR` |
| `SignalRHubMapping` (`Cirreum.Invocation.SignalR`) | DI-stashed `(InstanceKey, HubType)` record produced by the L5 `AddSignalR<THub>` extension and consumed by the registrar |
| `InvocationContextHubFilter` (`Cirreum.Invocation.SignalR`, internal) | `IHubFilter` covering all three lifecycle hooks — publishes `IInvocationContext`, materializes `IInvocationConnection` at upgrade, dispatches `IConnectionLifecycle` callbacks |
| `SignalRConnection` (`Cirreum.Invocation.SignalR`, internal) | `IInvocationConnection` adapter wrapping `HubCallerContext`; aliases `Items` and captures the SignalR caller proxy for server-push |
| `SignalRInvocationContext` (`Cirreum.Invocation.SignalR`, internal) | `IInvocationContext` adapter for SignalR — used both for Hub method invocations and for synthetic scopes around lifecycle hooks |
| `SignalRConnectionSender` (`Cirreum.Invocation.SignalR`, internal) | `IConnectionSender` impl — server-initiated push through the active connection's captured caller proxy |

## How registration works

The L5 `AddSignalR<THub>(instanceKey)` extension does two things:

1. Stashes a `SignalRHubMapping(instanceKey, typeof(THub))` record in DI.
2. Calls `builder.HostBuilder.RegisterInvocationProvider<SignalRInvocationRegistrar, SignalRInvocationSettings, SignalRInvocationInstanceSettings>()` from L4. The L4 helper:
   - Binds `Cirreum:Invocation:Providers:SignalR` from `IConfiguration` to `SignalRInvocationSettings`.
   - Calls `registrar.Register(...)` — services phase — which calls `services.AddSignalR()` and registers the `InvocationContextHubFilter` against the global `HubOptions`.
   - Stashes an `InvocationProviderMapping` in DI capturing the deferred `registrar.Map(...)` closure.

The L5 `MapSignalRInvocation()` endpoint-mapping method resolves all `InvocationProviderMapping` entries with `ProviderName == "SignalR"` and invokes their closures against `IEndpointRouteBuilder`. The registrar's `MapSource` then:

1. Resolves the `SignalRHubMapping` for each enabled instance.
2. Dispatches through reflection to `endpoints.MapHub<THub>(path)` (the framework only ships a generic-only overload).
3. Wires `RequireAuthorization` with `AuthenticationSchemes = settings.Scheme` if `Scheme` is set.

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

`Scheme` references a configured Authorization instance under `Cirreum:Authorization:Providers:*:Instances:{Scheme}`. Optional — leave unset for unauthenticated hubs (rare).

## Server-initiated push

Inject `IConnectionSender` from a SignalR Hub method (or any code running inside the SignalR invocation pipeline) to send to the calling client without depending on `IHubContext<THub>` directly:

```csharp
public sealed class ChatHub(IConnectionSender sender) : Hub {
    public async Task Echo(string text) {
        await sender.SendAsync("Echo", new { text, at = DateTime.UtcNow });
    }
}
```

The no-method `SendAsync<T>(payload)` overload uses the runtime type name as the SignalR method-routing convention (e.g. `SendAsync(new ChatMessage(...))` dispatches to client `connection.on("ChatMessage", ...)`); the keyed `SendAsync<T>(method, payload)` overload accepts an explicit method name.

## Connection lifecycle

Implement `IConnectionLifecycle` (from `Cirreum.Invocation.Connections`) and register it in DI to receive `OnConnectedAsync` / `OnDisconnectedAsync` callbacks. The HubFilter dispatches both under a synthetic invocation scope so consumers like `IUserStateAccessor` work normally inside the callbacks:

```csharp
internal sealed class AuditConnectionLifecycle : IConnectionLifecycle {

    public ValueTask<bool> OnConnectedAsync(IInvocationConnection connection, CancellationToken ct) {
        // Inspect connection.User, connection.ConnectionId, connection.Items, etc.
        // Return false to reject the connection (the upgrade aborts; client sees normal rejection).
        return ValueTask.FromResult(true);
    }

    public ValueTask OnDisconnectedAsync(IInvocationConnection connection, CancellationToken ct) {
        return ValueTask.CompletedTask;
    }

}
```

## Dependencies

- **Cirreum.InvocationProvider** — L2 abstractions (`InvocationProviderRegistrar`, `IInvocationContext`, `IInvocationContextAccessor`)
- **Microsoft.AspNetCore.App** (framework reference) — SignalR (`Microsoft.AspNetCore.SignalR`), endpoint routing

## Documentation

- [ADR-0002 — Unified `IInvocationContext` seam](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/adr/0002-unified-invocation-context.md)
- [Type-level design](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/01-DESIGN.md)
- [Provider-pattern integration](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/02-PROVIDER-PATTERN.md)
- [Migration & sequencing](https://github.com/cirreum/Cirreum.DevOps/blob/main/docs/InvocationContext/03-MIGRATION.md)

## Versioning

Follows [Semantic Versioning](https://semver.org/). Foundational library — major bumps are rare and coordinated with `Cirreum.InvocationProvider` releases.

## License

MIT — see [LICENSE](LICENSE).

---

**Cirreum Foundation Framework**  
*Layered simplicity for modern .NET*
