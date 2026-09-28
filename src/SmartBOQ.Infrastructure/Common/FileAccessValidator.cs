using System.IO;

namespace SmartBOQ.Infrastructure.Common;

/// <summary>
/// Proactive file access and lock validator for Excel documents.
/// Prevents unhandled sharing violation crashes by testing file stream accessibility
/// before reading or writing, providing localized and actionable error feedback.
/// </summary>
public static class FileAccessValidator
{
    /// <summary>
    /// Checks whether an existing file is currently locked exclusively by another application (e.g., Microsoft Excel).
    /// </summary>
    public static bool IsFileLocked(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            // Attempt to open the file with read/write access and no sharing to detect exclusive locks
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Asserts that a target export file is available for write operations.
    /// If locked, throws a friendly <see cref="InvalidOperationException"/> with clear instructions.
    /// </summary>
    public static void EnsureFileWritable(string filePath, string fileDescription = "ملف الإكسل")
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        if (IsFileLocked(filePath))
        {
            string fileName = Path.GetFileName(filePath);
            throw new InvalidOperationException(
                $"الملف '{fileName}' ({fileDescription}) مفتوح حالياً في تطبيق آخر (مثل Microsoft Excel).\n\n" +
                "يرجى حفظ وإغلاق الملف في برنامج الإكسل ثم إعادة المحاولة.");
        }
    }

    /// <summary>
    /// Asserts that a source input file is readable.
    /// </summary>
    public static void EnsureFileReadable(string filePath, string fileDescription = "ملف المقايسة")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"لم يتم العثور على {fileDescription} في المسار المحدد: {filePath}", filePath);
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string fileName = Path.GetFileName(filePath);
            throw new InvalidOperationException(
                $"تعذر قراءة {fileDescription} '{fileName}'. الملف مقفل أو قيد الاستخدام الحصري من قبل تطبيق آخر.\n\n" +
                $"التفاصيل: {ex.Message}");
        }
    }
}
