using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SmartBOQ.App.Services;
using SmartBOQ.App.ViewModels;
using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Interfaces;
using SmartBOQ.Infrastructure.Export;
using SmartBOQ.Infrastructure.Logging;
using SmartBOQ.Infrastructure.Matching;
using SmartBOQ.Infrastructure.Parsers;
using SmartBOQ.Infrastructure.Storage;
using SmartBOQ.Infrastructure.Verification;

namespace SmartBOQ.App;

/// <summary>
/// Application startup logic with automatic global crash interception and daily error logging.
/// Configures European/English digits globally for all numbers across the application.
/// Provides full Microsoft.Extensions.DependencyInjection container management.
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly FileAppLogger _logger = FileAppLogger.Default;
    public static IServiceProvider? Services { get; private set; }

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

        // 4. Build and configure Dependency Injection Container
        Services = ConfigureServices();

        _logger.LogInfo("SmartBOQ Enterprise application started successfully with Dependency Injection.");

        base.OnStartup(e);
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string dbPath = Path.Combine(exeDir, "smartboq.db");
        string oldAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartBOQ", "smartboq.db");
        if (!File.Exists(dbPath) && File.Exists(oldAppData))
        {
            try
            {
                File.Copy(oldAppData, dbPath, overwrite: false);
            }
            catch { }
        }

        // Domain & Infrastructure Service Registrations
        services.AddSingleton<IVerificationGate, PreFlightVerificationGate>();
        services.AddSingleton<IItemMatcher, SmartBOQ.Infrastructure.CognitiveBrain.Engine.CognitiveAdaptiveBrain>();
        services.AddSingleton<IBoqExporter, ClosedXmlExporter>();
        services.AddSingleton<ISqliteRepository>(sp => new SqliteBoqRepository(dbPath));
        services.AddSingleton<IBoqInspector, BoqInspectorService>();
        services.AddSingleton<ILocalizationService>(sp =>
        {
            var loc = new LocalizationService();
            loc.SetCulture("ar-EG");
            return loc;
        });

        // Application Engine Services
        services.AddSingleton(sp => new BoqReconciliationService(
            sp.GetRequiredService<IVerificationGate>(),
            new UniversalAdaptiveBoqReader(),
            new HierarchicalBoqReader(),
            sp.GetRequiredService<IItemMatcher>(),
            sp.GetRequiredService<IBoqExporter>(),
            sp.GetRequiredService<ISqliteRepository>(),
            sp.GetRequiredService<IBoqInspector>()
        ));

        // ViewModels
        services.AddSingleton(sp => new MainViewModel(
            sp.GetRequiredService<BoqReconciliationService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IBoqInspector>(),
            dbPath
        ));

        return services.BuildServiceProvider();
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
