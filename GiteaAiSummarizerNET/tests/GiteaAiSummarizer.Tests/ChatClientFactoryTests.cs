using GiteaAiSummarizer.Services;

namespace GiteaAiSummarizer.Tests;

public class ChatClientFactoryTests
{
    [Fact]
    public void CreateChatClient_MissingEngineType_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateChatClient(null, "endpoint", "model", "key"));
    }

    [Fact]
    public void CreateChatClient_UnsupportedEngine_Throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            ChatClientFactory.CreateChatClient("NotAThing", "endpoint", "model", "key"));
    }

    [Fact]
    public void CreateChatClient_Ollama_MissingEndpoint_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatClientFactory.CreateChatClient(ChatClientFactory.AIEngine_OLLAMA, null, "model", null));
    }

    [Fact]
    public void CreateChatClient_Anthropic_FallsThroughToNotSupported()
    {
        // Known gap (unrelated to this migration): the Anthropic branch never
        // returns a client, so it always falls through to this exception.
        Assert.Throws<NotSupportedException>(() =>
            ChatClientFactory.CreateChatClient(ChatClientFactory.AIEngine_ANTHROPIC, null, "model", "key"));
    }

    [Fact]
    public void CreateChatClient_Ollama_Valid_ReturnsClient()
    {
        var client = ChatClientFactory.CreateChatClient(
            ChatClientFactory.AIEngine_OLLAMA,
            "http://localhost:11434",
            "llama3",
            null
        );

        Assert.NotNull(client);
    }
}
