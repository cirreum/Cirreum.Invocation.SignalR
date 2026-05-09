# Cirreum.Invocation.SignalR 1.1.0 — `SignalRConnection.Abort()`

Implements the new `IInvocationConnection.Abort()` member from `Cirreum.InvocationProvider 1.2.0`. SignalR-sourced connections now expose the same explicit termination primitive as every other long-lived invocation source.

Coordinated minor — bumps the L2 dependency to 1.2.0 and forwards `Abort()` to SignalR's native `HubCallerContext.Abort()`.

---

## Why this release exists

`Cirreum.InvocationProvider 1.2.0` adds `void Abort()` to `IInvocationConnection` to give handlers a uniform way to terminate connections (server-side timeout, app-level kick, multi-socket orchestration). Every long-lived source adapter must implement it. This release adds the SignalR side.

---

## What's new

### `SignalRConnection.Abort()`

```csharp
public void Abort() {
    context.Abort();   // HubCallerContext.Abort()
}
```

Wraps SignalR's native termination path:

- Cancels `HubCallerContext.ConnectionAborted` (which `IInvocationConnection.Aborted` exposes)
- Drains the connection in SignalR's pipeline
- Triggers the Hub's `OnDisconnectedAsync(HubLifetimeContext, Exception?)` with a non-null exception, which our `InvocationContextHubFilter` maps to `DisconnectInfo` for `IConnectionLifecycle.OnDisconnectedAsync` hooks

Idempotent per SignalR's own contract — calling on an already-aborted connection is a no-op.

---

## Why minor and not patch

Adding a public method to a public type is additive per SemVer — that's a minor bump. `PatchRelease.ps1` only allows `Fixed` and `Security` changelog sections; an `Added` entry for the new method belongs under a minor release.

---

## Compatibility

- **Source-** and **binary-compatible** with v1.0.2 for all existing API surface (the registrar, hub filter, settings, mapping record, sender, invocation context).
- Bumps `Cirreum.InvocationProvider` from 1.1.0 to 1.2.0 (additive interface change — no consumer impact).
- No changes to configuration shape, registration extensions, or behavior of existing methods.

---

## See also

- `CHANGELOG.md` — condensed change list for 1.1.0.
- [`Cirreum.InvocationProvider 1.2.0`](https://www.nuget.org/packages/Cirreum.InvocationProvider) — the upstream contract this release implements.
- [`Cirreum.Invocation.WebSockets 1.0.0`](https://www.nuget.org/packages/Cirreum.Invocation.WebSockets) — companion release, ships against the same upstream 1.2.0 contract.
