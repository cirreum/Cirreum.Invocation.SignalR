namespace Cirreum.Invocation.SignalR;

using Cirreum.Invocation;
using Cirreum.Invocation.Connections;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// <see cref="IConnectionSender"/> for SignalR. Resolves the active
/// <see cref="SignalRConnection"/> from the ambient <see cref="IInvocationContextAccessor"/>
/// and dispatches sends through the captured <see cref="Microsoft.AspNetCore.SignalR.ISingleClientProxy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scoped lifetime — resolved per-invocation via DI. Reads the connection through the
/// ambient accessor rather than via DI directly because the connection is invocation-
/// bound, not service-bound; only the Hub method invocation knows which connection
/// `Caller` refers to.
/// </para>
/// <para>
/// SignalR is a method-routed transport — every wire frame names the receive handler.
/// The no-method <see cref="IConnectionSender.SendAsync{T}(T, CancellationToken)"/>
/// overload uses the runtime type name as the SignalR convention (e.g.
/// <c>SendAsync(new ChatMessage(...))</c> dispatches to the client's
/// <c>connection.on("ChatMessage", ...)</c> handler). For explicit method names, use the
/// keyed overload.
/// </para>
/// </remarks>
internal sealed class SignalRConnectionSender(
	IInvocationContextAccessor accessor
) : IConnectionSender {

	public ValueTask SendAsync<T>(T payload, CancellationToken cancellationToken = default) {
		// Route by runtime type name when no method is specified — natural fit for SignalR
		// clients that listen via connection.on(MessageType, handler).
		var method = payload?.GetType().Name ?? typeof(T).Name;
		return this.SendAsync(method, payload, cancellationToken);
	}

	public async ValueTask SendAsync<T>(string method, T payload, CancellationToken cancellationToken = default) {
		var connection = this.ResolveActiveConnection();
		await connection.CallerProxy.SendAsync(method, payload, cancellationToken);
	}

	private SignalRConnection ResolveActiveConnection() {
		var invocation = accessor.Current
			?? throw new InvalidOperationException(
				"IConnectionSender requires an active invocation. Inject this from a SignalR Hub method or other code that runs inside the Cirreum invocation pipeline.");

		return invocation.Connection as SignalRConnection
			?? throw new InvalidOperationException(
				$"IConnectionSender requires a SignalR-sourced invocation; the active invocation source is '{invocation.InvocationSource}'.");
	}

}