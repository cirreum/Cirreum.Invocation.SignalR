namespace Cirreum.Invocation.SignalR;

using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

/// <summary>
/// <see cref="IInvocationConnection"/> for a single long-lived SignalR connection.
/// Wraps SignalR's per-connection <see cref="HubCallerContext"/> as the unified seam for
/// per-connection state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Items"/> is aliased to <see cref="HubCallerContext.Items"/> — same dictionary
/// reference, no copy. Per-connection state set by either SignalR-aware code (the
/// HubFilter, the Hub itself) or framework code (the role-claims transformer's
/// equivalent for connections, when it lands) flows through transparently.
/// </para>
/// <para>
/// <see cref="User"/> is snapshotted at upgrade time — immutable per the L2 contract.
/// SignalR does not re-authenticate per Hub method invocation, so the principal is
/// effectively connection-scoped from the framework's perspective.
/// </para>
/// <para>
/// <see cref="CallerProxy"/> is captured at upgrade and used by
/// <see cref="SignalRConnectionSender"/> to push to this specific connection. Captured
/// here (vs. resolved per-send through <c>IHubContext&lt;THub&gt;</c>) because the L3
/// layer doesn't know <c>THub</c> at compile time.
/// </para>
/// </remarks>
internal sealed class SignalRConnection(
	HubCallerContext context,
	ISingleClientProxy callerProxy,
	DateTimeOffset connectedAtUtc
) : IInvocationConnection {

	public string ConnectionId => context.ConnectionId;

	public ClaimsPrincipal User { get; } = context.User ?? new ClaimsPrincipal();

	public DateTimeOffset ConnectedAtUtc { get; } = connectedAtUtc;

	public IDictionary<object, object?> Items => context.Items;

	public string InvocationSource => InvocationSources.SignalR;

	public CancellationToken Aborted => context.ConnectionAborted;

	/// <summary>
	/// Proxy to this connection's caller, captured at upgrade. Used by
	/// <see cref="SignalRConnectionSender"/> for server-initiated push.
	/// </summary>
	/// <remarks>
	/// Capturing relies on SignalR's <c>SingleClientProxy</c> being effectively a
	/// (HubLifetimeManager, ConnectionId) tuple — no per-method-invocation state,
	/// no captured DI scope. This has held across every SignalR release to date,
	/// but is an implementation detail rather than a documented invariant. If a
	/// future SignalR release changes that, switch to lazy resolution through
	/// IHubContext&lt;THub&gt; per send.
	/// </remarks>
	internal ISingleClientProxy CallerProxy { get; } = callerProxy;

}