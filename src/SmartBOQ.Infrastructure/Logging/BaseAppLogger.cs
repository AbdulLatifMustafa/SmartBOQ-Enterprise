using System.Diagnostics;
using System.Text;
using SmartBOQ.Domain.Interfaces;

namespace SmartBOQ.Infrastructure.Logging;

/// <summary>
/// Abstract base class defining log formatting, daily file naming,
/// directory creation, and detailed exception inspection.
/// </summary>
public abstract class BaseAppLogger : IAppLogger
{
    private readonly string _logDirectory;

    public string LogDirectoryPath => _logDirectory;

    protected BaseAppLogger(string? customDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(customDirectory))
        {
            // Execution directory where the application/exe is running
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _logDirectory = Path.Combine(baseDir, "logs");
        }
        else
        {
            _logDirectory = customDirectory;
        }

        EnsureDirectoryExists();
    }

    protected void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
    }

    /// <summary>
    /// Generates daily log file path: logs/error_YYYY-MM-DD.txt
    /// </summary>
    protected string GetDailyLogFilePath()
    {
        string fileName = $"error_{DateTime.Now:yyyy-MM-dd}.txt";
        return Path.Combine(_logDirectory, fileName);
    }

    public virtual void LogInfo(string message)
    {
        string entry = FormatEntry("INFO", message, null);
        WriteEntry(entry);
    }

    public virtual void LogWarning(string message)
    {
        string entry = FormatEntry("WARN", message, null);
        WriteEntry(entry);
    }

    public virtual void LogError(string message, Exception? ex = null)
    {
        string entry = FormatEntry("ERROR", message, ex);
        WriteEntry(entry);
    }

    public virtual void LogCrash(Exception ex, string context = "")
    {
        string header = string.IsNullOrWhiteSpace(context) 
            ? "FATAL UNHANDLED APPLICATION CRASH" 
            : $"FATAL UNHANDLED CRASH IN: {context}";
        
        string entry = FormatCrashReport(header, ex);
        WriteEntry(entry);
    }

    /// <summary>
    /// Concrete writer implemented by derived loggers (e.g. file, console, stream).
    /// </summary>
    protected abstract void WriteEntry(string entry);

    /// <summary>
    /// Formats a standard single-line or multi-line log entry with timestamp and thread id.
    /// </summary>
    protected static string FormatEntry(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        int threadId = Environment.CurrentManagedThreadId;

        sb.Append($"[{timestamp}] [{level,-5}] [Thread:{threadId}] {message}");

        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine($"  Exception: {ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine($"  StackTrace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                AppendInnerExceptions(sb, ex.InnerException, indentLevel: 1);
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Formats an exhaustive crash dump with complete environment telemetry and recursive stack traces.
    /// </summary>
    protected static string FormatCrashReport(string header, Exception ex)
    {
        var sb = new StringBuilder();
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        long workingSetMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);

        sb.AppendLine("================================================================================");
        sb.AppendLine($"  {header}");
        sb.AppendLine($"  Timestamp   : {timestamp}");
        sb.AppendLine($"  Machine     : {Environment.MachineName} (User: {Environment.UserName})");
        sb.AppendLine($"  OS Version  : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        sb.AppendLine($"  .NET Version: {Environment.Version}");
        sb.AppendLine($"  Memory Load : {workingSetMb} MB Working Set");
        sb.AppendLine($"  Process     : {Environment.ProcessPath}");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Top-Level Exception: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        sb.AppendLine($"TargetSite: {ex.TargetSite}");
        sb.AppendLine($"StackTrace:\n{ex.StackTrace}");

        if (ex is AggregateException aggEx)
        {
            sb.AppendLine("--- Aggregate Inner Exceptions ---");
            int idx = 1;
            foreach (var inner in aggEx.InnerExceptions)
            {
                sb.AppendLine($"  [Inner #{idx++}] {inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine($"  StackTrace:\n{inner.StackTrace}");
            }
        }
        else if (ex.InnerException != null)
        {
            AppendInnerExceptions(sb, ex.InnerException, indentLevel: 1);
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendInnerExceptions(StringBuilder sb, Exception inner, int indentLevel)
    {
        string indent = new(' ', indentLevel * 2);
        sb.AppendLine($"{indent}Caused by {inner.GetType().FullName}: {inner.Message}");
        if (!string.IsNullOrWhiteSpace(inner.StackTrace))
        {
            sb.AppendLine($"{indent}StackTrace:\n{inner.StackTrace}");
        }

        if (inner.InnerException != null && indentLevel < 10)
        {
            AppendInnerExceptions(sb, inner.InnerException, indentLevel + 1);
        }
    }
}
