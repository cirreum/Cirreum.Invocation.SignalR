# Cirreum.Invocation.SignalR 1.0.1 — Public `ProviderKey` constant

A naming-hygiene patch immediately after v1.0.0. Promotes the SignalR provider-name string from an internal magic literal to a publicly-referenceable `const` on the registrar, so L5 consumers (and anyone else filtering by provider name) can reference `SignalRInvocationRegistrar.ProviderKey` instead of hardcoding `"SignalR"` themselves.

Single small change. No behavior change. Runtime value unchanged.

---

## Why this release exists

When v1.0.0 shipped, `SignalRInvocationRegistrar.ProviderName` returned a literal `"SignalR"` string. That literal value is meaningful to consumers — most directly to the L5 `Cirreum.Runtime.Invocation.SignalR` package's `MapSignalRInvocation` implementation, which has to resolve `IEnumerable<InvocationProviderMapping>` from DI and filter by `ProviderName == "SignalR"` to map only SignalR-tagged endpoints.

In v1.0.0, the L5 had to redeclare its own `private const string SignalRProviderName = "SignalR"` to do that comparison — two unrelated string literals describing the same concept, with nothing keeping them in sync. If either side ever drifted (typo, rename), filtering would silently break.

The fix is to promote the name to a public `const` on the L3 registrar so there's exactly one source of truth.

---

## What changed

Before (v1.0.0):

```csharp
public sealed class SignalRInvocationRegistrar
    : InvocationProviderRegistrar<SignalRInvocationSettings, SignalRInvocationInstanceSettings> {

    public override string ProviderName => "SignalR";
    // ...
}
```

After (v1.0.1):

```csharp
public sealed class SignalRInvocationRegistrar
    : InvocationProviderRegistrar<SignalRInvocationSettings, SignalRInvocationInstanceSettings> {

    /// <summary>
    /// The provider key used to identify the SignalR provider in configuration or
    /// service registration scenarios.
    /// </summary>
    public const string ProviderKey = "SignalR";

    public override string ProviderName => ProviderKey;
    // ...
}
```

Consumers can now write:

```csharp
foreach (var mapping in mappings) {
    if (mapping.ProviderName == SignalRInvocationRegistrar.ProviderKey) {
        mapping.Map(endpoints);
    }
}
```

instead of:

```csharp
private const string SignalRProviderName = "SignalR";  // literal duplicated from L3
// ...
foreach (var mapping in mappings) {
    if (mapping.ProviderName == SignalRProviderName) {
        mapping.Map(endpoints);
    }
}
```

---

## Compatibility

- **Strictly source-compatible** with v1.0.0. `ProviderName` still returns `"SignalR"` exactly as before; `ProviderKey` is a new public addition that didn't exist previously.
- **Strictly binary-compatible** in terms of any v1.0.0 caller's existing call sites — `ProviderName` is unchanged at runtime.
- Anyone who hardcoded `"SignalR"` against the old API can keep doing so; the new `ProviderKey` is opt-in.

---

## See also

- `CHANGELOG.md` — condensed change list for `1.0.1`.
- [`Cirreum.Runtime.Invocation.SignalR`](https://www.nuget.org/packages/Cirreum.Runtime.Invocation.SignalR) — the L5 package this constant primarily benefits; floors at `1.0.1+` to reference `ProviderKey` from `MapSignalRInvocation`.
