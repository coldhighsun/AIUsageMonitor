using Spectre.Console;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// Provides a utility for running a task with a progress bar in the console using Spectre.Console.
/// </summary>
public static class ProgressReporter
{
    /// <summary>
    /// Runs a task with a progress bar in the console.
    /// </summary>
    /// <typeparam name="T">The type of the result produced by the task.</typeparam>
    /// <param name="description">A description of the task to be displayed in the progress bar.</param>
    /// <param name="body">A function that performs the task and reports progress.</param>
    /// <returns>The result produced by the task.</returns>
    public static T Run<T>(string description, Func<IProgress<int>, T> body)
    {
        var result = default(T)!;
        AnsiConsole.Progress().AutoClear(true).Start(ctx =>
        {
            var task = ctx.AddTask(description, maxValue: 100);
            result = body(new SynchronousProgress(percent => task.Value = percent));
            task.Value = 100;
            task.StopTask();
        });
        return result;
    }

    /// <summary>
    /// A private implementation of IProgress&lt;int&gt; that reports progress synchronously to a provided action.
    /// </summary>
    /// <param name="onReport">The action to be invoked when progress is reported.</param>
    private sealed class SynchronousProgress(Action<int> onReport) : IProgress<int>
    {
        public void Report(int value) => onReport(value);
    }
}