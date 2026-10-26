using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using NetworkTrafficController.App.Services;
using NetworkTrafficController.App.ViewModels;
using NetworkTrafficController.App.Views;
using NetworkTrafficController.Core.Logging;
using NetworkTrafficController.Core.Native;

namespace NetworkTrafficController.App;

/// <summary>アプリのエントリ。エンジンを組み立て、フェイルセーフのため未処理例外でも WinDivert ハンドルを閉じる。</summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private MonitorHost? _host;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"network-traffic-controller-app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(logPath));
        });
        var logger = _loggerFactory.CreateLogger("NetworkTrafficController.App");
        logger.LogInformation("NetworkTrafficController.App starting");

        // フェイルセーフ: プロセス終了・未処理例外で必ず全ハンドルを閉じる（CLAUDE.md 設計上の要点 5）
        HandleRegistry.HookProcessLifetime();
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 配置フォルダの検査（改ざん警告）。監視の起動前にログへ
        foreach (var warning in InstallDirectoryGuard.Check(logger).Warnings)
        {
            logger.LogWarning("InstallDirectoryGuard: {Warning}", warning);
        }

        _host = new MonitorHost(_loggerFactory);
        _viewModel = new MainViewModel(_host, _loggerFactory);

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();
        _viewModel.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggerFactory?.CreateLogger("NetworkTrafficController.App").LogError(e.Exception, "unhandled UI exception");
        // ハンドルを閉じて通信を元に戻し、UI のタイマ・エンジンも止めて状態と実態を一致させてから終了する
        HandleRegistry.CloseAll();
        _viewModel?.Dispose();
        _host?.Dispose();
        MessageBox.Show(e.Exception.Message + "\n\nNetworkTrafficController will now exit.", "NetworkTrafficController error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _host?.Dispose();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }
}
