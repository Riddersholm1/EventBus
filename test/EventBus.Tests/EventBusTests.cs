namespace EventBus.Tests;

public sealed record CounterIncremented(int NewValue);
public sealed record UserLoggedIn(string UserId);

/// <summary>Base/derived pair used to pin down type-exact routing.</summary>
public record BaseEvent(string Name);
public sealed record DerivedEvent(string Name) : BaseEvent(Name);

/// <summary>Value-type event: <c>TEvent : notnull</c> permits structs.</summary>
public readonly record struct TickEvent(long Ticks);

public class EventBusTests
{
    [Fact]
    public async Task Subscribe_SyncHandler_ReceivesPublishedEvent()
    {
        using var bus = new EventBus();
        CounterIncremented? received = null;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(e => received = e);
        await bus.PublishAsync(new CounterIncremented(42), TestContext.Current.CancellationToken);

        Assert.Equal(new CounterIncremented(42), received);
    }

    [Fact]
    public async Task PublishAsync_InvokesSyncAndAsyncHandlers()
    {
        using var bus = new EventBus();
        var hits = new List<string>();

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(e => hits.Add($"sync-{e.NewValue}"));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(async (e, _) =>
        {
            await Task.Yield();
            hits.Add($"async-{e.NewValue}");
        });

        await bus.PublishAsync(new CounterIncremented(7), TestContext.Current.CancellationToken);

        Assert.Equal(["sync-7", "async-7"], hits);
    }

    [Fact]
    public async Task PublishAsync_InvokesHandlersInSubscriptionOrder()
    {
        using var bus = new EventBus();
        var order = new List<int>();

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => order.Add(1));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            order.Add(2);
        });
        using IDisposable s3 = bus.Subscribe<CounterIncremented>(_ => order.Add(3));

        await bus.PublishAsync(new CounterIncremented(0), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], order);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsReceivingEvents()
    {
        using var bus = new EventBus();
        var count = 0;

        IDisposable subscription = bus.Subscribe<CounterIncremented>(_ => count++);
        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        subscription.Dispose();
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

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
            bus.Subscribe<CounterIncremented>((Func<CounterIncremented, Task>)(_ => Task.CompletedTask)));
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
        using var bus = new EventBus();

        // Each overload is named explicitly: with three delegate shapes in play,
        // a bare `null` literal is an ambiguous call.
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe((Action<CounterIncremented>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe((Func<CounterIncremented, Task>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe((Func<CounterIncremented, CancellationToken, Task>)null!));
    }

    [Fact]
    public async Task PublishAsync_NullEvent_Throws()
    {
        using var bus = new EventBus();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bus.PublishAsync<CounterIncremented>(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishAsync_NoSubscribers_DoesNothing()
    {
        using var bus = new EventBus();

        Exception? exception = await Record.ExceptionAsync(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PublishAsync_SyncHandlerThrows_AggregatesAllExceptionsAndRunsAllHandlers()
    {
        using var bus = new EventBus();
        var later = false;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("a"));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(_ => throw new ArgumentException("b"));
        using IDisposable s3 = bus.Subscribe<CounterIncremented>(_ => later = true);

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
        Assert.Contains(ex.InnerExceptions, e => e is ArgumentException);
        Assert.True(later, "all handlers must run even if earlier ones throw");
    }

    [Fact]
    public async Task PublishAsync_MixedHandlersThrow_AggregatesAllExceptions()
    {
        using var bus = new EventBus();

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("a"));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            throw new ArgumentException("b");
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
        Assert.Contains(ex.InnerExceptions, e => e is ArgumentException);
    }

    [Fact]
    public async Task PublishAsync_HandlerThrows_SubscriptionStaysActive()
    {
        using var bus = new EventBus();
        var calls = 0;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(_ =>
        {
            calls++;
            throw new InvalidOperationException("always");
        });

        await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PublishAsync_CancelledToken_Throws()
    {
        using var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        var handlerCalled = false;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(_ => handlerCalled = true);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.PublishAsync(new CounterIncremented(1), cts.Token));
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task PublishAsync_FlowsCancellationTokenToHandlers()
    {
        using var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        CancellationToken received = CancellationToken.None;

        using IDisposable _ = bus.Subscribe<CounterIncremented>((_, ct) =>
        {
            received = ct;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(new CounterIncremented(1), cts.Token);

        Assert.Equal(cts.Token, received);
    }

    [Fact]
    public async Task PublishAsync_Cancellation_MidFlight_StopsSubsequentHandlers()
    {
        using var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        var handlerOrder = new List<string>();

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            handlerOrder.Add("first-start");
            await cts.CancelAsync();
            handlerOrder.Add("first-end");
        });
        using IDisposable s2 = bus.Subscribe<CounterIncremented>((_, _) =>
        {
            handlerOrder.Add("second");
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.PublishAsync(new CounterIncremented(1), cts.Token));

        Assert.Contains("first-start", handlerOrder);
        Assert.Contains("first-end", handlerOrder);
        Assert.DoesNotContain("second", handlerOrder);
    }

    [Fact]
    public async Task PublishAsync_HandlerThrowsThenCancelled_PreservesHandlerErrors()
    {
        using var bus = new EventBus();
        using var cts = new CancellationTokenSource();
        var thirdRan = false;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => throw new InvalidOperationException("first"));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(_ => cts.Cancel());
        using IDisposable s3 = bus.Subscribe<CounterIncremented>(_ => thirdRan = true);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(
            () => bus.PublishAsync(new CounterIncremented(1), cts.Token));

        // Cancellation must not silently swallow failures that already happened.
        var inner = Assert.IsType<AggregateException>(ex.InnerException);
        Exception single = Assert.Single(inner.InnerExceptions);
        Assert.IsType<InvalidOperationException>(single);
        Assert.Equal("first", single.Message);
        Assert.False(thirdRan, "cancellation must still stop later handlers");
    }

    [Fact]
    public async Task PublishAsync_HandlerObservesCancellation_PropagatesHandlerException()
    {
        using var bus = new EventBus();
        using var cts = new CancellationTokenSource();

        using IDisposable _ = bus.Subscribe<CounterIncremented>(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });

        // Nothing failed beforehand, so the handler's own exception surfaces as-is.
        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.PublishAsync(new CounterIncremented(1), cts.Token));
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task DifferentEventTypes_Are_Independent()
    {
        using var bus = new EventBus();
        var counterHits = 0;
        var loginHits = 0;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => counterHits++);
        using IDisposable s2 = bus.Subscribe<UserLoggedIn>(_ => loginHits++);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await bus.PublishAsync(new UserLoggedIn("u"), TestContext.Current.CancellationToken);
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, counterHits);
        Assert.Equal(1, loginHits);
    }

    [Fact]
    public async Task MultipleSubscriptions_To_Same_Event_All_Fire()
    {
        using var bus = new EventBus();
        var a = 0;
        var b = 0;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => a++);
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(_ => b++);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task Unsubscribing_One_Leaves_Others_Active()
    {
        using var bus = new EventBus();
        var a = 0;
        var b = 0;

        IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => a++);
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(_ => b++);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        s1.Dispose();
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task SyncHandler_Can_Unsubscribe_Itself_During_Publish()
    {
        using var bus = new EventBus();
        var count = 0;
        IDisposable? self = null;

        self = bus.Subscribe<CounterIncremented>(_ =>
        {
            count++;
            self!.Dispose();
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken); // should not fire

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Handler_Can_Subscribe_New_Handler_During_Publish()
    {
        using var bus = new EventBus();
        var hits = new List<string>();
        IDisposable? inner = null;

        using IDisposable outer = bus.Subscribe<CounterIncremented>(_ =>
        {
            hits.Add("outer");
            inner ??= bus.Subscribe<CounterIncremented>(_ => hits.Add("inner"));
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        // Snapshot isolation: inner handler was added mid-publish, should not fire yet.
        Assert.Equal(["outer"], hits);

        hits.Clear();
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);
        Assert.Contains("outer", hits);
        Assert.Contains("inner", hits);

        inner?.Dispose();
    }

    [Fact]
    public async Task PublishAsync_HandlerUnsubscribedMidPublish_StillRunsFromSnapshot()
    {
        using var bus = new EventBus();
        var secondRan = false;
        IDisposable? second = null;

        using IDisposable first = bus.Subscribe<CounterIncremented>(_ => second!.Dispose());
        second = bus.Subscribe<CounterIncremented>(_ => secondRan = true);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        Assert.True(secondRan, "the snapshot taken at publish time still includes it");

        secondRan = false;
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);
        Assert.False(secondRan, "removal takes effect from the next publish");
    }

    [Fact]
    public async Task PublishAsync_AsyncHandler_Can_Unsubscribe_Itself()
    {
        using var bus = new EventBus();
        var count = 0;
        IDisposable? self = null;

        self = bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            count++;
            self!.Dispose();
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
        await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    // ---- Func<TEvent, Task> overload ------------------------------------

    [Fact]
    public async Task Subscribe_AsyncLambdaWithoutToken_IsAwaited_NotFireAndForget()
    {
        using var bus = new EventBus();
        var completed = false;

        // A one-parameter async lambda must bind to Subscribe(Func<TEvent, Task>).
        // If it bound to the Action<TEvent> overload it would be `async void`:
        // PublishAsync would return before the delay elapsed and `completed`
        // would still be false here.
        using IDisposable _ = bus.Subscribe<CounterIncremented>(async e =>
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            completed = true;
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(completed);
    }

    [Fact]
    public async Task Subscribe_TaskReturningMethodGroup_IsAwaited()
    {
        using var bus = new EventBus();
        var completed = false;

        async Task Handle(CounterIncremented e)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            completed = true;
        }

        using IDisposable _ = bus.Subscribe<CounterIncremented>(Handle);
        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(completed);
    }

    [Fact]
    public async Task PublishAsync_InvokesAllThreeHandlerKindsInOrder()
    {
        using var bus = new EventBus();
        var order = new List<string>();

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(_ => order.Add("sync"));
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Yield();
            order.Add("task");
        });
        using IDisposable s3 = bus.Subscribe<CounterIncremented>(async (_, _) =>
        {
            await Task.Yield();
            order.Add("task+ct");
        });

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(["sync", "task", "task+ct"], order);
    }

    [Fact]
    public async Task Subscribe_TaskOverload_ExceptionsAreAggregated()
    {
        using var bus = new EventBus();

        using IDisposable _ = bus.Subscribe<CounterIncremented>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
    }

    [Fact]
    public async Task Subscribe_TaskOverload_DisposeStopsReceivingEvents()
    {
        using var bus = new EventBus();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_AsyncHandlerReturnsNullTask_ThrowsDescriptiveError(bool withToken)
    {
        using var bus = new EventBus();

        using IDisposable _ = withToken
            ? bus.Subscribe<CounterIncremented>((Func<CounterIncremented, CancellationToken, Task>)((_, _) => null!))
            : bus.Subscribe<CounterIncremented>((Func<CounterIncremented, Task>)(_ => null!));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken));

        var inner = Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
        Assert.Contains("null Task", inner.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(CounterIncremented), inner.Message, StringComparison.Ordinal);
    }

    // ---- Routing semantics ----------------------------------------------

    [Fact]
    public async Task PublishAsync_RoutesOnStaticType_NotRuntimeType()
    {
        using var bus = new EventBus();
        var baseHits = 0;
        var derivedHits = 0;

        using IDisposable s1 = bus.Subscribe<BaseEvent>(_ => baseHits++);
        using IDisposable s2 = bus.Subscribe<DerivedEvent>(_ => derivedHits++);

        // Static type is BaseEvent even though the instance is a DerivedEvent.
        BaseEvent asBase = new DerivedEvent("x");
        await bus.PublishAsync(asBase, TestContext.Current.CancellationToken);

        Assert.Equal(1, baseHits);
        Assert.Equal(0, derivedHits);

        await bus.PublishAsync(new DerivedEvent("x"), TestContext.Current.CancellationToken);

        Assert.Equal(1, baseHits);
        Assert.Equal(1, derivedHits);
    }

    [Fact]
    public async Task PublishAsync_ValueTypeEvent_IsDelivered()
    {
        using var bus = new EventBus();
        TickEvent received = default;

        using IDisposable _ = bus.Subscribe<TickEvent>(e => received = e);
        await bus.PublishAsync(new TickEvent(99), TestContext.Current.CancellationToken);

        Assert.Equal(new TickEvent(99), received);
    }

    [Fact]
    public async Task Subscribe_SameHandlerTwice_InvokedTwice()
    {
        using var bus = new EventBus();
        var count = 0;
        void Handler(CounterIncremented e) => count++;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(Handler);
        using IDisposable s2 = bus.Subscribe<CounterIncremented>(Handler);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PublishAsync_ReentrantPublishFromHandler_IsDelivered()
    {
        using var bus = new EventBus();
        var loginHits = 0;

        using IDisposable s1 = bus.Subscribe<CounterIncremented>(async (_, ct) =>
            await bus.PublishAsync(new UserLoggedIn("u"), ct));
        using IDisposable s2 = bus.Subscribe<UserLoggedIn>(_ => loginHits++);

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, loginHits);
    }

    [Fact]
    public async Task PublishAsync_AllSyncHandlers_CompletesSynchronously()
    {
        using var bus = new EventBus();
        using IDisposable _ = bus.Subscribe<CounterIncremented>(_ => { });

        Task publish = bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.True(publish.IsCompletedSuccessfully, "no thread hop when every handler is synchronous");
        await publish;
    }

    // ---- Subscription token disposal -------------------------------------

    [Fact]
    public async Task Subscription_Dispose_IsIdempotent()
    {
        using var bus = new EventBus();
        var count = 0;
        var other = 0;

        IDisposable subscription = bus.Subscribe<CounterIncremented>(_ => count++);
        using IDisposable keep = bus.Subscribe<CounterIncremented>(_ => other++);

        subscription.Dispose();
        subscription.Dispose(); // must not throw, must not remove `keep`

        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

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
        using var bus = new EventBus();
        var hits = 0;

        // Pre-existing subscriber. Subscribers added mid-flight may or may not
        // observe a concurrent publish — the guarantee is only "no crash, no
        // corruption". We assert the pre-existing subscriber sees all events.
        using IDisposable baseline = bus.Subscribe<CounterIncremented>(_ => Interlocked.Increment(ref hits));

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
                    await bus.PublishAsync(new CounterIncremented(i), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken));
        }

        for (var s = 0; s < subscribers; s++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    using IDisposable sub = bus.Subscribe<CounterIncremented>(_ => { });
                }
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(publishers * iterations, hits);
    }

    [Fact]
    public async Task Concurrent_PublishAsync_DoesNotCorrupt()
    {
        using var bus = new EventBus();
        var hits = 0;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(async (_, _) =>
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
                    await bus.PublishAsync(new CounterIncremented(i), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken)));

        Assert.Equal(tasks * iterations, hits);
    }

    [Fact]
    public async Task Concurrent_DisposeBus_DuringPublishAndSubscribe_IsClean()
    {
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
