using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Interfaces;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Contract for the master ViewModel coordinator.
/// Decouples specialized child ViewModels from the concrete MainViewModel shell,
/// enabling pure OOP composition, testability, and isolated component lifecycles.
/// </summary>
public interface IMainViewModelCoordinator
{
    /// <summary>
    /// Updates the global application status bar text and optional progress percentage.
    /// </summary>
    void SetStatus(string message, int progress = -1);

    /// <summary>
    /// Sets global loading and busy indicators.
    /// </summary>
    void SetBusy(bool isBusy);

    /// <summary>
    /// Executes an asynchronous task safely with the global busy indicator and exception interception.
    /// </summary>
    Task ExecuteWithBusyIndicatorAsync(Func<Task> action, string initialStatus = "Processing...");

    /// <summary>
    /// Shared BOQ core reconciliation service.
    /// </summary>
    BoqReconciliationService Service { get; }

    /// <summary>
    /// Shared localization service.
    /// </summary>
    ILocalizationService Localization { get; }

    /// <summary>
    /// Shared workbook inspection service.
    /// </summary>
    IBoqInspector Inspector { get; }

    /// <summary>
    /// Physical path to the active SQLite persistence database.
    /// </summary>
    string DatabaseFilePath { get; }
}
