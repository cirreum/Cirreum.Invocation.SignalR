namespace Cirreum.Invocation.SignalR;

/// <summary>
/// DI-stashed mapping from configuration instance key to the Hub <see cref="Type"/>
/// registered at the L5 call site (<c>builder.AddInvocation(b =&gt; b.AddSignalR&lt;THub&gt;("chat"))</c>).
/// Resolved by <see cref="SignalRInvocationRegistrar.MapSource"/> during the endpoints
/// phase to call <c>endpoints.MapHub(HubType, settings.Path)</c>.
/// </summary>
/// <remarks>
/// Lives at L3 because both producers (the L5 <c>AddSignalR&lt;THub&gt;</c> extension)
/// and consumers (this package's registrar) need to reference it. L5 references L3,
/// so this is the natural shared home.
/// </remarks>
public sealed record SignalRHubMapping(string InstanceKey, Type HubType);
