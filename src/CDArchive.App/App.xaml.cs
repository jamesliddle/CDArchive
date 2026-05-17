using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CDArchive.App;

public partial class App : Application
{
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var services = new ServiceCollection();

            services.AddCoreServices();

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

            var mainWindow = new MainWindow
            {
                DataContext = ServiceProvider.GetRequiredService<MainViewModel>()
            };
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            var errorPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(App).Assembly.Location)!, "startup_error.txt");
            System.IO.File.WriteAllText(errorPath, ex.ToString());
            MessageBox.Show(ex.ToString(), "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
