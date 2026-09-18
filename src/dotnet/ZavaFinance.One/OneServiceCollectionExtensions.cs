using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZavaFinance.One;

public static class OneServiceCollectionExtensions
{
    public static IServiceCollection AddFinanceActivity(this IServiceCollection services)
    {
        services.TryAddSingleton<IActivityAssertionValidator, ActivityAssertionValidator>();
        services.TryAddSingleton<IFinanceActivityRunner, FinanceActivityRunner>();
        return services;
    }
}
