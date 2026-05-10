namespace Cirreum.Invocation.SignalR;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// SignalR <see cref="IHubFilter"/> that publishes <see cref="IInvocationContext"/>
/// for every Hub method invocation, materializes <see cref="IInvocationConnection"/>
/// at upgrade, and dispatches <see cref="IConnectionLifecycle"/> callbacks at
/// connection lifecycle boundaries.
/// </summary>
/// <remarks>
/// SignalR-side mirror of <c>InvocationContextHttpMiddleware</c> on the HTTP side, with
/// the additional connection-lifecycle responsibility long-lived sources require. One
/// filter applies globally (registered via <c>HubOptions.AddFilter</c>); per-connection
/// state is keyed by <see cref="HubCallerContext"/> in
/// <see cref="HubCallerContext.Items"/>.
/// </remarks>
internal sealed class InvocationContextHubFilter(
	IInvocationContextAccessor accessor,
	TimeProvider timeProvider
) : IHubFilter {

	private static readonly object _connectionItemsKey = new();

	/// <summary>
	/// Called once per connection at upgrade, before any Hub method dispatches. Captures
	/// the SignalR <see cref="ISingleClientProxy"/> for server-push, materializes the
	/// per-connection <see cref="SignalRConnection"/>, runs registered
	/// <see cref="IConnectionLifecycle.OnConnectedAsync"/> hooks under a synthetic
	/// invocation scope, and stashes the connection in <see cref="HubCallerContext.Items"/>
	/// for later retrieval by <see cref="InvokeMethodAsync"/>.
	/// </summary>
	public async Task OnConnectedAsync(
		HubLifetimeContext hubLifetimeContext,
		Func<HubLifetimeContext, Task> next) {

		// Stash the connection in Context.Items for later retrieval by InvokeMethodAsync. This
		// is necessary because HubLifetimeContext doesn't have a way to flow state from OnConnectedAsync
		// to InvokeMethodAsync, and HubCallerContext is the only state bag that flows across both.
		var hubInstance = hubLifetimeContext.Hub;
		var callerProxy = hubInstance.Clients.Caller;
		var connection = new SignalRConnection(
			hubLifetimeContext.Context,
			callerProxy,
			timeProvider.GetUtcNow());
		hubLifetimeContext.Context.Items[_connectionItemsKey] = connection;

		// Copy well-known authentication slots from the upgrade-time HttpContext.Items onto
		// Connection.Items so the connection-lifetime bag carries the auth context that the
		// HTTP middleware established for this connection's upgrade request. The forward
		// selector always stamps AuthenticatedScheme; the audience-auth claims transformer
		// stamps ApplicationUserCache when an IApplicationUserResolver matches. Per-Hub-method
		// SignalRInvocationContext construction seeds per-invocation Items from these slots,
		// so consumers like UserStateAccessor read uniformly across HTTP and SignalR without
		// hitting the IdP on every Hub method invocation. See ADR-0002 transport-adapter
		// invariant #2.
		var httpContext = hubLifetimeContext.Context.GetHttpContext();
		if (httpContext is not null) {
			if (httpContext.Items.TryGetValue(AuthenticationContextKeys.AuthenticatedScheme, out var scheme)) {
				connection.Items[AuthenticationContextKeys.AuthenticatedScheme] = scheme;
			}
			if (httpContext.Items.TryGetValue(AuthenticationContextKeys.ApplicationUserCache, out var appUser)) {
				connection.Items[AuthenticationContextKeys.ApplicationUserCache] = appUser;
			}
		}

		// Synthetic invocation scope so IUserStateAccessor and other ambient consumers
		// work normally inside IConnectionLifecycle callbacks (ADR-0002 invariant #7).
		var invocation = new SignalRInvocationContext(
			connection,
			hubLifetimeContext.ServiceProvider);
		accessor.Set(invocation);
		try {

			// Run OnConnectedAsync hooks. If any hook rejects the connection, abort the context to prevent
			// the connection from being established.
			//
			// Note that we run these hooks before calling next() to prevent the connection from being established
			// at all if any hook rejects it. This is important for security-related hooks that want to reject
			// unauthorized connections, as well as for resource management (e.g. rejecting connections when the
			// server is under heavy load).
			//
			// Also note that we run these hooks sequentially and abort on the first rejection. This is a design
			// choice that simplifies the contract for IConnectionLifecycle implementations (they don't have to
			// worry about concurrent execution or partial completion), and it also allows us to short-circuit the
			// connection process as soon as a rejection occurs, which can save resources.
			var lifecycles = hubLifetimeContext.ServiceProvider.GetServices<IConnectionLifecycle>();
			foreach (var lifecycle in lifecycles) {
				var accepted = await lifecycle.OnConnectedAsync(connection, connection.Aborted);
				if (!accepted) {
					hubLifetimeContext.Context.Abort();
					return;
				}
			}

			await next(hubLifetimeContext);

		} finally {
			accessor.Clear();
		}

	}

	/// <summary>
	/// Per-Hub-method-invocation hook. Retrieves the cached
	/// <see cref="SignalRConnection"/> stashed at upgrade, builds a per-invocation
	/// <see cref="SignalRInvocationContext"/>, and publishes it through
	/// <see cref="IInvocationContextAccessor"/> for the duration of the method call.
	/// </summary>
	public async ValueTask<object?> InvokeMethodAsync(
		HubInvocationContext hubContext,
		Func<HubInvocationContext, ValueTask<object?>> next) {

		// SignalR's lifecycle guarantees OnConnectedAsync runs (and stashes the connection)
		// before any InvokeMethodAsync. If the stash is missing here, something upstream
		// has gone wrong — surface a clear diagnostic instead of an opaque cast/lookup failure.
		if (!hubContext.Context.Items.TryGetValue(_connectionItemsKey, out var stashed)
			|| stashed is not SignalRConnection connection) {
			throw new InvalidOperationException(
				"InvocationContextHubFilter cannot resolve the per-connection state stashed in OnConnectedAsync. " +
				"The SignalR lifecycle guarantees OnConnectedAsync runs before any InvokeMethodAsync, so this " +
				"indicates a filter-ordering or HubCallerContext.Items mutation problem upstream. Check that no " +
				"other IHubFilter is removing items from HubCallerContext.Items between our hooks.");
		}

		var invocation = new SignalRInvocationContext(
			connection,
			hubContext.ServiceProvider);
		accessor.Set(invocation);
		try {
			return await next(hubContext);
		} finally {
			accessor.Clear();
		}

	}

	/// <summary>
	/// Called once per connection at disconnect. Runs registered
	/// <see cref="IConnectionLifecycle.OnDisconnectedAsync"/> hooks under a synthetic
	/// invocation scope. Exceptions from individual hooks are absorbed per the L2
	/// contract — one app's bug does not block other registered hooks or the framework's
	/// own teardown.
	/// </summary>
	public async Task OnDisconnectedAsync(
		HubLifetimeContext hubLifetimeContext,
		Exception? exception,
		Func<HubLifetimeContext, Exception?, Task> next) {

		// Connection may not be in Items if OnConnectedAsync rejected the upgrade
		// before we cached it. Fall through to next() in that case.
		if (hubLifetimeContext.Context.Items.TryGetValue(_connectionItemsKey, out var stashed)
			&& stashed is SignalRConnection connection) {

			// Map SignalR's optional Exception parameter to the L2 disconnect info.
			// SignalR convention: null exception = graceful close; non-null = abort/error.
			var info = new DisconnectInfo(
				WasGraceful: exception is null,
				Exception: exception,
				Reason: exception?.Message);

			var invocation = new SignalRInvocationContext(
				connection,
				hubLifetimeContext.ServiceProvider);
			accessor.Set(invocation);
			try {
				var lifecycles = hubLifetimeContext.ServiceProvider.GetServices<IConnectionLifecycle>();
				foreach (var lifecycle in lifecycles) {
					try {
						await lifecycle.OnDisconnectedAsync(connection, info, connection.Aborted);
					} catch {
						// Per L2 contract: exceptions from disconnect hooks are absorbed.
					}
				}
			} finally {
				accessor.Clear();
				hubLifetimeContext.Context.Items.Remove(_connectionItemsKey);
			}

		}

		await next(hubLifetimeContext, exception);

	}

}
