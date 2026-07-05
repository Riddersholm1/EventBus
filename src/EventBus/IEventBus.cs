using Microsoft.Extensions.DependencyInjection;

namespace EventBus;

/// <summary>
/// In-process event aggregator for loosely coupled publish/subscribe messaging
/// between components.
/// </summary>
/// <remarks>
/// <para>
/// Register an <see cref="IEventBus"/> via
/// <c>services.AddEventBus(ServiceLifetime.Scoped)</c>. The lifetime determines
/// the messaging boundary: <see cref="ServiceLifetime.Scoped"/> gives each DI
/// scope its own isolated bus (for example, one per Blazor Server circuit or per
/// web request), while <see cref="ServiceLifetime.Singleton"/> shares a single
/// process-wide bus (suitable for desktop apps, worker services, or app-wide
/// messaging).
/// </para>
/// <para>
/// Subscriptions returned from the <c>Subscribe</c> overloads are
/// <see cref="IDisposable"/>. Dispose them when the subscriber is torn down to
/// stop receiving events. Failing to do so holds a strong reference to the
/// subscriber for the lifetime of the bus.
/// </para>
/// <para>
/// All members are safe to call concurrently. Handlers are invoked in the
/// order they were subscribed; async handlers are awaited sequentially by
/// <see cref="PublishAsync{TEvent}(TEvent, CancellationToken)"/>. Sync
/// handlers (registered via the <see cref="Action{T}"/> overload of
/// <see cref="Subscribe{TEvent}(Action{TEvent})"/>) execute inline on the
/// publishing thread.
/// </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// Subscribes a synchronous handler for events of type <typeparamref name="TEvent"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Typically, a <c>sealed record</c>.</typeparam>
    /// <param name="handler">The callback invoked each time an event of this type is published.</param>
    /// <returns>
    /// A subscription token. Dispose it to unsubscribe. The token is safe to
    /// dispose multiple times.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;

    /// <summary>
    /// Subscribes an asynchronous handler for events of type <typeparamref name="TEvent"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Typically, a <c>sealed record</c>.</typeparam>
    /// <param name="handler">
    /// The async callback invoked each time an event of this type is published.
    /// Receives the event and a <see cref="CancellationToken"/> flowed from the publisher.
    /// </param>
    /// <returns>
    /// A subscription token. Dispose it to unsubscribe. The token is safe to
    /// dispose multiple times.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : notnull;

    /// <summary>
    /// Publishes an event to all handlers (both synchronous and asynchronous).
    /// Sync handlers execute inline; async handlers are awaited one after another
    /// in subscription order.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="eventData">The event instance. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">
    /// Propagated to every async handler. The loop checks the token between
    /// handlers and cancels the remaining ones if signalled.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="eventData"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="AggregateException">One or more handlers threw. Each inner exception is preserved.</exception>
    Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull;
}