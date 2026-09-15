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
    public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new TaskSubscription<TEvent>(this, handler);
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
            if (cancellationToken.IsCancellationRequested)
            {
                throw Cancelled(errors, cancellationToken);
            }

            try
            {
                await subscription.InvokeAsync(eventData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && errors is null)
            {
                // Nothing has failed yet, so the handler's own exception is the
                // most informative thing we can surface. Preserve its stack.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw Cancelled(errors, cancellationToken);
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
    /// <remarks>
    /// A publish already in flight keeps running against the snapshot it took, so
    /// disposing the bus does not abort handlers that have already started.
    /// Disposal is idempotent and safe to call concurrently.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _subscriptions.Clear();
    }

    /// <summary>
    /// Builds the exception thrown when <paramref name="token"/> is cancelled
    /// mid-publish. Handler failures collected before the cancellation was
    /// observed are carried along as an inner <see cref="AggregateException"/>
    /// rather than discarded, so a cancelled publish never silently loses
    /// diagnostics.
    /// </summary>
    private static OperationCanceledException Cancelled(List<Exception>? errors, CancellationToken token)
        => errors is { Count: > 0 }
            ? new OperationCanceledException(
                "The publish operation was cancelled after one or more handlers had already thrown. " +
                "See the inner AggregateException for those failures.",
                new AggregateException(errors),
                token)
            : new OperationCanceledException(token);

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
        if (Volatile.Read(ref _disposed) != 1)
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
        // Volatile so the check can't be hoisted or reused across the
        // check → insert → recheck sequence in AddSubscription on weak memory
        // models (ARM64: MAUI, Apple silicon, ARM servers).
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
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

        /// <summary>
        /// Creates the exception thrown when a <see cref="Task"/>-returning handler hands back
        /// <see langword="null"/> — almost always an unstubbed mock. Without
        /// this the caller would see an opaque <see cref="NullReferenceException"/>
        /// with no indication of which handler was at fault.
        /// </summary>
        protected static InvalidOperationException NullTask(Type eventType)
            => new($"An asynchronous handler for event type '{eventType}' returned a null Task. " +
                   "Handlers must return a non-null Task; a null return usually means the handler " +
                   "is an unconfigured test double.");

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

    private sealed class TaskSubscription<TEvent>(EventBus bus, Func<TEvent, Task> handler)
        : Subscription(bus) where TEvent : notnull
    {
        protected override Type EventType => typeof(TEvent);

        public override Task InvokeAsync(object @event, CancellationToken cancellationToken)
            => handler((TEvent)@event) ?? throw NullTask(typeof(TEvent));
    }

    private sealed class AsyncSubscription<TEvent>(EventBus bus, Func<TEvent, CancellationToken, Task> handler)
        : Subscription(bus) where TEvent : notnull
    {
        protected override Type EventType => typeof(TEvent);

        public override Task InvokeAsync(object @event, CancellationToken cancellationToken)
            => handler((TEvent)@event, cancellationToken) ?? throw NullTask(typeof(TEvent));
    }
}
