namespace SmartBOQ.Infrastructure.Logging;

/// <summary>
/// Thread-safe daily rolling file logger and global crash handler.
/// Inherits from <see cref="BaseAppLogger"/> and automatically persists errors and fatal crashes
/// to daily .txt files in the application execution directory.
/// </summary>
public sealed class FileAppLogger : BaseAppLogger
{
    private static readonly Lock FileLock = new();
    private static FileAppLogger? _defaultInstance;

    /// <summary>
    /// Thread-safe singleton instance writing to the application directory's /logs folder.
    /// </summary>
    public static FileAppLogger Default => _defaultInstance ??= new FileAppLogger();

    public FileAppLogger(string? customDirectory = null) : base(customDirectory)
    {
    }

    /// <summary>
    /// Appends the formatted log entry directly to the daily log text file.
    /// </summary>
    protected override void WriteEntry(string entry)
    {
        try
        {
            EnsureDirectoryExists();
            string filePath = GetDailyLogFilePath();

            lock (FileLock)
            {
                File.AppendAllText(filePath, entry);
            }
        }
        catch
        {
            // Fail-safe: prevent logging failure from causing secondary application crashes
        }
    }

    /// <summary>
    /// Installs application-wide global unhandled exception and crash hooks.
    /// If an unexpected crash occurs anywhere in the process, it will be automatically
    /// captured and written to the daily crash log file.
    /// </summary>
    public static void RegisterGlobalCrashHandler(FileAppLogger? logger = null)
    {
        var targetLogger = logger ?? Default;

        // 1. Process-wide unhandled domain exception
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                targetLogger.LogCrash(ex, "AppDomain.CurrentDomain.UnhandledException");
            }
            else
            {
                targetLogger.LogError($"Non-exception crash object: {args.ExceptionObject}");
            }
        };

        // 2. Unobserved Task async exceptions
        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            targetLogger.LogCrash(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved(); // Prevent process termination if recoverable
        };
    }
}
