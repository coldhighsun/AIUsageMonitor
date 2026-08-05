using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.DependencyInjection;

namespace AIUsageMonitor.Core.Services;

/// <summary>
/// Provides extension methods for registering core services related to Claude usage monitoring in an <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core services related to Claude usage monitoring to the specified <see cref="IServiceCollection"/>. This includes services for data location, parsing, caching, cost calculation, activity building, and usage analysis.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to which the services will be added.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
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