namespace AiAssistant;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using Microsoft.Extensions.AI;

sealed class CacheablePromptFactory(IChatClient chatClient, IEmbeddingGenerator<String, Embedding<Single>> embeddingGenerator)
{
    public Task<CacheablePrompt> Create(String prompt, CancellationToken ct = default) => Create(prompt, String.Empty, ct);
    public async Task<CacheablePrompt> Create(String prompt, String response, CancellationToken ct = default)
    {
        var embedTask = embeddingGenerator.GenerateEmbeddingAsync(
            prompt,
            new EmbeddingGenerationOptions() { Dimensions = 1024 },
            ct);
        var tagsTask = chatClient.CompleteAsync($"Create a comma-separated list of tags with no more than 10 and at least 1 values that describe succinctly the contents of this text and makes it suitable for later retrieval and categorization. DO not include any text in your response except for the list of tags: {prompt}", cancellationToken: ct);

        await Task.WhenAll(embedTask, tagsTask);

        var promptEmbedding = embedTask.Result;
        //TODO: use tags
        //var tags = tagsTask.Result.Message.Text?.Split([','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10).ToArray() ?? [];

        var result = new CacheablePrompt()
        {
            Prompt = prompt,
            PromptEmbedding = promptEmbedding.Vector,
            Response = response
            //Tags = tags
        };

        return result;
    }
}
