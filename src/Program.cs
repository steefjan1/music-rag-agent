using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var credential = new DefaultAzureCredential();

        // Azure AI Search clients — used by both ingestion and chat functions.
        var searchEndpoint = new Uri(
            Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_SEARCH_ENDPOINT is required."));

        services.AddSingleton(new SearchClient(searchEndpoint, "music-index", credential));
        services.AddSingleton(new SearchIndexClient(searchEndpoint, credential));
    })
    .Build();

host.Run();
