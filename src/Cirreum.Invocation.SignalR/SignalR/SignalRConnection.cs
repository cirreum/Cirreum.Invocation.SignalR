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
/// The caller proxy is captured at upgrade and used by the
/// <see cref="SendAsync{T}(string, T, CancellationToken)"/> overloads to push to this
/// specific connection. Captured here (vs. resolved per-send through
/// <c>IHubContext&lt;THub&gt;</c>) because the L3 layer doesn't know <c>THub</c> at
/// compile time.
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

	public void Abort() {
		// HubCallerContext.Abort() is SignalR's official termination path — cancels
		// ConnectionAborted, drains the connection, and triggers the Hub's
		// OnDisconnectedAsync. Idempotent per SignalR contract.
		context.Abort();
	}

	public ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
		// Route by runtime type name when no method is specified — natural fit for SignalR
		// clients that listen via connection.on(MessageType, handler).
		var method = payload?.GetType().Name ?? typeof(T).Name;
		return this.SendAsync(method, payload, cancellationToken);
	}

	public async ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
		// SendAsync on the captured ISingleClientProxy returns Task; the SignalR pipeline
		// handles all serialization through the configured IHubProtocol (JSON or MessagePack).
		await callerProxy.SendAsync(method, payload, cancellationToken);
	}

}