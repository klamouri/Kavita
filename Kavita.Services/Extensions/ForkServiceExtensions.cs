using Kavita.API.Services;
using Kavita.Services.Reading;
using Kavita.Services.Scanner.StrictMode;
using Microsoft.Extensions.DependencyInjection;

namespace Kavita.Services.Extensions;

/// <summary>
/// Fork-only DI registration entry point. All strict-mode + parser-debug-log
/// services are wired here so <c>ApplicationServiceExtensions</c> only needs a
/// single <c>services.AddForkServices()</c> call — keeps the upstream-touch
/// surface to one line and means future fork services can be added without
/// re-editing upstream.
/// </summary>
public static class ForkServiceExtensions
{
    public static IServiceCollection AddForkServices(this IServiceCollection services)
    {
        services.AddScoped<IStrictReadingItemService, StrictReadingItemService>();
        services.AddSingleton<IParserDebugLog, ParserDebugLog>();
        return services;
    }
}
