using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EliteSIP.App.Admin;
using EliteSIP.App.History;
using EliteSIP.App.Settings;

namespace EliteSIP.Tools.TrayProbe;

internal static partial class Program
{
    private static void ReopenTest()
    {
        AppBuilder.Configure<EliteSIP.App.App>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var app = (EliteSIP.App.App)Application.Current!;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                foreach (var (fieldName, methodName, window) in new (string, string, Window)[]
                {
                    ("_historyWindow", "ShowHistory", new CallHistoryWindow()),
                    ("_settingsWindow", "ShowSettings", new SettingsWindow()),
                    ("_administrationWindow", "ShowAdministration", new AdministrationWindow()),
                })
                {
                    var field = typeof(EliteSIP.App.App).GetField(fieldName, flags)!;
                    try
                    {
                        window.Show();
                        await Task.Delay(150);
                        field.SetValue(app, window);
                        window.WindowState = WindowState.Minimized;
                        await Task.Delay(250);
                        if (window.WindowState != WindowState.Minimized) { throw new InvalidOperationException(methodName + ": did not minimize"); }
                        typeof(EliteSIP.App.App).GetMethod(methodName, flags)!.Invoke(app, null);
                        await Task.Delay(750);
                        Console.WriteLine($"{methodName}: state={window.WindowState}, visible={window.IsVisible}, active={window.IsActive}");
                        if (window.WindowState != WindowState.Normal || !window.IsActive || !ReferenceEquals(window, field.GetValue(app)))
                        { throw new InvalidOperationException(methodName + ": did not restore and activate the same window"); }
                        Console.WriteLine(methodName + ": minimized window restored and active on X11");
                    }
                    finally { window.Close(); field.SetValue(app, null); }
                }
            }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}
