using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using SmartBOQ.App.Services;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Specialized sub-viewmodel managing historical rate search, price intelligence benchmarks,
/// and SQLite database interactions.
/// Inherits from <see cref="ChildViewModelBase"/>.
/// </summary>
public sealed class HistoricalRatesViewModel : ChildViewModelBase
{
    private string _historicalSearchQuery = string.Empty;
    private bool _isSearchingHistorical;
    private CancellationTokenSource? _historicalSearchCts;

    public ObservableCollection<HistoricalRateItem> HistoricalSearchResults { get; } = [];

    public string HistoricalSearchQuery
    {
        get => _historicalSearchQuery;
        set
        {
            if (SetProperty(ref _historicalSearchQuery, value))
            {
                DebounceHistoricalSearch();
            }
        }
    }

    public bool IsSearchingHistorical
    {
        get => _isSearchingHistorical;
        set => SetProperty(ref _isSearchingHistorical, value);
    }

    public int HistoricalTotalCount => HistoricalSearchResults.Count;
    public bool HasNoHistoricalResults => !IsSearchingHistorical && HistoricalSearchResults.Count == 0;
    public bool HasHistoricalResults => HistoricalSearchResults.Count > 0;

    public string HistoricalAvgRate
    {
        get
        {
            if (HistoricalSearchResults.Count == 0) return "-";
            var validRates = HistoricalSearchResults.Where(x => x.UnitRate.HasValue && x.UnitRate.Value > 0).ToList();
            if (validRates.Count == 0) return "-";
            decimal avg = validRates.Average(x => x.UnitRate!.Value);
            string cur = validRates.FirstOrDefault()?.Currency ?? "EGP";
            return $"{avg:N2} {cur}";
        }
    }

    public string HistoricalLatestDate
    {
        get
        {
            if (HistoricalSearchResults.Count == 0) return "-";
            var latest = HistoricalSearchResults.OrderByDescending(x => x.SnapshotDate).FirstOrDefault();
            return latest != null ? latest.SnapshotDate.ToString("yyyy-MM-dd") : "-";
        }
    }

    public RelayCommand SearchHistoricalCommand { get; }
    public RelayCommand ClearHistoricalSearchCommand { get; }
    public RelayCommand RefreshHistoricalCommand { get; }
    public RelayCommand OpenHistoricalItemLocationCommand { get; }
    public RelayCommand OpenDatabaseLocationCommand { get; }

    public HistoricalRatesViewModel(IMainViewModelCoordinator coordinator) : base(coordinator)
    {
        SearchHistoricalCommand = new RelayCommand(_ => _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery));
        ClearHistoricalSearchCommand = new RelayCommand(_ =>
        {
            HistoricalSearchQuery = string.Empty;
            _ = ExecuteHistoricalSearchAsync(string.Empty);
        });
        RefreshHistoricalCommand = new RelayCommand(_ => _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery));
        OpenHistoricalItemLocationCommand = new RelayCommand(p => OpenHistoricalItemLocation(p as HistoricalRateItem));
        OpenDatabaseLocationCommand = new RelayCommand(_ => OpenDatabaseLocation());
    }

    private void DebounceHistoricalSearch()
    {
        _historicalSearchCts?.Cancel();
        _historicalSearchCts = new CancellationTokenSource();
        var token = _historicalSearchCts.Token;

        Task.Delay(250, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _ = ExecuteHistoricalSearchAsync(HistoricalSearchQuery);
                });
            }
        }, TaskScheduler.Default);
    }

    public async Task ExecuteHistoricalSearchAsync(string? query = null)
    {
        IsSearchingHistorical = true;
        try
        {
            var results = await Service.SearchHistoricalRatesAsync(query);
            HistoricalSearchResults.Clear();
            foreach (var item in results)
            {
                HistoricalSearchResults.Add(item);
            }

            OnPropertyChanged(nameof(HistoricalTotalCount));
            OnPropertyChanged(nameof(HasNoHistoricalResults));
            OnPropertyChanged(nameof(HasHistoricalResults));
            OnPropertyChanged(nameof(HistoricalAvgRate));
            OnPropertyChanged(nameof(HistoricalLatestDate));
        }
        catch (Exception ex)
        {
            SetStatus($"خطأ في البحث التاريخي: {ex.Message}");
        }
        finally
        {
            IsSearchingHistorical = false;
            OnPropertyChanged(nameof(HasNoHistoricalResults));
        }
    }

    public async Task LoadHistoricalRatesIfEmptyAsync()
    {
        if (HistoricalSearchResults.Count == 0)
        {
            await ExecuteHistoricalSearchAsync(HistoricalSearchQuery);
        }
    }

    public void OpenHistoricalItemLocation(HistoricalRateItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.ExportFilePath) || !File.Exists(item.ExportFilePath))
        {
            return;
        }

        ExcelNavigator.OpenWorkbookAtSheet(item.ExportFilePath, item.BillNumber, 0);
    }

    public void OpenDatabaseLocation()
    {
        try
        {
            string dbPath = Coordinator.DatabaseFilePath;
            string dir = Path.GetDirectoryName(dbPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(dbPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dbPath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch { }
    }
}
