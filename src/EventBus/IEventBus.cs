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
/// order they were subscribed; asynchronous handlers are awaited sequentially by
/// <see cref="PublishAsync{TEvent}(TEvent, CancellationToken)"/>. Synchronous
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
    /// <remarks>
    /// This overload is for genuinely synchronous work. An async lambda binds to
    /// one of the <see cref="Task"/>-returning overloads instead, so
    /// <c>Subscribe&lt;T&gt;(async e =&gt; await …)</c> is awaited properly
    /// rather than becoming a fire-and-forget <c>async void</c> handler.
    /// </remarks>
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
    /// Use this overload when the handler does not need the publisher's
    /// <see cref="CancellationToken"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Typically, a <c>sealed record</c>.</typeparam>
    /// <param name="handler">
    /// The async callback invoked each time an event of this type is published.
    /// It is awaited before the next handler runs, and must not return
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A subscription token. Dispose it to unsubscribe. The token is safe to
    /// dispose multiple times.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : notnull
    {
        // Default implementation so that pre-existing external IEventBus
        // implementations (hand-written test doubles, for instance) keep
        // compiling. EventBus itself provides a direct implementation.
        ArgumentNullException.ThrowIfNull(handler);
        return Subscribe<TEvent>((e, _) => handler(e));
    }

    /// <summary>
    /// Subscribes an asynchronous handler that receives the publisher's
    /// <see cref="CancellationToken"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type. Typically, a <c>sealed record</c>.</typeparam>
    /// <param name="handler">
    /// The async callback invoked each time an event of this type is published.
    /// Receives the event and a <see cref="CancellationToken"/> flowed from the
    /// publisher. It is awaited before the next handler runs, and must not
    /// return <see langword="null"/>.
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
    /// <remarks>
    /// <para>
    /// Routing uses the compile-time <typeparamref name="TEvent"/>, not
    /// <c>eventData.GetType()</c>. Publishing through a base-typed variable —
    /// <c>PublishAsync&lt;object&gt;(concreteEvent)</c> — therefore reaches only
    /// subscribers of that base type. Let <typeparamref name="TEvent"/> be
    /// inferred from a concrete event to get the expected routing.
    /// </para>
    /// <para>
    /// Handlers run on the publishing thread's context; a handler that publishes
    /// its own event type recurses and will exhaust the stack.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="eventData">The event instance. Must not be <see langword="null"/>.</param>
    /// <param name="cancellationToken">
    /// Propagated to every async handler. The loop checks the token between
    /// handlers and cancels the remaining ones if signalled.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="eventData"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. If handlers had already
    /// thrown before the cancellation was observed, their exceptions are preserved
    /// as an inner <see cref="AggregateException"/> rather than discarded.
    /// </exception>
    /// <exception cref="AggregateException">One or more handlers threw. Each inner exception is preserved.</exception>
    Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull;
}
