namespace Cirreum.Invocation;

using Cirreum.Invocation.Configuration;
using Cirreum.Invocation.Connections;
using Cirreum.Invocation.SignalR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;

/// <summary>
/// Registrar for the SignalR invocation source. Maps SignalR Hubs from
/// <c>Cirreum:Invocation:Providers:SignalR</c> configuration and wires the
/// <see cref="InvocationContextHubFilter"/> that publishes <see cref="IInvocationContext"/>
/// per Hub method invocation.
/// </summary>
/// <remarks>
/// <para>
/// Hub-type-agnostic: this registrar does not know which concrete <c>THub</c> to map
/// for a given instance. The L5 <c>AddSignalR&lt;THub&gt;(instanceKey)</c> extension
/// stashes a <see cref="SignalRHubMapping"/> in DI carrying the (instanceKey, HubType)
/// pair; <see cref="MapSource"/> resolves it and dispatches through reflection to
/// <c>endpoints.MapHub&lt;THub&gt;(settings.Path)</c> at endpoints-phase time (ASP.NET
/// ships only a generic <c>MapHub&lt;THub&gt;</c> overload).
/// </para>
/// </remarks>
public sealed class SignalRInvocationRegistrar
	: InvocationProviderRegistrar<SignalRInvocationSettings, SignalRInvocationInstanceSettings> {

	/// <summary>
	/// Represents the provider key used to identify the SignalR provider in configuration or service registration
	/// scenarios.
	/// </summary>
	public const string ProviderKey = "SignalR";

	/// <inheritdoc/>
	public override string ProviderName => ProviderKey;

	/// <inheritdoc/>
	public override void ValidateSettings(SignalRInvocationInstanceSettings settings) {

		// Base validates Path is non-empty; we add the protocol-shape check.
		if (!settings.Path.StartsWith('/')) {
			throw new InvalidOperationException(
				$"SignalR provider instance Path '{settings.Path}' must start with '/'.");
		}

	}

	/// <inheritdoc/>
	protected override void RegisterSource(
		string key,
		SignalRInvocationInstanceSettings settings,
		IServiceCollection services,
		IConfiguration configuration) {

		// SignalR core registration is process-wide and idempotent — safe to call
		// once per instance. Per-instance HubOptions overrides flow through the
		// standard HubOptions<THub> pipeline at L5 (where THub is known).
		services.AddSignalR();

		// Register the framework HubFilter that publishes IInvocationContext per
		// Hub method invocation. AddFilter on the global HubOptions applies the
		// filter to every Hub registered in the host — one filter, all hubs.
		// Idempotent via TryAddSingleton; Configure runs once per call but is
		// internally deduped by the options system.
		services.TryAddSingleton<InvocationContextHubFilter>();
		services.Configure<HubOptions>(o => o.AddFilter<InvocationContextHubFilter>());

	}

	/// <inheritdoc/>
	protected override void MapSource(
		string key,
		SignalRInvocationInstanceSettings settings,
		IEndpointRouteBuilder endpoints) {

		var mapping = endpoints.ServiceProvider
			.GetServices<SignalRHubMapping>()
			.FirstOrDefault(m => m.InstanceKey == key)
			?? throw new InvalidOperationException(
				$"No SignalRHubMapping found for instance '{key}'. " +
				$"Did you call builder.AddInvocation(b => b.AddSignalR<THub>(\"{key}\")) at the L5 layer?");

		// Bind the instance's HttpOptions sub-section to HttpConnectionDispatcherOptions —
		// the per-mapping config surface for transport-level concerns (Transports,
		// ApplicationMaxBufferSize, TransportMaxBufferSize, LongPolling.PollTimeout,
		// WebSockets.CloseTimeout, MinimumProtocolVersion, CloseOnAuthenticationExpiration).
		// HubOptions properties live in a sibling HubOptions sub-section bound by L5
		// AddSignalR<THub>; Cirreum framework fields (Enabled, Path, Scheme) live at the
		// instance-section root. The three roles never collide.
		var hub = MapHubByType(endpoints, mapping.HubType, settings.Path,
			options => settings.Section?.GetSection("HttpOptions").Bind(options));

		// Wire up the auth scheme reference if specified. Scheme references a
		// configured Authorization instance under
		// Cirreum:Authorization:Providers:*:Instances:{Scheme}.
		if (!string.IsNullOrWhiteSpace(settings.Scheme)) {
			hub.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = settings.Scheme });
		}

	}

	// ASP.NET's MapHub extension is generic-only — there is no MapHub(Type, string, ...)
	// overload in the public API. We dispatch through reflection to the 3-param
	// MapHub<THub>(string, Action<HttpConnectionDispatcherOptions>) overload so we can
	// pass per-mapping HttpConnectionDispatcherOptions configuration. Acceptable because
	// endpoint mapping happens once at startup.
	private static readonly MethodInfo _mapHubGenericMethod = typeof(HubEndpointRouteBuilderExtensions)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.First(m => m.Name == nameof(HubEndpointRouteBuilderExtensions.MapHub)
			&& m.IsGenericMethod
			&& m.GetParameters().Length == 3);

	private static HubEndpointConventionBuilder MapHubByType(
		IEndpointRouteBuilder endpoints,
		Type hubType,
		string path,
		Action<HttpConnectionDispatcherOptions> configureOptions) {

		var concrete = _mapHubGenericMethod.MakeGenericMethod(hubType);
		return (HubEndpointConventionBuilder)concrete.Invoke(null, [endpoints, path, configureOptions])!;

	}

}
