using AiAssistant;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

using OpenAI;
using OpenAI.Embeddings;

using System.ClientModel;

var builder = Host.CreateApplicationBuilder(args);
_ = builder.Logging.SetMinimumLevel(LogLevel.Error);
var openaiCreds = new ApiKeyCredential(new ConfigurationManager().AddJsonFile("appsettings.secrets.json", optional: false).Build().GetValue<String>("OpenAiKey") ?? throw new Exception("no api key found"));
_ = builder.Services
    .AddHostedService<AiService>()
    .AddSingleton<CacheablePromptFactory>()
    .AddSingleton(sp =>
    {
        var connection = new SqliteConnection("Data Source=prompt_cache.db");

        connection.LoadExtension("vec0");

        return connection;
    })
    .AddSqliteVectorStore()
    .AddEmbeddingGenerator<String, Embedding<Single>>(b => b.Use(new OpenAIEmbeddingGenerator(new EmbeddingClient("text-embedding-3-small", openaiCreds), dimensions: 1024)))
    .AddChatClient(b =>
        b.UseFunctionInvocation(c =>
        {
            c.DetailedErrors = true;
            c.KeepFunctionCallingMessages = true;
            c.RetryOnError = true;
        })
        .Use(
            new OpenAIChatClient(
                new OpenAIClient(openaiCreds),
                "gpt-4o")));

var host = builder.Build();
await host.RunAsync();
