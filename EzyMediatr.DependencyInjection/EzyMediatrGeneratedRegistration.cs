using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace EzyMediatr.DependencyInjection.Generated;

[EditorBrowsable(EditorBrowsableState.Never)]
public static class EzyMediatrGeneratedRegistration
{
    private static readonly object Gate = new();
    private static Action<IServiceCollection>? _registrations;
    private static bool _runtimeDiscoveryRequired;

    public static void Register(Action<IServiceCollection> registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        lock (Gate)
        {
            _registrations += registration;
        }
    }

    public static void RequireRuntimeDiscovery()
    {
        lock (Gate)
        {
            _runtimeDiscoveryRequired = true;
        }
    }

    internal static bool Apply(IServiceCollection services)
    {
        Action<IServiceCollection>? registrations;
        lock (Gate)
        {
            if (_runtimeDiscoveryRequired || _registrations is null)
            {
                return false;
            }

            registrations = _registrations;
        }

        registrations(services);
        return true;
    }
}
