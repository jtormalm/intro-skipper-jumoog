using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConfusedPolarBear.Plugin.IntroSkipper;

/// <summary>
/// Server entrypoint.
/// </summary>
public class Entrypoint : IHostedService, IDisposable
{
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ITaskManager _taskManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Entrypoint> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private Timer _queueTimer;
    private bool _analyzeAgain;
    private static CancellationTokenSource? _cancellationTokenSource;
    private static ManualResetEventSlim _autoTaskCompletEvent = new ManualResetEventSlim(false);

    /// <summary>
    /// Initializes a new instance of the <see cref="Entrypoint"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="userViewManager">User view manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public Entrypoint(
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILogger<Entrypoint> logger,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _userViewManager = userViewManager;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _logger = logger;
        _loggerFactory = loggerFactory;

        _queueTimer = new Timer(
                OnTimerCallback,
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Gets State of the automatic task.
    /// </summary>
    public static TaskState AutomaticTaskState
    {
        get
        {
            if (_cancellationTokenSource is not null)
            {
                return _cancellationTokenSource.IsCancellationRequested
                        ? TaskState.Cancelling
                        : TaskState.Running;
            }

            return TaskState.Idle;
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemModified;
        _taskManager.TaskCompleted += OnLibraryRefresh;

        FFmpegWrapper.Logger = _logger;

        try
        {
            // Enqueue all episodes at startup to ensure any FFmpeg errors appear as early as possible
            _logger.LogInformation("Running startup enqueue");
            var queueManager = new QueueManager(_loggerFactory.CreateLogger<QueueManager>(), _libraryManager);
            queueManager?.GetMediaItems();
        }
        catch (Exception ex)
        {
            _logger.LogError("Unable to run startup enqueue: {Exception}", ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemModified;
        _taskManager.TaskCompleted -= OnLibraryRefresh;

        // Stop the timer
        _queueTimer.Change(Timeout.Infinite, 0);

        if (_cancellationTokenSource != null) // Null Check
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }

        return Task.CompletedTask;
    }

    // Disclose source for inspiration
    // Implementation based on the principles of jellyfin-plugin-media-analyzer:
    // https://github.com/endrl/jellyfin-plugin-media-analyzer

    /// <summary>
    /// Library item was added.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemAdded(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance!.Configuration.AutoDetectCredits)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Library item was modified.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="itemChangeEventArgs">The <see cref="ItemChangeEventArgs"/>.</param>
    private void OnItemModified(object? sender, ItemChangeEventArgs itemChangeEventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance!.Configuration.AutoDetectCredits)
        {
            return;
        }

        // Don't do anything if it's not a supported media type
        if (itemChangeEventArgs.Item is not Episode)
        {
            return;
        }

        if (itemChangeEventArgs.Item.LocationType == LocationType.Virtual)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// TaskManager task ended.
    /// </summary>
    /// <param name="sender">The sending entity.</param>
    /// <param name="eventArgs">The <see cref="TaskCompletionEventArgs"/>.</param>
    private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs eventArgs)
    {
        // Don't do anything if auto detection is disabled
        if (!Plugin.Instance!.Configuration.AutoDetectIntros && !Plugin.Instance!.Configuration.AutoDetectCredits)
        {
            return;
        }

        var result = eventArgs.Result;

        if (result.Key != "RefreshLibrary")
        {
            return;
        }

        if (result.Status != TaskCompletionStatus.Completed)
        {
            return;
        }

        StartTimer();
    }

    /// <summary>
    /// Start timer to debounce analyzing.
    /// </summary>
    private void StartTimer()
    {
        if (Entrypoint.AutomaticTaskState == TaskState.Running)
        {
           _analyzeAgain = true; // Items added during a scan will be included later.
        }
        else if (ScheduledTaskSemaphore.CurrentCount > 0)
        {
            _logger.LogInformation("Media Library changed, analyzis will start soon!");
            _queueTimer.Change(TimeSpan.FromMilliseconds(20000), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Wait for timer callback to be completed.
    /// </summary>
    private void OnTimerCallback(object? state)
    {
        try
        {
            PerformAnalysis();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in PerformAnalysis");
        }
    }

    /// <summary>
    /// Wait for timer to be completed.
    /// </summary>
    private void PerformAnalysis()
    {
        _logger.LogInformation("Timer elapsed - start analyzing");
        _autoTaskCompletEvent.Reset();

        using (_cancellationTokenSource = new CancellationTokenSource())
        {
            var progress = new Progress<double>();
            var cancellationToken = _cancellationTokenSource.Token;

            if (Plugin.Instance!.Configuration.AutoDetectIntros && Plugin.Instance!.Configuration.AutoDetectCredits)
            {
                // This is where we can optimize a single scan
                var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                    AnalysisMode.Introduction,
                    _loggerFactory.CreateLogger<DetectIntrosCreditsTask>(),
                    _loggerFactory,
                    _libraryManager);

                baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken);

                var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                    AnalysisMode.Credits,
                    _loggerFactory.CreateLogger<DetectIntrosCreditsTask>(),
                    _loggerFactory,
                    _libraryManager);

                baseCreditAnalyzer.AnalyzeItems(progress, cancellationToken);
            }
            else if (Plugin.Instance!.Configuration.AutoDetectIntros)
            {
                var baseIntroAnalyzer = new BaseItemAnalyzerTask(
                    AnalysisMode.Introduction,
                    _loggerFactory.CreateLogger<DetectIntrosTask>(),
                    _loggerFactory,
                    _libraryManager);

                baseIntroAnalyzer.AnalyzeItems(progress, cancellationToken);
            }
            else if (Plugin.Instance!.Configuration.AutoDetectCredits)
            {
                var baseCreditAnalyzer = new BaseItemAnalyzerTask(
                    AnalysisMode.Credits,
                    _loggerFactory.CreateLogger<DetectCreditsTask>(),
                    _loggerFactory,
                    _libraryManager);

                baseCreditAnalyzer.AnalyzeItems(progress, cancellationToken);
            }
        }

        _autoTaskCompletEvent.Set();
        _cancellationTokenSource = null;

        // New item detected, start timer again
        if (_analyzeAgain)
        {
            _logger.LogInformation("Analyzing ended, but we need to analyze again!");
            _analyzeAgain = false;
            StartTimer();
        }
    }

    /// <summary>
    /// Method to cancel the automatic task.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void CancelAutomaticTask(CancellationToken cancellationToken)
    {
        if (_cancellationTokenSource != null)
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }

            _autoTaskCompletEvent.Wait(TimeSpan.FromSeconds(60), cancellationToken); // Wait for the signal
        }
    }

    /// <summary>
    /// Dispose.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Protected dispose.
    /// </summary>
    /// <param name="dispose">Dispose.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (!dispose)
        {
            _queueTimer.Dispose();

            return;
        }
    }
}
