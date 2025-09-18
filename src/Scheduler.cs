using System;
using System.Threading;
using Cronos;
using Microsoft.Extensions.Logging;

namespace P4Sync
{
    /// <summary>
    /// Implements cron-based scheduling functionality for sync jobs using Cronos for cron expression parsing
    /// </summary>
    /// <remarks>
    /// This class handles scheduling of sync jobs based on cron expressions.
    /// It uses the Cronos library to parse cron expressions and calculate next occurrence times.
    /// The scheduler will automatically continue scheduling the next occurrence after each execution.
    /// </remarks>
    public class Scheduler : IDisposable
    {
        private readonly string _cronExpression;
        private readonly Action _action;
        private readonly CronExpression _expression;
        private readonly ILogger<Scheduler> _logger;
        private Timer? _timer;
        private bool _isRunning = false;
        private readonly object _lock = new object();
        private DateTime _lastExecution = DateTime.MinValue;

        public Scheduler(string cronExpression, Action action, ILogger<Scheduler> logger)
        {
            if (string.IsNullOrWhiteSpace(cronExpression))
                throw new ArgumentNullException(nameof(cronExpression));

            _cronExpression = cronExpression;
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Parse the cron expression
            _expression = CronExpression.Parse(cronExpression);
            _logger.LogInformation("Scheduler initialized with cron expression: '{CronExpression}'", cronExpression);
        }

        /// <summary>
        /// Starts the scheduler
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning)
                {
                    _logger.LogWarning("Scheduler is already running, ignoring Start request");
                    return;
                }

                _logger.LogInformation("Starting scheduler");
                ScheduleNext();
                _isRunning = true;
                _logger.LogInformation("Scheduler started successfully");
            }
        }

        /// <summary>
        /// Stops the scheduler
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning)
                {
                    _logger.LogWarning("Scheduler is not running, ignoring Stop request");
                    return;
                }

                _logger.LogInformation("Stopping scheduler");
                _timer?.Dispose();
                _timer = null;
                _isRunning = false;
                _logger.LogInformation("Scheduler stopped successfully");
            }
        }

        /// <summary>
        /// Schedules the next execution based on the cron expression
        /// </summary>
        private void ScheduleNext()
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextOccurrence = _expression.GetNextOccurrence(now);
    
                if (nextOccurrence.HasValue)
                {
                    var delay = nextOccurrence.Value - now;
                    
                    // Handle case where delay is negative (scheduled time is in the past)
                    if (delay.TotalMilliseconds < 0)
                    {
                        Console.WriteLine($"Warning: Calculated execution time is in the past: {nextOccurrence.Value}. Scheduling for next occurrence.");
                        nextOccurrence = _expression.GetNextOccurrence(now.AddMinutes(1));
                        if (nextOccurrence.HasValue)
                        {
                            delay = nextOccurrence.Value - now;
                        }
                        else
                        {
                            Console.WriteLine("Unable to find a valid future occurrence. Scheduling cancelled.");
                            return;
                        }
                    }
                    
                    Console.WriteLine($"Scheduling next execution at: {nextOccurrence.Value} (in {delay.TotalMinutes:F1} minutes)");
                    
                    _timer?.Dispose();
                    _timer = new Timer(ExecuteAction, null, delay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    Console.WriteLine("No future occurrences found for the cron expression");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error scheduling next execution: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes the scheduled action and schedules the next execution
        /// </summary>
        private void ExecuteAction(object? state)
        {
            var executionTime = DateTime.UtcNow;
            
            // Check if this execution is too close to the last one (within 10 seconds)
            if ((executionTime - _lastExecution).TotalSeconds < 10)
            {
                Console.WriteLine($"Warning: Execution triggered too soon after previous execution. Skipping this execution.");
                ScheduleNext();
                return;
            }
            
            _lastExecution = executionTime;
            Console.WriteLine($"[{executionTime}] Executing scheduled action...");
            
            try
            {
                _action();
                Console.WriteLine($"[{DateTime.UtcNow}] Scheduled action completed successfully (took {(DateTime.UtcNow - executionTime).TotalSeconds:F1} seconds)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in scheduled action: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            finally
            {
                // Schedule the next execution
                ScheduleNext();
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
