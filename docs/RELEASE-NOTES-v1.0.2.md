# Cirreum.Invocation.SignalR 1.0.2 — Per-mapping `HttpConnectionDispatcherOptions` binding

A naming-hygiene completion patch. Wires up SignalR's third configuration surface — `HttpConnectionDispatcherOptions` (per-mapping, transport-level config) — by switching the registrar's `MapHub<THub>` reflection from the 2-param overload to the 3-param overload and binding from a dedicated `HttpOptions` sub-section under each instance.

Strictly additive at runtime. v1.0.1 instances that didn't declare an `HttpOptions` sub-section see no behavior change — the binder runs over zero properties and `HttpConnectionDispatcherOptions` retains its ASP.NET defaults.

---

## Why this release exists

SignalR exposes three configuration surfaces:

| Surface | Scope | Where ASP.NET configures |
|---|---|---|
| `HubOptions` (global) | All Hubs in host | `AddSignalR(o => ...)` |
| `HubOptions<THub>` (per-Hub) | One specific `THub` | `AddHubOptions<THub>(o => ...)` |
| `HttpConnectionDispatcherOptions` (per-mapping) | One specific `MapHub<THub>(path, ...)` call | `MapHub<THub>(path, options => ...)` |

v1.0.0 / v1.0.1 wired up the first two via the L5 `Cirreum.Runtime.Invocation.SignalR` package's `AddSignalRInvocation` and `AddSignalR<THub>` extensions. The third — `HttpConnectionDispatcherOptions` — was undeliverable from the L5 layer because it's tied to a specific `MapHub<THub>(path)` call, which happens at endpoints-phase time inside the L3 registrar's `MapSource`. The v1.0.0 / v1.0.1 `MapSource` used the 2-param `MapHub<THub>(path)` overload only, leaving consumers with no path to drive transport-level options from configuration.

This release closes that gap.

---

## What changed

### `MapSource` switches to the 3-param `MapHub<THub>` overload

The reflection helper now resolves the `MapHub<THub>(IEndpointRouteBuilder, string, Action<HttpConnectionDispatcherOptions>)` overload (3 parameters) instead of the 2-param `MapHub<THub>(IEndpointRouteBuilder, string)`. The configure delegate binds from the instance section's `HttpOptions` sub-section:

```csharp
var hub = MapHubByType(endpoints, mapping.HubType, settings.Path,
    options => settings.Section?.GetSection("HttpOptions").Bind(options));
```

ASP.NET's 2-param overload internally calls the 3-param version with `configureOptions = null`, so always using the 3-param version is functionally identical for the case where no `HttpOptions` sub-section is present (the binder sees an empty section and binds zero properties).

### `HttpOptions` sub-section now meaningful

Under each instance:

```json
"chat": {
  "Enabled": true,
  "Path": "/chat",
  "Scheme": "oidc_primary",

  "HubOptions": {
    "MaximumReceiveMessageSize": 65536,
    "EnableDetailedErrors": true
  },

  "HttpOptions": {
    "Transports": "WebSockets, LongPolling",
    "ApplicationMaxBufferSize": 131072,
    "TransportMaxBufferSize": 131072,
    "LongPolling": { "PollTimeout": "00:01:30" },
    "WebSockets": { "CloseTimeout": "00:00:10" }
  }
}
```

| Sub-section / field | Binds to | Bound by |
|---|---|---|
| `Enabled`, `Path`, `Scheme` | Cirreum framework | This package's registrar (consumed directly) |
| `HubOptions` (per-instance) | `HubOptions<THub>` | L5 `Cirreum.Runtime.Invocation.SignalR.AddSignalR<THub>` |
| `HttpOptions` (per-instance) | `HttpConnectionDispatcherOptions` | This package's `SignalRInvocationRegistrar.MapSource` (new in v1.0.2) |

The three roles never collide because each binds from its own named sub-section.

### Why explicit sub-sections (vs flat binding)

`HubOptions` and `HttpConnectionDispatcherOptions` are different SignalR types configuring different layers (Hub method invocation vs. HTTP connection dispatch). They share zero property names today, but a flat-binding pattern would still leave intent ambiguous to readers and risk silent collisions if Microsoft ever ships overlapping properties. The explicit sub-sections make the layer split visible in the JSON itself and let JSON schemas validate each sub-section strictly. Same design decision applied uniformly across this package's `HttpOptions` binding and L5's `HubOptions` bindings.

---

## Compatibility

- **Strictly source-compatible** with v1.0.1. `SignalRInvocationRegistrar`'s public surface is unchanged.
- **Strictly behavior-compatible** for v1.0.1 instances that didn't declare an `HttpOptions` sub-section — binding an empty section is a no-op and `HttpConnectionDispatcherOptions` retains its ASP.NET defaults.
- Apps that already had an `HttpOptions` sub-section in their instance config (none expected — the sub-section had no consumer prior to v1.0.2) would now see those values applied to the connection dispatcher.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.0.2`.
- [`Cirreum.Runtime.Invocation.SignalR`](https://www.nuget.org/packages/Cirreum.Runtime.Invocation.SignalR) — the L5 package that pairs with this release; floors at `1.0.2+` to consume the per-mapping options binding.
