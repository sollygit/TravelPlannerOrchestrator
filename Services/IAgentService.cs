using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TravelPlannerFunctions.Services;

/// <summary>
/// Base interface for agent services
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Gets the agent ID used by this service
    /// </summary>
    string AgentId { get; }

    /// <summary>
    /// Gets the connection string for this agent service
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Gets a response from the agent
    /// </summary>
    Task<string> GetAgentResponseAsync(string prompt);

    /// <summary>
    /// Cleans JSON response from markdown formatting
    /// </summary>
    string CleanJsonResponse(string response);
}

/// <summary>
/// Base implementation for agent services
/// </summary>
public abstract class BaseAgentService : IAgentService
{
    protected readonly JsonSerializerOptions JsonOptions;
    protected readonly ILogger<BaseAgentService> Logger;

    // Retry configuration
    private const int MaxRetryAttempts = 5;
    private const int InitialRetryDelayMs = 1000; // Start with a 1 second delay

    public string AgentId { get; }
    public string ConnectionString { get; }

    protected BaseAgentService(string agentId, string connectionString, ILogger<BaseAgentService> logger)
    {
        AgentId = agentId;
        ConnectionString = connectionString;
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));

        JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        };
    }

    protected static string GetAgentIdentifier(string envVarName)
    {
        string? agentId = Environment.GetEnvironmentVariable(envVarName);

        if (string.IsNullOrEmpty(agentId))
        {
            throw new InvalidOperationException(
                $"{envVarName} environment variable is not configured");
        }

        return agentId;
    }

    public async Task<string> GetAgentResponseAsync(string prompt)
    {
        int retryCount = 0;
        int retryDelay = InitialRetryDelayMs;
        bool shouldRetry;

        do
        {
            shouldRetry = false;

            try
            {
                if (string.IsNullOrWhiteSpace(ConnectionString))
                {
                    throw new InvalidOperationException(
                        $"Project endpoint for agent {AgentId} is not set.");
                }

                if (string.IsNullOrWhiteSpace(AgentId))
                {
                    throw new InvalidOperationException(
                        "Hosted Agent name is not set.");
                }

                var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
                TokenCredential credential = string.IsNullOrWhiteSpace(clientId) ? 
                    new AzureDeveloperCliCredential(): 
                    new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));

                var endpoint = $"{ConnectionString.TrimEnd('/')}" + $"/agents/{Uri.EscapeDataString(AgentId)}" + "/endpoint/protocols/openai/responses?api-version=v1";

                Logger.LogInformation(
                    "Calling Hosted Agent {AgentId} at {Endpoint}",
                    AgentId,
                    endpoint);

                // Hosted Agents use the ai.azure.com scope.
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext(
                        new[] { "https://ai.azure.com/.default" }),
                    CancellationToken.None);

                using var httpClient = new HttpClient();

                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        token.Token);

                httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                var requestBody = new
                {
                    input = prompt,
                    stream = false
                };

                var json = JsonSerializer.Serialize(
                    requestBody,
                    JsonOptions);

                using var content = new StringContent(
                    json,
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var response = await httpClient.PostAsync(
                    endpoint,
                    content);

                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError(
                        "Hosted Agent {AgentId} returned HTTP {StatusCode}: {Response}",
                        AgentId,
                        (int)response.StatusCode,
                        responseBody);

                    if ((int)response.StatusCode == 429 ||
                        (int)response.StatusCode == 503)
                    {
                        throw new RequestFailedException(
                            (int)response.StatusCode,
                            responseBody);
                    }

                    throw new RequestFailedException(
                        (int)response.StatusCode,
                        $"Hosted Agent request failed: {responseBody}");
                }

                Logger.LogInformation(
                    "Hosted Agent {AgentId} returned successfully.",
                    AgentId);

                // Responses API response.
                using var document =
                    JsonDocument.Parse(responseBody);

                var root = document.RootElement;

                // The Responses API normally exposes the generated text
                // through output[].content[].text.
                if (root.TryGetProperty("output", out var output) &&
                    output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outputItem in output.EnumerateArray())
                    {
                        if (!outputItem.TryGetProperty(
                                "content",
                                out var contentItems) ||
                            contentItems.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var contentItem in
                                 contentItems.EnumerateArray())
                        {
                            if (contentItem.TryGetProperty(
                                    "text",
                                    out var text))
                            {
                                var result = text.GetString();

                                if (!string.IsNullOrWhiteSpace(result))
                                {
                                    return result;
                                }
                            }
                        }
                    }
                }

                // Some Responses-compatible implementations expose
                // output_text directly.
                if (root.TryGetProperty(
                        "output_text",
                        out var outputText))
                {
                    var result = outputText.GetString();

                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }

                Logger.LogWarning(
                    "Hosted Agent {AgentId} returned a successful response " +
                    "but no text output was found. Response: {Response}",
                    AgentId,
                    responseBody);

                return string.Empty;
            }
            catch (RequestFailedException ex) when (
                (ex.Status == (int)HttpStatusCode.TooManyRequests ||
                 ex.Status == 503) &&
                retryCount < MaxRetryAttempts)
            {
                shouldRetry = true;

                retryDelay = await HandleRetry(
                    ++retryCount,
                    retryDelay,
                    ex.Message);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Error calling Hosted Agent {AgentId}",
                    AgentId);

                throw;
            }

        } while (shouldRetry);

        throw new Exception(
            $"Failed to get a response from agent {AgentId} " +
            $"after {MaxRetryAttempts} attempts");
    }

    /// <summary>
    /// Validates and normalizes JSON responses from agents
    /// </summary>
    /// <param name="response">The JSON response from an agent</param>
    /// <returns>Validated JSON string</returns>
    public string CleanJsonResponse(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            Logger.LogWarning("[JSON-PARSER] Response was null or empty");
            return "{}";
        }

        Logger.LogInformation($"[JSON-PARSER] Processing response ({response.Length} chars)");

        // Trim any whitespace
        response = response.Trim();

        // Simple case: Check if response is already valid JSON
        try
        {
            using (JsonDocument.Parse(response))
            {
                Logger.LogInformation("[JSON-PARSER] Response is valid JSON");
                return response;
            }
        }
        catch (JsonException)
        {
            Logger.LogInformation("[JSON-PARSER] Initial JSON validation failed, attempting to extract JSON");
        }

        // Handle markdown code blocks if present
        if (response.Contains("```"))
        {
            // Find start and end of code block
            int codeBlockStart = response.IndexOf("```");
            int codeBlockEnd = response.LastIndexOf("```");

            if (codeBlockStart != codeBlockEnd) // Make sure we found both opening and closing markers
            {
                // Extract content between code blocks
                int contentStart = response.IndexOf('\n', codeBlockStart);
                if (contentStart != -1 && contentStart < codeBlockEnd)
                {
                    response = response.Substring(contentStart, codeBlockEnd - contentStart).Trim();
                    Logger.LogInformation("[JSON-PARSER] Extracted content from code block");
                }
            }

            // Remove any remaining backticks
            response = response.Replace("```", "").Trim();
        }
        // Handle single backtick blocks
        else if (response.StartsWith("`") && response.EndsWith("`"))
        {
            response = response.Substring(1, response.Length - 2).Trim();
            Logger.LogInformation("[JSON-PARSER] Removed single backticks");
        }

        // Final validation
        try
        {
            using (JsonDocument.Parse(response))
            {
                Logger.LogInformation("[JSON-PARSER] Successfully validated JSON");
                return response;
            }
        }
        catch (JsonException ex)
        {
            Logger.LogError($"[JSON-PARSER] Failed to parse JSON: {ex.Message}");

            // If we can't parse it, log the problematic JSON and return empty object
            if (response.Length > 200)
            {
                Logger.LogError($"[JSON-PARSER] Problematic JSON (first 200 chars): {response.Substring(0, 200)}...");
            }
            else
            {
                Logger.LogError($"[JSON-PARSER] Problematic JSON: {response}");
            }

            return "{}";
        }
    }

    private async Task<int> HandleRetry(int retryCount, int retryDelay, string errorMessage)
    {
        // Calculate exponential backoff with jitter
        int maxJitterMs = retryDelay / 4;
        Random random = new Random();
        int jitter = random.Next(-maxJitterMs, maxJitterMs);
        int actualDelay = retryDelay + jitter;

        Logger.LogInformation($"Rate limit hit for agent {AgentId}. Retrying in {actualDelay}ms (attempt {retryCount} of {MaxRetryAttempts}). Error: {errorMessage}");

        // Wait for the calculated delay
        await Task.Delay(actualDelay);

        // Double the delay for the next retry (exponential backoff)
        return retryDelay * 2;
    }
}