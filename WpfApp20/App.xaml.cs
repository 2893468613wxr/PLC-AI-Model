using System.Windows;
using WpfApp20.Services;

namespace WpfApp20;

public partial class App : Application
{
    public static ConfigService ConfigService { get; private set; } = null!;
    public static DataLogService Logger { get; private set; } = null!;
    public static AlarmEngine AlarmEngine { get; private set; } = null!;
    public static DeviceManager Manager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigService = new ConfigService();
        Logger = new DataLogService();
        AlarmEngine = new AlarmEngine();
        Manager = new DeviceManager(ConfigService, Logger, AlarmEngine);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Manager.StopAll();
        Manager.SaveConfig();
        base.OnExit(e);
    }
}
