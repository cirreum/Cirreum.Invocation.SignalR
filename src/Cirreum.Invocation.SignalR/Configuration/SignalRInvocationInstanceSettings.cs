namespace Cirreum.Invocation.Configuration;

/// <summary>
/// Instance settings for a SignalR invocation source.
/// Each instance represents one Hub mapped at a configured path.
/// </summary>
/// <remarks>
/// V1 carries no SignalR-specific fields beyond the base — the base
/// <see cref="InvocationProviderInstanceSettings.Section"/> exposes the raw config
/// section so apps can override any standard <c>HubOptions</c> field through
/// configuration without Cirreum re-defining them.
/// </remarks>
public sealed class SignalRInvocationInstanceSettings
	: InvocationProviderInstanceSettings;
