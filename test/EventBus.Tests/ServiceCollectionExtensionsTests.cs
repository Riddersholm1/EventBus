using Microsoft.Extensions.DependencyInjection;

namespace EventBus.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEventBus_Registers_IEventBus_As_Scoped()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);

        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
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
    public void AddEventBus_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddEventBus(ServiceLifetime.Scoped);
        services.AddEventBus(ServiceLifetime.Scoped);

        Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
    }
}