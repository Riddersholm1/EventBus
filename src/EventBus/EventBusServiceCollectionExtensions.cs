using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventBus;

/// <summary>
/// <see cref="IServiceCollection"/> extensions for registering
/// <see cref="IEventBus"/>.
/// </summary>
public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEventBus"/> with the specified <paramref name="lifetime"/>.
    /// Use <see cref="ServiceLifetime.Scoped"/> for Blazor Server (one bus per circuit/user);
    /// use <see cref="ServiceLifetime.Singleton"/> for desktop (MAUI/WPF), worker services,
    /// or app-wide messaging where a single process-wide bus is wanted.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="lifetime">The lifetime to register the bus with.</param>
    /// <returns>The same <paramref name="services"/> instance so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddEventBus(this IServiceCollection services, ServiceLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAdd(new ServiceDescriptor(typeof(IEventBus), typeof(EventBus), lifetime));
        return services;
    }
}