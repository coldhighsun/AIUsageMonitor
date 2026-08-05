using Spectre.Console;

namespace AIUsageMonitor.Cli.Rendering;

public static class ProgressReporter
{
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

    // System.Progress<T> marshals callbacks through the captured SynchronizationContext (or the
    // thread pool if there is none), which would race with Spectre's render loop in a console app.
    // Report synchronously on the calling thread instead so the progress bar updates in step.
    private sealed class SynchronousProgress(Action<int> onReport) : IProgress<int>
    {
        public void Report(int value) => onReport(value);
    }
}
