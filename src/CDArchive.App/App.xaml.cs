using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CDArchive.App.Services;
using CDArchive.App.ViewModels;
using CDArchive.Core;
using CDArchive.Core.Services;
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

            // Capture the UI dispatcher's SynchronizationContext now, on the
            // thread that owns it. NAudioPlayerService pulls this through DI
            // (see AddCoreServices) to marshal NAudio's pool-thread events
            // back to WPF consumers. Doing the capture here — at OnStartup,
            // which the WPF runtime guarantees runs on the UI thread — keeps
            // the dependency explicit and immune to "what if a future caller
            // resolves the audio service from a background thread" (Rework C2).
            var uiSyncContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException(
                    "App.OnStartup must run on a thread with a SynchronizationContext " +
                    "(typically the WPF DispatcherSynchronizationContext). The audio " +
                    "player relies on this for cross-thread event marshalling.");
            services.AddSingleton<SynchronizationContext>(uiSyncContext);

            services.AddCoreServices();
            services.AddLogging(b =>
            {
                b.ClearProviders();
                b.AddSerilog(Log.Logger, dispose: false);
            });

            // Rework H3: VM-level abstractions over the WPF dialog surface so
            // VMs stay free of `System.Windows.MessageBox` / `Microsoft.Win32`
            // references. WPF-only by design; both impls live in
            // CDArchive.App.Services.
            services.AddSingleton<IDialogService, WpfDialogService>();
            services.AddSingleton<IFileDialogService, WpfFileDialogService>();

            services.AddSingleton<MainViewModel>();
            // Rework H45: SettingsViewModel and ImportExportViewModel were
            // registered Transient but the Singleton MainViewModel ctor-
            // injected them, freezing the first instance for life and
            // silently violating the Transient contract. Every other VM in
            // this graph is Singleton; matching these two to Singleton
            // documents the actual in-practice behavior and removes the
            // captive-dependency trap. (The pre-fix behavior was already
            // singleton-like; this is a no-op runtime change with a
            // correctness gain on the registration intent.)
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<CanonViewModel>();
            services.AddSingleton<AlbumsViewModel>();
            services.AddSingleton<TracksViewModel>();
            services.AddSingleton<ItunesImportViewModel>();
            services.AddSingleton<ImportExportViewModel>();
            services.AddSingleton<PickListsViewModel>();
            services.AddSingleton<PlayerViewModel>();

            ServiceProvider = services.BuildServiceProvider();

            Log.Information("Service provider built");

            // ArchiveSettings is constructed by DI with default values only —
            // see Rework H4. Read the persisted settings.json now, on the UI
            // thread, before MainViewModel resolves (which transitively pulls
            // every settings consumer). Failures are logged and swallowed by
            // Initialize; an unreadable settings file no longer crashes
            // startup, the user just sees the defaults until they fix it.
            ServiceProvider.GetRequiredService<IArchiveSettings>().Initialize();

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

    /// <summary>
    /// Last-resort handler for exceptions that escape every other
    /// try/catch on the UI thread. Pre-fix this only logged + flushed,
    /// leaving <c>e.Handled = false</c>, so WPF terminated the process
    /// without showing the user anything — symptom: "the app just closed".
    /// Now: show a MessageBox with the exception details + the log-file
    /// path, then mark Handled so the app stays alive when it safely can.
    /// Truly fatal failures (stack overflow, out-of-memory) won't reach
    /// this handler at all, so always-Handled is the right default.
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled dispatcher exception");

        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CDArchive", "logs");

            MessageBox.Show(
                $"An unexpected error occurred:\n\n" +
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                $"The application will try to continue. Full details (including stack trace) " +
                $"were written to today's log file under:\n{logDir}\n\n" +
                $"Stack trace:\n{e.Exception}",
                "Unhandled error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
        catch
        {
            // MessageBox.Show itself failed (extremely rare — shell down,
            // session ending, etc). Fall back to the original behaviour:
            // flush logs and let WPF terminate.
            Log.CloseAndFlush();
        }
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
