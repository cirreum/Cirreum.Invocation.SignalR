# Cirreum.Invocation.SignalR 1.2.0 — `SendAsync` moves onto the connection

Implements the new `IInvocationConnection.SendAsync<T>` overloads from `Cirreum.InvocationProvider 1.3.0` directly on `SignalRConnection`, and deletes the standalone `SignalRConnectionSender` (the `IConnectionSender` impl) along with its scoped DI registration. Wire bytes are unchanged — the new connection-bound overloads forward to the same captured `ISingleClientProxy.SendAsync(method, payload, ct)` the sender used.

---

## Why this release exists

`SignalRConnectionSender` was a 30-line scoped service whose entire job was:

```csharp
var connection = accessor.Current?.Connection as SignalRConnection
    ?? throw new InvalidOperationException("...");
await connection.CallerProxy.SendAsync(method, payload, ct);
```

Every consumer that called it was already in (or directly under) a SignalR Hub method — the same place where `accessor.Current?.Connection` is non-null and points at the right `SignalRConnection`. The L2 consolidation (see `Cirreum.InvocationProvider 1.3.0`) puts `SendAsync<T>` on the connection itself; the wrapper has no purpose.

---

## What's new

### `SignalRConnection.SendAsync<T>` — two overloads

Both overloads forward to the captured `ISingleClientProxy.SendAsync(method, payload, ct)`:

```csharp
public ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
    var method = payload?.GetType().Name ?? typeof(T).Name;
    return this.SendAsync(method, payload, cancellationToken);
}

public async ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
    await callerProxy.SendAsync(method, payload, cancellationToken);
}
```

| Behavior | Detail |
|---|---|
| Method routing (no-method overload) | Uses the runtime payload type name (matches `connection.on("ChatMessage", handler)` convention). |
| Method routing (keyed overload) | Uses the explicit method name. |
| Serialization | Routed through the configured `IHubProtocol` (JSON or MessagePack). Apps control this via `AddSignalR().AddJsonProtocol(...)` / `.AddMessagePackProtocol()`. |
| Connection scope | Sends to the calling client only — same as `Clients.Caller` from inside a Hub method. |

The `CallerProxy` capture pattern (`ISingleClientProxy` snapshotted at upgrade time and held on the connection) is unchanged from 1.1.0. SignalR's `SingleClientProxy` is effectively a `(HubLifetimeManager, ConnectionId)` tuple — no per-method-invocation state, no captured DI scope — so capture-once-and-forward is safe.

---

## What's removed

### `SignalRConnectionSender`

Deleted, along with its scoped DI registration in `SignalRInvocationRegistrar.RegisterSource`. Cross-cutting code that injected it switches to ambient-accessor + connection:

```diff
  public sealed class NotifyHandler(
-     IInvocationContextAccessor accessor,
-     IConnectionSender sender) : ICommandHandler<NotifyCommand> {
+     IInvocationContextAccessor accessor) : ICommandHandler<NotifyCommand> {

      public async ValueTask<Result> Handle(NotifyCommand cmd, CancellationToken ct) {
-         await sender.SendAsync("Notification", cmd.Payload, ct);
+         await accessor.Current?.Connection?.SendAsync("Notification", cmd.Payload, ct);
          return Result.Success();
      }
  }
```

Hub method bodies that already used `Clients.Caller.SendAsync(...)` directly are unaffected — only cross-cutting code paths that pushed via the framework abstraction need the migration.

---

## Coordinated upstream work

This release requires `Cirreum.InvocationProvider 1.3.0` (the L2 contract change). It ships in lockstep with `Cirreum.Invocation.WebSockets 1.2.0` (parallel adapter update) and `Cirreum.Runtime.Invocation.SignalR 1.1.0` (flow-through dep bump).

---

## Compatibility

- **Source-incompatible** for app code that injected `IConnectionSender` (typed find/replace migration above; ~1 line per call site).
- **Source- and binary-compatible** for Hub method code using `Clients.Caller.SendAsync(...)` directly — that surface is SignalR's, not Cirreum's.
- All other surface (`SignalRConnection.Abort`, `SignalRHubMapping`, `InvocationContextHubFilter`, the registrar) is unchanged.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.2.0`.
- [`Cirreum.InvocationProvider 1.3.0`](https://www.nuget.org/packages/Cirreum.InvocationProvider) — the L2 consolidation that motivated this release.
- [`Cirreum.Invocation.WebSockets 1.2.0`](https://www.nuget.org/packages/Cirreum.Invocation.WebSockets) — parallel adapter update.
- `RELEASE-NOTES-v1.1.0.md` — `SignalRConnection.Abort()` addition.
