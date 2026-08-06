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
    /// <remarks>
    /// Registration is additive-once: if <see cref="IEventBus"/> is already registered,
    /// this call is a no-op and the existing registration wins — <b>including its
    /// lifetime</b>. Calling <c>AddEventBus(ServiceLifetime.Scoped)</c> and then
    /// <c>AddEventBus(ServiceLifetime.Singleton)</c> leaves the bus scoped, silently.
    /// Register the bus once, in composition root.
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="lifetime">The lifetime to register the bus with.</param>
    /// <returns>The same <paramref name="services"/> instance so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="lifetime"/> is not one of the defined <see cref="ServiceLifetime"/> values.
    /// </exception>
    public static IServiceCollection AddEventBus(this IServiceCollection services, ServiceLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(services);

        // An undefined enum value would otherwise be handed to ServiceDescriptor
        // and fail much later, at resolve time, far from the mistake.
        if (!Enum.IsDefined(lifetime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), lifetime, $"Not a defined {nameof(ServiceLifetime)} value.");
        }

        services.TryAdd(new ServiceDescriptor(typeof(IEventBus), typeof(EventBus), lifetime));
        return services;
    }
}
