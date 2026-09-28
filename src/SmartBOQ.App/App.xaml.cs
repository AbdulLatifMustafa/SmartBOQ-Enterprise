using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using SmartBOQ.Infrastructure.Logging;

namespace SmartBOQ.App;

/// <summary>
/// Application startup logic with automatic global crash interception and daily error logging.
/// Configures European/English digits globally for all numbers across the application.
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly FileAppLogger _logger = FileAppLogger.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. Enforce European/English digits (0, 1, 2, 3...) globally across all threads
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("ar-EG").Clone();
        culture.NumberFormat.DigitSubstitution = DigitShapes.None;
        culture.NumberFormat.NativeDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];
        culture.NumberFormat.NegativeSign = "-";
        culture.NumberFormat.PositiveSign = "+";
        culture.NumberFormat.NumberDecimalSeparator = ".";
        culture.NumberFormat.NumberGroupSeparator = ",";
        culture.NumberFormat.CurrencyDecimalSeparator = ".";
        culture.NumberFormat.CurrencyGroupSeparator = ",";
        culture.NumberFormat.PercentDecimalSeparator = ".";
        culture.NumberFormat.PercentGroupSeparator = ",";
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // 2. Override FrameworkElement language to en-US so WPF number formatting produces European digits
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("en-US")));

        // 3. Install global unhandled exception and crash interceptors
        FileAppLogger.RegisterGlobalCrashHandler(_logger);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _logger.LogInfo("SmartBOQ Enterprise application started successfully.");

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.LogCrash(e.Exception, "WPF Dispatcher UI Thread");

        string logPath = _logger.LogDirectoryPath;
        string message = $"حدث خطأ غير متوقع أثناء تشغيل البرنامج.\n\nتم حفظ تفاصيل الخطأ في السجل اليومي داخل المجلد:\n{logPath}\n\nالرسالة: {e.Exception.Message}";
        
        MessageBox.Show(message, "SmartBOQ - تنبيه خطأ", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }
}
