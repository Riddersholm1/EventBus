using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace EventBus;

/// <summary>
/// Default <see cref="IEventBus"/> implementation backed by a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of
/// <see cref="ImmutableList{T}"/> subscriptions.
/// <see cref="PublishAsync{TEvent}"/> reads a snapshot of the list without
/// locking, so handlers can subscribe or unsubscribe while other handlers are
/// running without corrupting iteration.
/// </summary>
/// <remarks>
/// This type is internal; consume the bus through <see cref="IEventBus"/>.
/// It is made visible to the test assembly via <c>InternalsVisibleTo</c>
/// for white-box testing of disposal semantics.
/// </remarks>
internal sealed class EventBus : IEventBus, IDisposable
{
    private readonly ConcurrentDictionary<Type, ImmutableList<Subscription>> _subscriptions = new();
    private int _disposed;

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new SyncSubscription<TEvent>(this, handler);
        AddSubscription(typeof(TEvent), subscription);
        return subscription;
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new AsyncSubscription<TEvent>(this, handler);
        AddSubscription(typeof(TEvent), subscription);
        return subscription;
    }

    /// <inheritdoc />
    public async Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ThrowIfDisposed();

        if (!_subscriptions.TryGetValue(typeof(TEvent), out ImmutableList<Subscription>? snapshot) || snapshot.IsEmpty)
        {
            return;
        }

        List<Exception>? errors = null;
        foreach (Subscription subscription in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await subscription.InvokeAsync(eventData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is { Count: > 0 })
        {
            throw new AggregateException(errors);
        }
    }

    /// <summary>
    /// Releases all subscriptions. Called automatically by the DI container when the
    /// owning scope ends (or when the application shuts down, for a singleton
    /// registration); rarely needs to be called by user code.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _subscriptions.Clear();
    }

    /// <summary>
    /// Adds a subscription, throwing <see cref="ObjectDisposedException"/>
    /// if the bus was disposed concurrently. The disposed check is performed
    /// after insertion so that a concurrent <see cref="IDisposable.Dispose"/>
    /// that cleared the dictionary between the check and the insert cannot
    /// silently leak a subscription.
    /// </summary>
    private void AddSubscription(Type eventType, Subscription subscription)
    {
        ThrowIfDisposed();

        _subscriptions.AddOrUpdate(
            eventType,
            static (_, state) => ImmutableList.Create(state),
            static (_, existing, state) => existing.Add(state),
            subscription);

        // If Dispose() ran between ThrowIfDisposed and AddOrUpdate, the
        // subscription was re-added to an already-cleared dictionary.
        // Detect that and roll back.
        if (_disposed != 1)
        {
            return;
        }

        _subscriptions.Clear();
        ThrowIfDisposed();
    }

    private void RemoveSubscription(Type eventType, Subscription subscription)
    {
        // If the bus is disposed, the dictionary is already empty — don't
        // resurrect a key via AddOrUpdate. Use a TryUpdate CAS loop instead.
        while (_subscriptions.TryGetValue(eventType, out ImmutableList<Subscription>? current))
        {
            ImmutableList<Subscription> updated = current.Remove(subscription);
            if (ReferenceEquals(current, updated))
            {
                return; // Subscription already removed
            }

            // Remove the key entirely when the last subscriber unsubscribes,
            // so empty lists don't accumulate over the circuit lifetime.
            if (updated.IsEmpty)
            {
                // TryRemove only if nobody else mutated the list since our read.
                if (((ICollection<KeyValuePair<Type, ImmutableList<Subscription>>>)_subscriptions)
                        .Remove(new KeyValuePair<Type, ImmutableList<Subscription>>(eventType, current)))
                {
                    return;
                }
            }
            else if (_subscriptions.TryUpdate(eventType, updated, current))
            {
                return;
            }

            // CAS failed — another thread mutated first; retry.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
    }

    /// <summary>
    /// Base class for subscription tokens. Disposing a subscription removes
    /// it from the owning <see cref="EventBus"/>.
    /// </summary>
    private abstract class Subscription(EventBus bus) : IDisposable
    {
        private int _disposed;

        protected abstract Type EventType { get; }

        public abstract Task InvokeAsync(object @event, CancellationToken cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            bus.RemoveSubscription(EventType, this);
        }
    }

    private sealed class SyncSubscription<TEvent>(EventBus bus, Action<TEvent> handler)
        : Subscription(bus) where TEvent : notnull
    {
        protected override Type EventType => typeof(TEvent);

        public override Task InvokeAsync(object @event, CancellationToken cancellationToken)
        {
            handler((TEvent)@event);
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncSubscription<TEvent>(EventBus bus, Func<TEvent, CancellationToken, Task> handler)
        : Subscription(bus) where TEvent : notnull
    {
        protected override Type EventType => typeof(TEvent);

        public override Task InvokeAsync(object @event, CancellationToken cancellationToken)
            => handler((TEvent)@event, cancellationToken);
    }
}