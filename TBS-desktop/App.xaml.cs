using System.Windows;

namespace TaskbarSholawat;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WhisperNative.Configure();

        if (e.Args.Contains("--selftest"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTest.RunAsync(e.Args));
            return;
        }

        new MainWindow().Show();
    }
}
