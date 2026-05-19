using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CDArchive.App.ViewModels;
using CDArchive.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CDArchive.App;

public partial class App : Application
{
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureSerilog();

        // Surface unhandled exceptions from both the dispatcher and pool threads
        // through Serilog before we crash, so the file sink captures them.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        try
        {
            Log.Information("Application starting");

            var services = new ServiceCollection();

            services.AddCoreServices();
            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(Log.Logger, dispose: false);
            });

            services.AddSingleton<MainViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddSingleton<CanonViewModel>();
            services.AddSingleton<AlbumsViewModel>();
            services.AddSingleton<TracksViewModel>();
            services.AddSingleton<ItunesImportViewModel>();
            services.AddTransient<ImportExportViewModel>();
            services.AddSingleton<PickListsViewModel>();
            services.AddSingleton<PlayerViewModel>();

            ServiceProvider = services.BuildServiceProvider();

            Log.Information("Service provider built");

            var mainWindow = new MainWindow
            {
                DataContext = ServiceProvider.GetRequiredService<MainViewModel>()
            };
            mainWindow.Show();

            Log.Information("Main window shown");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");

            var errorPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(App).Assembly.Location)!, "startup_error.txt");
            System.IO.File.WriteAllText(errorPath, ex.ToString());
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);

        try
        {
            ServiceProvider?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ServiceProvider disposal threw");
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureSerilog()
    {
        var logDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CDArchive",
            "logs");
        Directory.CreateDirectory(logDir);

        var logPath = System.IO.Path.Combine(logDir, "cdarchive-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: OutputTemplate)
            .WriteTo.Debug(outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled dispatcher exception");
        Log.CloseAndFlush();
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "Unhandled AppDomain exception (terminating={IsTerminating})", e.IsTerminating);
        else
            Log.Fatal("Unhandled non-Exception AppDomain object (terminating={IsTerminating})", e.IsTerminating);

        Log.CloseAndFlush();
    }
}
