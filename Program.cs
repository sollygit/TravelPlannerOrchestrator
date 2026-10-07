using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TravelPlannerFunctions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddLogging();
        services.AddSingleton((serviceProvider) =>
        {
            
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var destinationConnectionString = configuration["DESTINATION_RECOMMENDER_CONNECTION"] ?? throw new InvalidOperationException("Destination recommender connection is not configured");
            var logger = loggerFactory.CreateLogger<DestinationRecommenderService>();
            return new DestinationRecommenderService(destinationConnectionString, loggerFactory.CreateLogger<DestinationRecommenderService>());
        });
        
        services.AddSingleton((serviceProvider) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var itineraryConnectionString = configuration["ITINERARY_PLANNER_CONNECTION"] ?? throw new InvalidOperationException("Itinerary planner connection string is not configured");
            var logger = loggerFactory.CreateLogger<ItineraryPlannerService>();
            return new ItineraryPlannerService(itineraryConnectionString, loggerFactory.CreateLogger<ItineraryPlannerService>());
        });

        services.AddSingleton((serviceProvider) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var localRecommendationsConnectionString = configuration["LOCAL_RECOMMENDATIONS_CONNECTION"] ?? throw new InvalidOperationException("Local recommendations connection is not configured");
            var logger = loggerFactory.CreateLogger<LocalRecommendationsService>();
            return new LocalRecommendationsService(localRecommendationsConnectionString, loggerFactory.CreateLogger<LocalRecommendationsService>());
        });

        // Older package versions expose AddAzureClients(Action<AzureClientFactoryBuilder>) only.
        // Build a temporary provider to access IConfiguration here so we don't depend on the overload that accepts IServiceProvider.
        using var serviceProvider = services.BuildServiceProvider();
        services.AddAzureClients(clientBuilder =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var clientId = configuration["AZURE_CLIENT_ID"] ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            TokenCredential credential = string.IsNullOrWhiteSpace(clientId) ? 
                new AzureDeveloperCliCredential() : 
                new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));

            clientBuilder.UseCredential(credential);

            var connectionString = configuration["AzureWebJobsStorage"] ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");
            if (!string.IsNullOrEmpty(connectionString))
            {
                clientBuilder.AddBlobServiceClient(connectionString);
            }
            else
            {
                var storageAccountName = configuration["AzureWebJobsStorage__accountName"] ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName");
                ArgumentException.ThrowIfNullOrEmpty(storageAccountName, "AzureWebJobsStorage__accountName environment variable is not set.");

                clientBuilder.AddBlobServiceClient(
                    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                    credential);
            }
        });

        // Configure CORS for both local development and Azure Static Web App
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                // Get the Static Web App URL from environment variables (set in Azure)
                var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? "*";

                // Split by comma if multiple origins are provided
                var origins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries);

                if (origins.Length == 1 && origins[0] == "*")
                {
                    // For development or if no specific origins are set, allow any origin
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
                else
                {
                    // For production with specific origins
                    policy.WithOrigins(origins)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                }
            });
        });
    })
    .ConfigureAppConfiguration(builder =>
    {
        builder.AddEnvironmentVariables();
        builder.AddUserSecrets<Program>(optional: true);
    })
    .Build();

await host.RunAsync();