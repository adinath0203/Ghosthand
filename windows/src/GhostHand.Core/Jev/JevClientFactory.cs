using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostHand.Core.Jev;

public static class JevClientFactory
{
    /// <summary>Creates the decision-model client for the provider selected in <see cref="JevOptions.Provider"/>.</summary>
    public static IJevClient Create(HttpClient httpClient, JevOptions options, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return options.Provider == JevProvider.OpenRouter
            ? new OpenRouterJevClient(httpClient, options, loggerFactory.CreateLogger<OpenRouterJevClient>())
            : new JevClient(httpClient, options, loggerFactory.CreateLogger<JevClient>());
    }
}
