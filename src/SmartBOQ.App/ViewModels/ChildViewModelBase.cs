using SmartBOQ.Application.Services;
using SmartBOQ.Domain.Interfaces;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Abstract base class for all child sub-viewmodels in the application.
/// Inherits from <see cref="ViewModelBase"/> and establishes OOP coordination with the shell.
/// </summary>
public abstract class ChildViewModelBase : ViewModelBase
{
    protected IMainViewModelCoordinator Coordinator { get; }

    protected ChildViewModelBase(IMainViewModelCoordinator coordinator)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>
    /// Direct access to the shared reconciliation service.
    /// </summary>
    protected BoqReconciliationService Service => Coordinator.Service;

    /// <summary>
    /// Direct access to the shared localization service.
    /// </summary>
    protected ILocalizationService Localization => Coordinator.Localization;

    /// <summary>
    /// Direct access to the shared inspector service.
    /// </summary>
    protected IBoqInspector Inspector => Coordinator.Inspector;

    /// <summary>
    /// Updates the global status bar message and progress.
    /// </summary>
    protected void SetStatus(string message, int progress = -1)
    {
        Coordinator.SetStatus(message, progress);
    }

    /// <summary>
    /// Sets global loading and busy indicators.
    /// </summary>
    protected void SetBusy(bool isBusy)
    {
        Coordinator.SetBusy(isBusy);
    }

    /// <summary>
    /// Executes an asynchronous unit of work with UI busy tracking.
    /// </summary>
    protected Task ExecuteAsync(Func<Task> action, string initialStatus = "Processing...")
    {
        return Coordinator.ExecuteWithBusyIndicatorAsync(action, initialStatus);
    }
}
