using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Enums;
using SmartBOQ.Domain.Models;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Specialized sub-viewmodel managing high-level project metrics, pre-flight verification analytics,
/// and multi-currency contract reconciliation summaries.
/// Inherits from <see cref="ChildViewModelBase"/>.
/// </summary>
public sealed class ProjectSummaryViewModel : ChildViewModelBase
{
    private ReconciliationResult? _result;
    private VerificationReport? _verification;

    public ReconciliationResult? Result
    {
        get => _result;
        private set
        {
            if (SetProperty(ref _result, value))
            {
                NotifyAllMetrics();
            }
        }
    }

    public VerificationReport? Verification
    {
        get => _verification;
        private set
        {
            if (SetProperty(ref _verification, value))
            {
                OnPropertyChanged(nameof(HasVerification));
                OnPropertyChanged(nameof(VerificationStatusText));
            }
        }
    }

    public bool HasResults => Result != null;
    public bool HasVerification => Verification != null;
    public string VerificationStatusText => Verification?.IsValid == true ? "سليم ومعتمد" : "يتطلب تدقيق";

    public int TotalTargetItems => Result?.TotalTargetItems ?? 0;
    public int ExactMatchesCount => Result?.ExactMatches ?? 0;
    public int HighFuzzyCount => Result?.HighFuzzyMatches ?? 0;
    public int ReviewNeededCount => Result?.ReviewNeeded ?? 0;
    public int VariationOrdersCount => Result?.VariationOrders ?? 0;
    public int ProvisionalSumsCount => Result?.ProvisionalSumsShielded ?? 0;

    public double MatchAccuracyRate
    {
        get
        {
            if (TotalTargetItems == 0) return 0.0;
            return Math.Round((double)(ExactMatchesCount + HighFuzzyCount) / TotalTargetItems * 100.0, 1);
        }
    }

    public IReadOnlyList<CurrencyBucketSummary> CurrencySummaries => Result?.CurrencySummaries ?? [];

    public string ExecutionTimeFormatted => Result != null ? $"{Result.ElapsedTime.TotalSeconds:F2} ثانية" : "-";

    public ProjectSummaryViewModel(IMainViewModelCoordinator coordinator) : base(coordinator)
    {
    }

    /// <summary>
    /// Updates the summary state with fresh execution outputs.
    /// </summary>
    public void UpdateResult(ReconciliationResult? result, VerificationReport? report = null)
    {
        Verification = report;
        Result = result;
    }

    private void NotifyAllMetrics()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(TotalTargetItems));
        OnPropertyChanged(nameof(ExactMatchesCount));
        OnPropertyChanged(nameof(HighFuzzyCount));
        OnPropertyChanged(nameof(ReviewNeededCount));
        OnPropertyChanged(nameof(VariationOrdersCount));
        OnPropertyChanged(nameof(ProvisionalSumsCount));
        OnPropertyChanged(nameof(MatchAccuracyRate));
        OnPropertyChanged(nameof(CurrencySummaries));
        OnPropertyChanged(nameof(ExecutionTimeFormatted));
    }
}
