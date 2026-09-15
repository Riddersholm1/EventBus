using System.Runtime.CompilerServices;

namespace EventBus.Tests;

public sealed record CounterIncremented(int NewValue);
public sealed record UserLoggedIn(string UserId);

/// <summary>Base/derived pair used to pin down type-exact routing.</summary>
public record BaseEvent(string Name);
public sealed record DerivedEvent(string Name) : BaseEvent(Name);

/// <summary>Value-type event: <c>TEvent : notnull</c> permits structs.</summary>
public readonly record struct TickEvent(long Ticks);

/// <summary>
/// xUnit builds a new instance of this class for every test, so the bus and
/// token source below are per-test state, not shared fixtures. Holding them as
/// fields (rather than <c>using</c> locals) also keeps handler lambdas from
/// capturing a local that is disposed later in the same method.
/// </summary>
public class EventBusTests : IDisposable
{
    private readonly EventBus _bus = new();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _bus.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Subscribe_SyncHandler_ReceivesPublishedEvent()
    {
        CounterIncremented? received = null;

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(e => received = e);
        await _bus.PublishAsync(new CounterIncremented(42), TestContext.Current.CancellationToken);

        Assert.NotNull(received);
        Assert.Equal(42, received.NewValue);
    }

    [Fact]
    public async Task PublishAsync_InvokesHandlersInSubscriptionOrder()
    {
        var order = new List<int>();

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => order.Add(1));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            order.Add(2);
        });
        using IDisposable s3 = _bus.Subscribe<CounterIncremented>(_ => order.Add(3));

        await _bus.PublishAsync(new CounterIncremented(0), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsReceivingEvents()
    {
        var count = 0;

        IDisposable subscription = _bus.Subscribe<CounterIncremented>(_ => count++);
        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        subscription.Dispose();
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Dispose_Bus_SubsequentSubscribeThrows()
    {
        var bus = new EventBus();
        bus.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>(_ => { }));
        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>(_ => Task.CompletedTask));
        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>((_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Dispose_Bus_SubsequentPublishAsyncThrows()
    {
        var bus = new EventBus();
        bus.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dispose_Bus_IsIdempotent()
    {
        var bus = new EventBus();
        bus.Dispose();

        bus.Dispose(); // must not throw
    }

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {

        // Each overload is named explicitly: with three delegate shapes in play,
        // a bare `null` literal is an ambiguous call.
        Assert.Throws<ArgumentNullException>(() =>
            _bus.Subscribe((Action<CounterIncremented>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            _bus.Subscribe((Func<CounterIncremented, Task>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            _bus.Subscribe((Func<CounterIncremented, CancellationToken, Task>)null!));
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _bus.PublishAsync<CounterIncremented>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishAsync_NoSubscribers_DoesNothing()
    {

        Exception? exception = await Record.ExceptionAsync(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PublishAsync_SyncHandlerThrows_AggregatesAllExceptionsAndRunsAllHandlers()
    {
        var later = false;

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("a"));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(_ => throw new ArgumentException("b"));
        using IDisposable s3 = _bus.Subscribe<CounterIncremented>(_ => later = true);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
        Assert.Contains(ex.InnerExceptions, e => e is ArgumentException);
        Assert.True(later, "all handlers must run even if earlier ones throw");
    }

    [Fact]
    public async Task PublishAsync_MixedHandlersThrow_AggregatesAllExceptions()
    {

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("a"));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            throw new ArgumentException("b");
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
        Assert.Contains(ex.InnerExceptions, e => e is ArgumentException);
    }

    [Fact]
    public async Task PublishAsync_HandlerThrows_SubscriptionStaysActive()
    {
        var calls = 0;

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(_ =>
        {
            calls++;
            throw new InvalidOperationException("always");
        });

        await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PublishAsync_CancelledToken_Throws()
    {
        var handlerCalled = false;

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(_ => handlerCalled = true);
        await _cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _bus.PublishAsync(new CounterIncremented(1), _cts.Token));
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task PublishAsync_FlowsCancellationTokenToHandlers()
    {
        CancellationToken received = CancellationToken.None;

        using IDisposable _ = _bus.Subscribe<CounterIncremented>((_, ct) =>
        {
            received = ct;
            return Task.CompletedTask;
        });

        await _bus.PublishAsync(new CounterIncremented(1), _cts.Token);

        Assert.Equal(_cts.Token, received);
    }

    [Fact]
    public async Task PublishAsync_Cancellation_MidFlight_StopsSubsequentHandlers()
    {
        var handlerOrder = new List<string>();

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            handlerOrder.Add("first-start");
            await _cts.CancelAsync();
            handlerOrder.Add("first-end");
        });
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>((_, _) =>
        {
            handlerOrder.Add("second");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _bus.PublishAsync(new CounterIncremented(1), _cts.Token));

        Assert.Contains("first-start", handlerOrder);
        Assert.Contains("first-end", handlerOrder);
        Assert.DoesNotContain("second", handlerOrder);
    }

    [Fact]
    public async Task PublishAsync_HandlerThrows_ThenTokenCancelledBetweenHandlers_PreservesHandlerErrors()
    {
        var thirdRan = false;

        // Cancellation is detected by the loop's pre-flight check between handlers,
        // rather than surfacing out of a handler (see the companion test below).
        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("first"));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(_ => _cts.Cancel());
        using IDisposable s3 = _bus.Subscribe<CounterIncremented>(_ => thirdRan = true);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => _bus.PublishAsync(new CounterIncremented(1), _cts.Token));

        // Cancellation must not silently swallow failures that already happened.
        var inner = Assert.IsType<AggregateException>(ex.InnerException);
        Exception single = Assert.Single(inner.InnerExceptions);
        Assert.IsType<InvalidOperationException>(single);
        Assert.Equal("first", single.Message);
        Assert.False(thirdRan, "cancellation must still stop later handlers");
    }

    [Fact]
    public async Task PublishAsync_HandlerThrows_ThenHandlerObservesCancellation_PreservesBoth()
    {

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("first"));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(async (_, ct) =>
        {
            // Cancel and observe it from inside the handler, so the OperationCanceledException
            // comes out of the handler rather than the loop's own pre-flight check.
            await _cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => _bus.PublishAsync(new CounterIncremented(1), _cts.Token));

        var inner = Assert.IsType<AggregateException>(ex.InnerException);
        Exception single = Assert.Single(inner.InnerExceptions);
        Assert.Equal("first", single.Message);
    }

    [Fact]
    public async Task PublishAsync_HandlerObservesCancellation_PropagatesHandlerException()
    {

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(async (_, ct) =>
        {
            await _cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });

        // Nothing failed beforehand, so the handler's own exception surfaces as-is.
        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _bus.PublishAsync(new CounterIncremented(1), _cts.Token));
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task DifferentEventTypes_Are_Independent()
    {
        var counterHits = 0;
        var loginHits = 0;

        string? loginUser = null;
        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => counterHits++);
        using IDisposable s2 = _bus.Subscribe<UserLoggedIn>(e => { loginHits++; loginUser = e.UserId; });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await _bus.PublishAsync(new UserLoggedIn("u"), TestContext.Current.CancellationToken);
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, counterHits);
        Assert.Equal(1, loginHits);
        Assert.Equal("u", loginUser);
    }

    [Fact]
    public async Task MultipleSubscriptions_To_Same_Event_All_Fire()
    {
        var a = 0;
        var b = 0;

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => a++);
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(_ => b++);

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task Unsubscribing_One_Leaves_Others_Active()
    {
        var a = 0;
        var b = 0;

        IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => a++);
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(_ => b++);

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        s1.Dispose();
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task SyncHandler_Can_Unsubscribe_Itself_During_Publish()
    {
        var count = 0;
        var self = new StrongBox<IDisposable>();

        self.Value = _bus.Subscribe<CounterIncremented>(_ =>
        {
            count++;
            self.Value!.Dispose();
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken); // should not fire

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Handler_Can_Subscribe_New_Handler_During_Publish()
    {
        var hits = new List<string>();
        var inner = new List<IDisposable>();

        using IDisposable outer = _bus.Subscribe<CounterIncremented>(_ =>
        {
            hits.Add("outer");
            if (inner.Count == 0)
            {
                inner.Add(_bus.Subscribe<CounterIncremented>(_ => hits.Add("inner")));
            }
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        // Snapshot isolation: inner handler was added mid-publish, should not fire yet.
        Assert.Equal(["outer"], hits);

        hits.Clear();
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);
        Assert.Contains("outer", hits);
        Assert.Contains("inner", hits);

        foreach (IDisposable subscription in inner)
        {
            subscription.Dispose();
        }
    }

    [Fact]
    public async Task PublishAsync_HandlerUnsubscribedMidPublish_StillRunsFromSnapshot()
    {
        var secondRan = false;
        var second = new StrongBox<IDisposable>();

        using IDisposable first = _bus.Subscribe<CounterIncremented>(_ => second.Value!.Dispose());
        second.Value = _bus.Subscribe<CounterIncremented>(_ => secondRan = true);

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        Assert.True(secondRan, "the snapshot taken at publish time still includes it");

        secondRan = false;
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);
        Assert.False(secondRan, "removal takes effect from the next publish");
    }

    [Fact]
    public async Task PublishAsync_AsyncHandler_Can_Unsubscribe_Itself()
    {
        var count = 0;
        var self = new StrongBox<IDisposable>();

        self.Value = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            count++;
            self.Value!.Dispose();
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    // ---- Func<TEvent, Task> overload ------------------------------------

    [Fact]
    public async Task Subscribe_AsyncLambdaWithoutToken_IsAwaited_NotFireAndForget()
    {
        var completed = false;

        // A one-parameter async lambda must bind to Subscribe(Func<TEvent, Task>).
        // If it bound to the Action<TEvent> overload it would be `async void`:
        // PublishAsync would return before the delay elapsed and `completed`
        // would still be false here.
        using IDisposable _ = _bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            completed = true;
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(completed);
    }

    [Fact]
    public async Task Subscribe_TaskReturningMethodGroup_IsAwaited()
    {
        var completed = false;

        async Task Handle(CounterIncremented e)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            completed = true;
        }

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(Handle);
        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(completed);
    }

    [Fact]
    public async Task PublishAsync_InvokesAllThreeHandlerKindsInOrder()
    {
        var order = new List<string>();

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(_ => order.Add("sync"));
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Yield();
            order.Add("task");
        });
        using IDisposable s3 = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            order.Add("task+ct");
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(["sync", "task", "task+ct"], order);
    }

    [Fact]
    public async Task Subscribe_TaskOverload_ExceptionsAreAggregated()
    {

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
    }

    [Fact]
    public async Task Subscribe_TaskOverload_DisposeStopsReceivingEvents()
    {
        var count = 0;

        IDisposable subscription = _bus.Subscribe<CounterIncremented>(_ =>
        {
            count++;
            return Task.CompletedTask;
        });

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        subscription.Dispose();
        await _bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_AsyncHandlerReturnsNullTask_ThrowsDescriptiveError(bool withToken)
    {

        using IDisposable _ = withToken
            ? _bus.Subscribe<CounterIncremented>((_, _) => null!)
            : _bus.Subscribe<CounterIncremented>(_ => null!);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        var inner = Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
        Assert.Contains("null Task", inner.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CounterIncremented), inner.Message, StringComparison.Ordinal);
    }

    // ---- Routing semantics ----------------------------------------------

    [Fact]
    public async Task PublishAsync_RoutesOnStaticType_NotRuntimeType()
    {
        var baseHits = 0;
        var derivedHits = 0;

        string? baseName = null;
        using IDisposable s1 = _bus.Subscribe<BaseEvent>(e => { baseHits++; baseName = e.Name; });
        using IDisposable s2 = _bus.Subscribe<DerivedEvent>(_ => derivedHits++);

        // Static type is BaseEvent even though the instance is a DerivedEvent.
        BaseEvent asBase = new DerivedEvent("x");
        await _bus.PublishAsync(asBase, TestContext.Current.CancellationToken);

        Assert.Equal(1, baseHits);
        Assert.Equal(0, derivedHits);
        Assert.Equal("x", baseName);

        await _bus.PublishAsync(new DerivedEvent("x"), TestContext.Current.CancellationToken);

        Assert.Equal(1, baseHits);
        Assert.Equal(1, derivedHits);
    }

    [Fact]
    public async Task PublishAsync_ValueTypeEvent_IsDelivered()
    {
        TickEvent received = default;

        using IDisposable _ = _bus.Subscribe<TickEvent>(e => received = e);
        await _bus.PublishAsync(new TickEvent(99), TestContext.Current.CancellationToken);

        Assert.Equal(99, received.Ticks);
    }

    [Fact]
    public async Task Subscribe_SameHandlerTwice_InvokedTwice()
    {
        var count = 0;
        void Handler(CounterIncremented e) => count++;

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(Handler);
        using IDisposable s2 = _bus.Subscribe<CounterIncremented>(Handler);

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PublishAsync_ReentrantPublishFromHandler_IsDelivered()
    {
        var loginHits = 0;

        using IDisposable s1 = _bus.Subscribe<CounterIncremented>(async (_, ct) =>
            await _bus.PublishAsync(new UserLoggedIn("u"), ct));
        using IDisposable s2 = _bus.Subscribe<UserLoggedIn>(_ => loginHits++);

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, loginHits);
    }

    [Fact]
    public async Task PublishAsync_AllSyncHandlers_CompletesSynchronously()
    {
        using IDisposable _ = _bus.Subscribe<CounterIncremented>(_ => { });

        Task publish = _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(publish.IsCompletedSuccessfully, "no thread hop when every handler is synchronous");
        await publish;
    }

    // ---- Subscription token disposal -------------------------------------

    [Fact]
    public async Task Subscription_Dispose_IsIdempotent()
    {
        var count = 0;
        var other = 0;

        IDisposable subscription = _bus.Subscribe<CounterIncremented>(_ => count++);
        using IDisposable keep = _bus.Subscribe<CounterIncremented>(_ => other++);

        subscription.Dispose();
        subscription.Dispose(); // must not throw, must not remove `keep`

        await _bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, count);
        Assert.Equal(1, other);
    }

    [Fact]
    public void Subscription_Dispose_AfterBusDisposed_DoesNotThrow()
    {
        var bus = new EventBus();
        IDisposable subscription = bus.Subscribe<CounterIncremented>(_ => { });

        bus.Dispose();

        subscription.Dispose(); // must not throw
    }

    // ---- Concurrency ------------------------------------------------------

    [Fact]
    public async Task Concurrent_SubscribeAndPublishAsync_DoesNotCrashOrLoseSubscribers()
    {
        var hits = 0;

        // Pre-existing subscriber. Subscribers added mid-flight may or may not
        // observe a concurrent publish — the guarantee is only "no crash, no
        // corruption". We assert the pre-existing subscriber sees all events.
        using IDisposable baseline = _bus.Subscribe<CounterIncremented>(_ => Interlocked.Increment(ref hits));

        const int publishers = 4;
        const int subscribers = 4;
        const int iterations = 500;

        var tasks = new List<Task>(publishers + subscribers);
        for (var p = 0; p < publishers; p++)
        {
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    await _bus.PublishAsync(new CounterIncremented(i), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken));
        }

        for (var s = 0; s < subscribers; s++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    using IDisposable sub = _bus.Subscribe<CounterIncremented>(_ => { });
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(publishers * iterations, hits);
    }

    [Fact]
    public async Task Concurrent_PublishAsync_DoesNotCorrupt()
    {
        var hits = 0;

        using IDisposable _ = _bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            Interlocked.Increment(ref hits);
        });

        const int tasks = 8;
        const int iterations = 200;

        await Task.WhenAll(
            Enumerable.Range(0, tasks).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    await _bus.PublishAsync(new CounterIncremented(i), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken)));

        Assert.Equal(tasks * iterations, hits);
    }

    [Fact]
    public async Task Concurrent_DisposeBus_DuringPublishAndSubscribe_IsClean()
    {
        // This test exists to race Dispose against Subscribe, so the captured bus
        // really can be disposed while these closures run — that is the scenario
        // under test, not an oversight.
        var bus = new EventBus();
        using IDisposable baseline = bus.Subscribe<CounterIncremented>(_ => { });

        const int workers = 4;
        const int iterations = 400;

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                // Racing a Dispose may legitimately produce ObjectDisposedException;
                // anything else — or a hang — is a defect.
                try
                {
                    // ReSharper disable once AccessToDisposedClosure -- deliberate; see above.
                    bus.Subscribe<CounterIncremented>(_ => { }).Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        await Task.Delay(5, TestContext.Current.CancellationToken);
        bus.Dispose();

        await Task.WhenAll(tasks);

        bus.Dispose(); // still idempotent after the race
        Assert.Throws<ObjectDisposedException>(() => bus.Subscribe<CounterIncremented>(_ => { }));
    }
}

/// <summary>
/// Exercises the <see cref="IEventBus.Subscribe{TEvent}(Func{TEvent, Task})"/>
/// default interface implementation. <see cref="EventBus"/> overrides it, so
/// these are the only tests that run the interface's own body — the code path
/// every third-party <see cref="IEventBus"/> implementation inherits.
/// </summary>
public class DefaultInterfaceImplementationTests
{
    /// <summary>
    /// An implementation of exactly the shape 1.0.0 required: it declares only
    /// the members that existed then. That this still compiles is the
    /// back-compatibility guarantee; the <see cref="Func{T, TResult}"/> overload
    /// is supplied entirely by the interface's default implementation.
    /// </summary>
    private sealed class LegacyBus : IEventBus, IDisposable
    {
        private readonly EventBus _inner = new();

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
            => _inner.Subscribe(handler);

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : notnull
            => _inner.Subscribe(handler);

        public Task PublishAsync<TEvent>(TEvent eventData, CancellationToken cancellationToken = default) where TEvent : notnull
            => _inner.PublishAsync(eventData, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task DefaultImplementation_DeliversAndAwaitsHandler()
    {
        using var legacy = new LegacyBus();
        IEventBus bus = legacy;
        var completed = false;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            completed = true;
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(completed, "the forwarded handler must still be awaited");
    }

    [Fact]
    public void DefaultImplementation_NullHandler_Throws()
    {
        using var legacy = new LegacyBus();
        IEventBus bus = legacy;

        // Guarded by the default implementation itself, not by EventBus.
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe((Func<CounterIncremented, Task>)null!));
    }

    [Fact]
    public async Task DefaultImplementation_TokenUnsubscribes()
    {
        using var legacy = new LegacyBus();
        IEventBus bus = legacy;
        var count = 0;

        IDisposable subscription = bus.Subscribe<CounterIncremented>(_ =>
        {
            count++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        subscription.Dispose();
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }
}
