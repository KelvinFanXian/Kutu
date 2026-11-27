using System.Windows;
using Kutu.Features.Updates;

namespace Kutu;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--kutu-apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { UpdateInstaller.Apply(e.Args[1], e.Args[2]); }
            catch (Exception ex) { MessageBox.Show($"更新未能完成：{ex.Message}", "Kutu", MessageBoxButton.OK, MessageBoxImage.Error); }
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
