using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.DependencyInjection;

namespace AIUsageMonitor.Core.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeUsageCore(this IServiceCollection services)
    {
        services.AddSingleton<ClaudeDataLocator>();
        services.AddSingleton<StatsCacheParser>();
        services.AddSingleton<StatsCacheBuilder>();
        services.AddSingleton<SessionParser>();
        services.AddSingleton<SessionFileCache>();
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<RecentActivityBuilder>();
        services.AddSingleton<HourlyActivityBuilder>();
        services.AddSingleton<HistoryParser>();
        services.AddSingleton<ClaudeUsageProvider>();
        services.AddSingleton<IUsageProvider>(sp => sp.GetRequiredService<ClaudeUsageProvider>());
        services.AddSingleton<UsageAnalyzer>();
        services.AddSingleton<DataService>();
        return services;
    }
}
