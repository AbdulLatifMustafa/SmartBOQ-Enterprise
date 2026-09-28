using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartBOQ.App.Services;
using SmartBOQ.Infrastructure.Common;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Specialized sub-viewmodel managing Excel workbook export, dynamic formula linking,
/// and external Excel file viewing.
/// Inherits from <see cref="ChildViewModelBase"/>.
/// </summary>
public sealed class ExportFileViewModel : ChildViewModelBase
{
    private string _outputFilePath = string.Empty;
    private string _dashboardFilePath = string.Empty;
    private bool _isExported;
    private string _lastExportedSchedulePath = string.Empty;
    private string _lastExportedDashboardPath = string.Empty;
    private string _lastExportedFolder = string.Empty;

    public string OutputFilePath { get => _outputFilePath; set => SetProperty(ref _outputFilePath, value); }
    public string DashboardFilePath { get => _dashboardFilePath; set => SetProperty(ref _dashboardFilePath, value); }

    public bool IsExported { get => _isExported; set => SetProperty(ref _isExported, value); }
    public string LastExportedSchedulePath { get => _lastExportedSchedulePath; set => SetProperty(ref _lastExportedSchedulePath, value); }
    public string LastExportedDashboardPath { get => _lastExportedDashboardPath; set => SetProperty(ref _lastExportedDashboardPath, value); }
    public string LastExportedFolder { get => _lastExportedFolder; set => SetProperty(ref _lastExportedFolder, value); }

    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenReconciledFileCommand { get; }
    public RelayCommand OpenDashboardCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    public ExportFileViewModel(IMainViewModelCoordinator coordinator) : base(coordinator)
    {
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        OpenReconciledFileCommand = new RelayCommand(_ => OpenReconciledFile());
        OpenDashboardCommand = new RelayCommand(_ => OpenDashboard());
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
    }

    public void BrowseOutput()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
            DefaultExt = ".xlsx",
            Title = "تحديد مسار حفظ مقايسة الأسعار المدمجة"
        };

        if (dlg.ShowDialog() == true)
        {
            OutputFilePath = dlg.FileName;
        }
    }

    public void OpenReconciledFile()
    {
        string path = !string.IsNullOrWhiteSpace(LastExportedSchedulePath) ? LastExportedSchedulePath : OutputFilePath;
        OpenFileWithSystemDefault(path);
    }

    public void OpenDashboard()
    {
        string path = !string.IsNullOrWhiteSpace(LastExportedDashboardPath) ? LastExportedDashboardPath : DashboardFilePath;
        OpenFileWithSystemDefault(path);
    }

    public void OpenOutputFolder()
    {
        string folder = LastExportedFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            string outPath = !string.IsNullOrWhiteSpace(OutputFilePath) ? OutputFilePath : DashboardFilePath;
            folder = Path.GetDirectoryName(outPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        if (Directory.Exists(folder))
        {
            try
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private static void OpenFileWithSystemDefault(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch { }
    }
}
