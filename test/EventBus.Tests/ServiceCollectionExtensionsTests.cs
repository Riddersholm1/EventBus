using Microsoft.Extensions.DependencyInjection;

namespace EventBus.Tests;

public class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Transient)]
    public void AddEventBus_Registers_IEventBus_With_Requested_Lifetime(ServiceLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddEventBus(lifetime);

        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
        Assert.Equal(lifetime, descriptor.Lifetime);
    }

    [Fact]
    public void AddEventBus_Returns_Same_Instance_Within_Scope()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();
        var a = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var b = scope.ServiceProvider.GetRequiredService<IEventBus>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddEventBus_Returns_Different_Instance_Per_Scope()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        using ServiceProvider provider = services.BuildServiceProvider();

        IEventBus bus1;
        IEventBus bus2;
        using (IServiceScope scope1 = provider.CreateScope())
        {
            bus1 = scope1.ServiceProvider.GetRequiredService<IEventBus>();
        }
        using (IServiceScope scope2 = provider.CreateScope())
        {
            bus2 = scope2.ServiceProvider.GetRequiredService<IEventBus>();
        }

        Assert.NotSame(bus1, bus2);
    }

    [Fact]
    public void AddEventBus_Singleton_Shares_One_Instance_Across_Scopes()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Singleton);
        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope1 = provider.CreateScope();
        using IServiceScope scope2 = provider.CreateScope();

        Assert.Same(
            scope1.ServiceProvider.GetRequiredService<IEventBus>(),
            scope2.ServiceProvider.GetRequiredService<IEventBus>());
    }

    [Fact]
    public void AddEventBus_Transient_Returns_New_Instance_Per_Resolve()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Transient);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotSame(
            provider.GetRequiredService<IEventBus>(),
            provider.GetRequiredService<IEventBus>());
    }

    [Fact]
    public void AddEventBus_DoesNotExposeConcreteType()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        // Consumers should only depend on the interface.
        Assert.Null(scope.ServiceProvider.GetService<EventBus>());
    }

    [Fact]
    public void Disposing_Scope_Disposes_EventBus()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        using ServiceProvider provider = services.BuildServiceProvider();

        IEventBus bus;
        using (IServiceScope scope = provider.CreateScope())
        {
            bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
            // Subscription works inside the scope.
            using IDisposable _ = bus.Subscribe<CounterIncremented>(_ => { });
        }

        // After scope disposal the bus should refuse new work.
        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>(_ => { }));
    }

    [Fact]
    public void Disposing_Provider_Disposes_Singleton_EventBus()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Singleton);

        IEventBus bus;
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            bus = provider.GetRequiredService<IEventBus>();
        }

        Assert.Throws<ObjectDisposedException>(() =>
            bus.Subscribe<CounterIncremented>(_ => { }));
    }

    [Fact]
    public void AddEventBus_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        services.AddEventBus(ServiceLifetime.Scoped);

        Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
    }

    [Fact]
    public void AddEventBus_SecondCall_WithDifferentLifetime_KeepsTheFirst()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        services.AddEventBus(ServiceLifetime.Singleton);

        // Documented behaviour: registration is additive-once, so the later
        // (and probably intended) lifetime is silently ignored.
        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddEventBus_UndefinedLifetime_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddEventBus((ServiceLifetime)99));
    }

    [Fact]
    public void AddEventBus_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((IServiceCollection)null!).AddEventBus(ServiceLifetime.Scoped));
    }

    [Fact]
    public async Task Resolved_Bus_Delivers_Events_Within_Scope()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var received = 0;

        using IDisposable _ = bus.Subscribe<CounterIncremented>(_ => received++);
        await bus.PublishAsync(new CounterIncremented(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, received);
    }
}
