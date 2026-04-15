using Microsoft.Extensions.AI;
using OllamaSharp;
using GenerativeAI.Microsoft;
using System.ClientModel;
using OpenAI;
using Azure.AI.Projects;
using Microsoft.Agents.AI;
using Azure;
using Azure.Identity;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace GiteaAiSummarizer.Services;

public static class ChatClientFactory
{
    public const string AIEngine_AZURE = "Azure";
    public const string AIEngine_OPENAI = "OpenAI";
    public const string AIEngine_OLLAMA = "Ollama";
    public const string AIEngine_GEMINI = "Gemini";
    public const string AIEngine_ANTHROPIC = "Anthropic";

    public static IChatClient CreateChatClient(
        string? pType,
        string? pEndpoint,
        string? pModelName,
        string? pKey
    )
    {
        // validate parameters
        if (string.IsNullOrEmpty(pType))
            throw new ArgumentNullException(nameof(pType), "AI engine type is not configured.");

        bool isAzure = AIEngine_AZURE.Equals(pType, StringComparison.OrdinalIgnoreCase);
        bool isOpenAI = AIEngine_OPENAI.Equals(pType, StringComparison.OrdinalIgnoreCase);
        bool isOllama = AIEngine_OLLAMA.Equals(pType, StringComparison.OrdinalIgnoreCase);
        bool isGemini = AIEngine_GEMINI.Equals(pType, StringComparison.OrdinalIgnoreCase);
        bool isAnthropic = AIEngine_ANTHROPIC.Equals(pType, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(pEndpoint) && (isAzure || isOllama))
            throw new ArgumentNullException(nameof(pEndpoint), "AI endpoint is not configured.");

        if (string.IsNullOrEmpty(pModelName) && (isOllama || isGemini || isAzure))
            throw new ArgumentNullException(nameof(pModelName), "AI model name is not configured.");

        if (string.IsNullOrEmpty(pKey) && (isOpenAI || isGemini || isAzure || isAnthropic))
            throw new ArgumentNullException(nameof(pKey), "AI API key is not configured.");

        if (isAzure)
        {
            /*
            //https://learn.microsoft.com/en-sg/answers/questions/5587848/how-to-use-api-key-in-azure-ai-foundry
            var options = new AzureOpenAIClientOptions();
            AzureOpenAIClient azureClient = new AzureOpenAIClient(
                new Uri(pEndpoint!),
                new ApiKeyCredential(pKey!),
                options
            );

            return azureClient.GetChatClient(pModelName!).AsIChatClient();
            */

            ChatClient client =
                new(
                    credential: new ApiKeyCredential(pKey!),
                    model: pModelName!,
                    options: new OpenAIClientOptions() { Endpoint = new($"{pEndpoint!}"), }
                );

            return client.AsIChatClient();
        }
        else if (isOpenAI)
        {
            return new OpenAIClient(pKey!).GetChatClient(pModelName).AsIChatClient();
        }
        else if (isOllama)
        {
            var httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromMinutes(40),
                BaseAddress = new Uri(pEndpoint!)
            };
            return new OllamaApiClient(httpClient) { SelectedModel = pModelName! };
        }
        else if (isGemini)
        {
            return new GenerativeAIChatClient(pKey!, pModelName!);
        }
        else if (isAnthropic)
        {
            // return new Anthropic.SDK.AnthropicClient(pKey!).AsChatClient(
            //     pModelName ?? "claude-3-5-sonnet-latest"
            // );
        }

        throw new NotSupportedException($"The AI engine type '{pType}' is not supported.");
    }
}
