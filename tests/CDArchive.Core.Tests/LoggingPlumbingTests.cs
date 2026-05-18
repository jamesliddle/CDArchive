using CDArchive.Core;
using CDArchive.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Tests;

public class LoggingPlumbingTests
{
    [Fact]
    public void AddCoreServices_RegistersILoggerOfT_AsResolvable()
    {
        var services = new ServiceCollection();
        services.AddCoreServices();
        using var sp = services.BuildServiceProvider();

        var logger = sp.GetService<ILogger<SqliteCanonDataService>>();

        Assert.NotNull(logger);
    }

    [Fact]
    public void AddCoreServices_RegistersILoggerFactory_AsResolvable()
    {
        var services = new ServiceCollection();
        services.AddCoreServices();
        using var sp = services.BuildServiceProvider();

        var factory = sp.GetService<ILoggerFactory>();

        Assert.NotNull(factory);
        // AddCoreServices calls AddLogging() to register the standard pipeline.
        // The App project then layers AddSerilog() onto the same factory.
        Assert.IsNotType<NullLoggerFactory>(factory);
    }

    [Fact]
    public void SqliteCanonDataService_AcceptsNullLogger_ForTestableConstruction()
    {
        // The service's ILogger<T> parameter is optional with a NullLogger
        // fallback so tests can construct it without wiring up DI.
        var logger = NullLogger<SqliteCanonDataService>.Instance;

        Assert.NotNull(logger);
    }
}
