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
/// At construction, the framework seeds the per-invocation bag with the well-known
/// authentication slots (<c>AuthenticatedScheme</c>, <c>ApplicationUserCache</c>) from
/// <c>Connection.Items</c> so consumers like <c>UserStateAccessor</c> read uniformly
/// across HTTP and SignalR without re-resolving the application user on every Hub method
/// invocation. The seed is a snapshot copy — per-invocation writes do NOT propagate back
/// to <c>Connection.Items</c> (per-message state isolation; ADR-0002 invariant #6). The
/// connection-lifetime values were placed onto <c>Connection.Items</c> at upgrade by
/// <see cref="InvocationContextHubFilter.OnConnectedAsync"/>.
/// </para>
/// <para>
/// Used both for in-flight Hub method invocations (via <see cref="InvocationContextHubFilter"/>'s
/// <c>InvokeMethodAsync</c>) and for synthetic invocation scopes around connection
/// lifecycle hooks (<c>OnConnectedAsync</c> / <c>OnDisconnectedAsync</c>) so consumers
/// like <c>IUserStateAccessor</c> work normally inside <see cref="IConnectionLifecycle"/>
/// callbacks — see ADR-0002 transport-adapter invariant #7.
/// </para>
/// </remarks>
internal sealed class SignalRInvocationContext : IInvocationContext {

	internal SignalRInvocationContext(
		SignalRConnection connection,
		IServiceProvider services) {

		this.User = connection.User;
		this.Services = services;
		this.Aborted = connection.Aborted;
		this.Connection = connection;
		this.Items = SeedAuthSlots(connection);
	}

	public ClaimsPrincipal User { get; }

	public IDictionary<object, object?> Items { get; }

	public IServiceProvider Services { get; }

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
	public CancellationToken Aborted { get; }

	public string InvocationSource => InvocationSources.SignalR;

	public IInvocationConnection? Connection { get; }

	private static Dictionary<object, object?> SeedAuthSlots(SignalRConnection connection) {

		// Per-invocation Items starts as a fresh dictionary, seeded with the well-known
		// authentication slots from Connection.Items. These are connection-lifetime values
		// (the same authenticated identity owns the entire connection); seeding them
		// per-invocation lets UserStateAccessor and other consumers read invocation.Items
		// without needing to know about Connection.Items. App per-message writes to
		// invocation.Items don't propagate back to Connection.Items (separate dicts) —
		// per-message isolation per ADR-0002 invariant #6.
		var dict = new Dictionary<object, object?>();
		if (connection.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
			dict[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
		}
		if (connection.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
			dict[AuthenticationContextKeys.ApplicationUserCache] = appUser;
		}
		return dict;
	}

}
