using System.Threading;
using System.Windows;
using System.Windows.Threading;
using UltraLauncher.App.ViewModels;
using UltraLauncher.Core.Application;
using UltraLauncher.Core.Common;
using UltraLauncher.Core.Logging;

namespace UltraLauncher.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    /// <summary>Ядро лаунчера (создаётся при запуске).</summary>
    public static LauncherCore? Core { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Один экземпляр на пользователя: два лаунчера одновременно писали бы в одни и те же файлы.
        _singleInstance = new Mutex(true, @"Local\UltraLauncher.SingleInstance", out var created);
        if (!created)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            NativeWindowActivator.ActivateExisting("UltraLauncher");
            Shutdown(0);
            return;
        }

        try
        {
            Core = new LauncherCore(AppPaths.Default());
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить UltraLauncher: " + ex.Message + "\n\nПроверьте права на запись в папку %LOCALAPPDATA%\\UltraLauncher.",
                "UltraLauncher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Core?.Log.Error("App", "Необработанная ошибка фоновой задачи", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Core?.Log.Error("App", "Критическая ошибка", args.ExceptionObject as Exception);

        var vm = new MainViewModel(Core);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        await vm.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Core?.Log.Error("App", "Необработанная ошибка интерфейса", e.Exception);
        if (MainWindow?.DataContext is MainViewModel vm)
        {
            vm.Notifications.ShowError(e.Exception);
            e.Handled = true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Core?.Log.Info("App", "UltraLauncher закрыт");
        Core?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
