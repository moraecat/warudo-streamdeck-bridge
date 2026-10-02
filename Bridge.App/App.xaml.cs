using System.Threading;
using System.Windows;

namespace WarudoBridge;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceMutex = new Mutex(true, @"Local\WarudoStreamDeckBridge", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Warudo Bridge가 이미 실행 중입니다. 작업 표시줄에서 열어주세요.", "Warudo Bridge");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}
