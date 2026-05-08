namespace Cirreum.Invocation.SignalR;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

/// <summary>
/// <see cref="IInvocationContext"/> for SignalR-sourced invocations. Carries the
/// per-method snapshot of the authenticated principal, the per-invocation DI scope, the
/// invocation cancellation token, and the parent <see cref="IInvocationConnection"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Items"/> is a fresh per-invocation dictionary — distinct from the
/// per-connection <see cref="IInvocationConnection.Items"/>. Consumers that need state
/// outliving a single Hub method invocation should write to
/// <c>Connection.Items</c>, not here.
/// </para>
/// <para>
/// Used both for in-flight Hub method invocations (via <see cref="InvocationContextHubFilter"/>'s
/// <c>InvokeMethodAsync</c>) and for synthetic invocation scopes around connection
/// lifecycle hooks (<c>OnConnectedAsync</c> / <c>OnDisconnectedAsync</c>) so consumers
/// like <c>IUserStateAccessor</c> work normally inside <see cref="IConnectionLifecycle"/>
/// callbacks — see ADR-0002 transport-adapter invariant #7.
/// </para>
/// </remarks>
internal sealed class SignalRInvocationContext(
	SignalRConnection connection,
	IServiceProvider services
) : IInvocationContext {

	public ClaimsPrincipal User { get; } = connection.User;

	public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

	public IServiceProvider Services { get; } = services;

	/// <summary>
	/// Gets a cancellation token that is triggered when the connection is aborted.
	/// </summary>
	/// <remarks>
	/// SignalR has no per-Hub-method cancellation distinct from the connection's
	/// <see cref="HubCallerContext.ConnectionAborted"/>. Per-invocation Aborted
	/// degenerates to connection.Aborted — the L2 contract is satisfied because the
	/// "fires when connection.Aborted fires" requirement is trivially met when the
	/// two are the same token.
	/// </remarks>
	public CancellationToken Aborted { get; } = connection.Aborted;

	public string InvocationSource => InvocationSources.SignalR;

	public IInvocationConnection? Connection { get; } = connection;

}