using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SmartBOQ.App.Services;

/// <summary>
/// Ultra-lightweight, non-blocking Excel navigator.
/// Intelligently detects if a workbook is already open in Excel to avoid launching duplicate processes
/// or triggering modal COM deadlocks. Uses native Win32 activation for 0ms response and minimum system overhead.
/// </summary>
public static class ExcelNavigator
{
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    /// <summary>
    /// Opens the specified workbook and activates the target worksheet or in-sheet table row.
    /// If the file is already open in an existing Excel instance, it instantly brings it to the front
    /// without spawning a new process or causing UI freezes.
    /// </summary>
    public static void OpenWorkbookAtSheet(string filePath, string? sheetName = null, int rowIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        // Run entirely in background to ensure 100% responsive UI
        Task.Run(() =>
        {
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                string fileName = Path.GetFileName(filePath);
                string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);

                var excelProcs = Process.GetProcessesByName("EXCEL");
                if (excelProcs.Length == 0)
                {
                    // Excel is not running at all: launch cleanly via Windows Shell
                    var psi = new ProcessStartInfo
                    {
                        FileName = fullPath,
                        UseShellExecute = true
                    };
                    Process.Start(psi);

                    if (!string.IsNullOrWhiteSpace(sheetName) || rowIndex > 0)
                    {
                        Task.Delay(1600).ContinueWith(_ =>
                        {
                            try { TryActivateInRunningExcel(fullPath, fileName, sheetName, rowIndex); }
                            catch { }
                        });
                    }
                    return;
                }

                // Fast Win32 window search to bring existing window to front (0ms delay)
                bool windowFound = TryActivateWindowByTitle(fileName, fileNameNoExt);

                // Attempt in-process COM navigation (scroll to table row or sheet)
                if (TryActivateInRunningExcel(fullPath, fileName, sheetName, rowIndex))
                {
                    return;
                }

                if (windowFound)
                {
                    return;
                }

                // File is not currently open: launch cleanly via Windows Shell
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });

                if (!string.IsNullOrWhiteSpace(sheetName) || rowIndex > 0)
                {
                    Task.Delay(1300).ContinueWith(_ =>
                    {
                        try { TryActivateInRunningExcel(fullPath, fileName, sheetName, rowIndex); }
                        catch { }
                    });
                }
            }
            catch
            {
                // Fallback to basic shell open if anything fails
                try
                {
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                }
                catch { }
            }
        });
    }

    /// <summary>
    /// Checks if the workbook is open in an existing running Excel instance and activates the worksheet or row.
    /// Never spawns a new Excel process.
    /// </summary>
    private static bool TryActivateInRunningExcel(string fullPath, string fileName, string? sheetName, int rowIndex)
    {
        try
        {
            var excelObj = GetActiveExcelApplication();
            if (excelObj == null) return false;

            dynamic excel = excelObj;
            dynamic workbooks = excel.Workbooks;
            int count = workbooks.Count;

            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic wb = workbooks[i];
                    string wbPath = wb.FullName?.ToString() ?? string.Empty;
                    string wbName = wb.Name?.ToString() ?? string.Empty;

                    if (string.Equals(wbPath, fullPath, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(wbName, fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        wb.Activate();

                        dynamic? targetSheet = null;

                        if (!string.IsNullOrWhiteSpace(sheetName) && !sheetName.StartsWith("["))
                        {
                            try
                            {
                                targetSheet = wb.Sheets[sheetName];
                            }
                            catch
                            {
                                foreach (dynamic s in wb.Sheets)
                                {
                                    if (string.Equals(s.Name?.ToString()?.Trim(), sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                                    {
                                        targetSheet = s;
                                        break;
                                    }
                                }
                            }
                        }

                        if (targetSheet != null)
                        {
                            try { targetSheet.Activate(); } catch { }
                        }
                        else
                        {
                            try { targetSheet = wb.ActiveSheet; } catch { }
                        }

                        // In-sheet navigation: scroll to rowIndex or search for sheet/bill text
                        if (targetSheet != null)
                        {
                            if (rowIndex > 0)
                            {
                                try
                                {
                                    excel.ActiveWindow.ScrollRow = Math.Max(1, rowIndex - 2);
                                    dynamic cell = targetSheet.Cells[rowIndex, 1];
                                    cell.Select();
                                }
                                catch { }
                            }
                            else if (!string.IsNullOrWhiteSpace(sheetName) && !sheetName.StartsWith("["))
                            {
                                try
                                {
                                    dynamic found = targetSheet.Cells.Find(sheetName);
                                    if (found != null)
                                    {
                                        int fRow = (int)found.Row;
                                        excel.ActiveWindow.ScrollRow = Math.Max(1, fRow - 2);
                                        found.Select();
                                    }
                                }
                                catch { }
                            }
                        }

                        // Bring Excel window to front
                        try
                        {
                            IntPtr hwnd = new IntPtr((long)excel.Hwnd);
                            if (hwnd != IntPtr.Zero)
                            {
                                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                                else ShowWindowAsync(hwnd, SW_SHOW);
                                SetForegroundWindow(hwnd);
                            }
                        }
                        catch { }

                        return true;
                    }
                }
                catch
                {
                    // Continue to next workbook if one fails
                }
            }
        }
        catch
        {
            // COM not available or Excel busy
        }

        return false;
    }

    /// <summary>
    /// Fast Win32 window search across running Excel processes to bring existing window to front.
    /// </summary>
    private static bool TryActivateWindowByTitle(string fileName, string fileNameNoExt)
    {
        try
        {
            var procs = Process.GetProcessesByName("EXCEL");
            foreach (var p in procs)
            {
                try
                {
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;

                    string title = p.MainWindowTitle;
                    if (title.Contains(fileName, StringComparison.OrdinalIgnoreCase) ||
                        title.Contains(fileNameNoExt, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                        else ShowWindowAsync(hwnd, SW_SHOW);
                        SetForegroundWindow(hwnd);
                        return true;
                    }
                }
                catch
                {
                    // Ignored
                }
            }
        }
        catch
        {
            // Ignored
        }

        return false;
    }

    /// <summary>
    /// Gets active Excel.Application COM instance if running, otherwise returns null without launching a new process.
    /// </summary>
    private static object? GetActiveExcelApplication()
    {
        try
        {
            var clsid = Type.GetTypeFromProgID("Excel.Application")?.GUID ?? Guid.Empty;
            if (clsid == Guid.Empty) return null;

            GetActiveObject(ref clsid, IntPtr.Zero, out object ppunk);
            return ppunk;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lightweight file lock check.
    /// </summary>
    public static bool IsFileLocked(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
