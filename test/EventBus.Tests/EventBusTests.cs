namespace EventBus.Tests;

public sealed record CounterIncremented(int NewValue);
public sealed record UserLoggedIn(string UserId);

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
    public async Task PublishAsync_WithOnlySyncHandlers_InvokesThem()
    {
        using var bus = new EventBus();
        var hits = 0;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(_ => hits++);
        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, hits);
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

        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>(_ => { }));
    }

    [Fact]
    public void Subscribe_NullHandler_Throws()
    {
        using var bus = new EventBus();

        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe((Action<CounterIncremented>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            bus.Subscribe<CounterIncremented>(null!));
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

        IDisposable outer = bus.Subscribe<CounterIncremented>(_ =>
        {
            hits.Add("outer");
            inner ??= bus.Subscribe<CounterIncremented>(_ => hits.Add("inner"));
        });

        try
        {
            await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);
            // Snapshot isolation: inner handler was added mid-publish, should not fire yet.
            Assert.Equal(["outer"], hits);

            hits.Clear();
            await bus.PublishAsync(new CounterIncremented(2), TestContext.Current.CancellationToken);
            Assert.Contains("outer", hits);
            Assert.Contains("inner", hits);
        }
        finally
        {
            outer.Dispose();
            inner?.Dispose();
        }
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
            })));

        Assert.Equal(tasks * iterations, hits);
    }
}