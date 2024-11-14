using RadLine;

public sealed class PromptCompletion : ITextCompletion
{
    public IEnumerable<String> GetCompletions(String context, String word, String suffix) => [];
}

#if false
var builder = Host.CreateApplicationBuilder(args);
_ = builder.Logging.SetMinimumLevel(LogLevel.Error);
var openaiCreds = new ApiKeyCredential(new ConfigurationManager().AddJsonFile("appsettings.secrets.json").AddCommandLine(args).Build().GetValue<String>("OpenAiKey") ?? throw new Exception("no api key found"));
_ = builder.Services
    .AddHostedService<MainService>()
    .AddSingleton(s => new FunctionsDbContext($"Data Source={s.GetRequiredService<Settings>().FunctionsStorePath}"))
    .AddSingleton<Functions>()
    .AddSingleton(sp => sp.GetRequiredService<IOptions<Settings>>().Value)
    .AddOptions<Settings>()
    .BindConfiguration("Settings")
    .PostConfigure(s => s.PromptsCachePath ??= "prompts_cache.db")
    .PostConfigure(s => s.FunctionsStorePath ??= "functions_store.db")
    .Services
    .AddSingleton<CacheablePromptFactory>()
    .AddSingleton(sp =>
    {
        var settings = sp.GetRequiredService<Settings>();
        var connection = new SqliteConnection($"Data Source={settings.PromptsCachePath}");

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
            c.ConcurrentInvocation = false;
            c.MaximumIterationsPerRequest = 32;
        })
        .Use(
            new OpenAIChatClient(
                new OpenAIClient(openaiCreds),
                "gpt-4o")));

var host = builder.Build();
await host.RunAsync();

#endif