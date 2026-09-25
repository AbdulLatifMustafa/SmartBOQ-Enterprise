using System.Windows;
using System.Windows.Threading;
using SmartBOQ.Infrastructure.Logging;

namespace SmartBOQ.App;

/// <summary>
/// Application startup logic with automatic global crash interception and daily error logging.
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly FileAppLogger _logger = FileAppLogger.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. Install global unhandled exception and crash interceptors
        FileAppLogger.RegisterGlobalCrashHandler(_logger);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _logger.LogInfo("SmartBOQ Enterprise application started successfully.");

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 2. Log full crash details to the daily text file in the application execution directory
        _logger.LogCrash(e.Exception, "WPF Dispatcher UI Thread");

        // 3. User-friendly notification informing the user where the error log is saved
        string logPath = _logger.LogDirectoryPath;
        string message = $"حدث خطأ غير متوقع أثناء تشغيل البرنامج.\n\nتم حفظ تفاصيل الخطأ في السجل اليومي داخل المجلد:\n{logPath}\n\nالرسالة: {e.Exception.Message}";
        
        MessageBox.Show(message, "SmartBOQ - تنبيه خطأ", MessageBoxButton.OK, MessageBoxImage.Error);

        // Mark as handled to prevent abrupt process termination when possible
        e.Handled = true;
    }
}
