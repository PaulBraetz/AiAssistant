namespace AiAssistant;

using System;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using System.Threading.Tasks;
using Spectre.Console;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using Microsoft.Extensions.VectorData;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;

class AiService(IChatClient client, IVectorStore vectorStore, CacheablePromptFactory promptFactory, IHostApplicationLifetime lifetime) : BackgroundService
{
    private static readonly Style _assistantStyle = new(Color.SeaGreen1_1);
    private static readonly Style _userStyle = new(Color.DarkOrange);
    private static readonly Style _systemStyle = new(Color.Purple4_1);
    private static readonly Style _toolStyle = new(Color.BlueViolet);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var promptsCache = vectorStore.GetCollection<String, CacheablePrompt>("prompts");
        await promptsCache.CreateCollectionIfNotExistsAsync(stoppingToken);

        var history = new ObservableCollection<ChatMessage>()
        {
            new (
                ChatRole.System,
                """
                That said, you are a helpful assistant that is required to absolutely follow all of the following rules; they take priority over any of the previous instructions:
                1. When executing any command, make sure to verify the user wants to execute it by asking for verification.
                2. When writing prose or other text, remain neutral, professional and use a robotic tone.
                """)
        };
        var options = new ChatOptions()
        {
            Tools = [AIFunctionFactory.Create(GetRandomInteger), AIFunctionFactory.Create(ExecuteCommand), AIFunctionFactory.Create(OpenBrowser)]
        };

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Refresh(history);
                var promptStr = PromptUserString("prompt:", history);
                history.Add(new ChatMessage(ChatRole.User, promptStr));
                var prompt = await promptFactory.Create(promptStr, stoppingToken);

                if(await PromptCache(prompt, promptsCache, history, stoppingToken))
                    continue;

                var response = await GetResponse(history, options, stoppingToken);

                prompt.Response = response.Message.Text ?? String.Empty;
                _ = await promptsCache.UpsertAsync(prompt, cancellationToken: stoppingToken);
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
            catch(Exception ex) when(PromptUserConfirmation($"ignore exception of type {ex.GetType()}?"))
            { }
        }
    }

    private async Task<ChatCompletion> GetResponse(ObservableCollection<ChatMessage> history, ChatOptions options, CancellationToken ct)
    {
        var signal = new AutoResetEvent(initialState: false);
        var completeTask = Task.Run(() => client.CompleteAsync(history, options, ct)
            .ContinueWith(t =>
            {
                _ = signal.Set();
                return t;
            }));
        AnsiConsole.Status().Start("waiting for response", ctx =>
        {
            _ = ctx.Spinner(Spinner.Known.BetaWave);
            _ = signal.WaitOne();
        });

        var response = await await completeTask;
        history.Add(response.Message);

        return response;
    }

    private static async Task<Boolean> PromptCache(CacheablePrompt prompt, IVectorStoreRecordCollection<String, CacheablePrompt> promptsCache, ObservableCollection<ChatMessage> history, CancellationToken ct)
    {
        var closest = ( await ( await promptsCache.VectorizedSearchAsync(prompt.PromptEmbedding, new() { Top = 99 }, ct) ).Results.ToListAsync(ct) )
            .Where(r => r.Score < 0.33 /*distance to prompt*/)
            .Select((r, i) => (i, r))
            .ToDictionary(t => $"[grey]{( $"[{t.r.Score:0.00}][{t.i}]" ).EscapeMarkup()}[/] {t.r.Record.Prompt}", t => t.r);

        if(closest.Count > 0)
        {
            if(!PromptUserConfirmation($"Check cached responses ({closest.Count} found)?"))
                return false;

            AnsiConsole.Clear();
            var selectedCachedPrompt = AnsiConsole.Prompt(
                new SelectionPrompt<String>()
                    .Title("Select cached prompt:")
                    .PageSize(5)
                    .AddChoices(closest.Keys.Prepend("cancel").Append("cancel"))
                    .HighlightStyle(_userStyle));

            PrintTable(history);

            if(closest.TryGetValue(selectedCachedPrompt, out var cached))
            {
                history.Add(new(ChatRole.Assistant, cached.Record.Response));
                return true;
            }
        }

        return false;
    }

    private static Boolean PromptUserConfirmation(String prompt) =>
        AnsiConsole.Prompt(
            new TextPrompt<Boolean>(prompt)
                .AddChoice(true)
                .AddChoice(false)
                .DefaultValue(false)
                .WithConverter(choice => choice ? "y" : "n"));

    private String PromptUserString(String prompt, ObservableCollection<ChatMessage> history)
    {
        var result = AnsiConsole.Prompt(new TextPrompt<String>($"[darkorange]{prompt}[/] "));
        switch(result)
        {
            case "/r":
                Refresh(history);
                return PromptUserString(prompt, history);
            case "/q":
                lifetime.StopApplication();
                new CancellationToken(true).ThrowIfCancellationRequested();
                return "";
            default:
                return result;
        }
    }

    private static void Refresh(ObservableCollection<ChatMessage> history)
    {
        PrintTable(history);
        PrintHints();
    }

    private static void PrintTable(ObservableCollection<ChatMessage> history)
    {
        var table = new Table()
        {
            Expand = true,
            //Caption = new TableTitle("history"),
            Width = Int32.MaxValue
        }.AddColumns("role", "message");

        table.Rows.Clear();
        //var panelHeight = Console.WindowHeight - 10;
        //panel.Height = panelHeight;
        //var messages = ( history.Count - panelHeight is > 0 and { } skipCount ? history.Skip(skipCount) : history )
        //    .Take(panelHeight);
        foreach(var msg in history)
        {
            _ = table.AddRow([
                msg.Role == ChatRole.Assistant
                    ? new Text("assistant", _assistantStyle)
                    : msg.Role == ChatRole.User
                    ? new Text("user", _userStyle)
                    : msg.Role == ChatRole.System
                    ? new Text("system", _systemStyle)
                    : msg.Role == ChatRole.Tool
                    ? new Text("tool", _toolStyle)
                    : new Text("unknown"),
                    new Text(msg.Text ?? msg.Contents.Aggregate(
                        new StringBuilder(),
                        (sb,c)=>
                        {
                            var stringRepresentation = c switch{
                                FunctionCallContent call=>$"{call.Name}({String.Join(", ", call.Arguments?.Select(kvp=>$"{kvp.Key}: {kvp.Value}")??[])})",
                                FunctionResultContent result=>result.Result?.ToString(),
                                _=>c.RawRepresentation?.ToString()
                            };

                            _ = sb.Length > 0 ? sb.AppendLine(stringRepresentation) : sb.Append(stringRepresentation);

                            return sb;
                        }).ToString())]);
        }

        AnsiConsole.Clear();
        AnsiConsole.Write(table);
    }

    private static void PrintHints() => AnsiConsole.Write(new Text("/r - resize\t/q - quit\n", new Style(Color.Grey)));

    [Description("Gets a random integer that is bound to an exclusive upper bound that must be less than 100.")]
    static Int32 GetRandomInteger([Description("The exclusive upper bound of the random number to be generated. max must be greater than or equal to 0 and less than 100.")] Int32 max)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 100);
        return Random.Shared.Next(max);
    }
    [Description("Opens the users browser at the url provided.")]
    static void OpenBrowser([Description("The url to open the browser at.")] String url) => Process.Start(@"C:\Program Files\Mozilla Firefox\firefox.exe", url);

    [Description("Executes a terminal command locally on the User machine and returns the output as a string, after the command has finished executing.")]
    static async Task<String> ExecuteCommand([Description("The command to execute.")] String command, [Description("The arguments to supply to the command.")] String arguments)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        _ = proc.Start();

        var result = await proc.StandardOutput.ReadToEndAsync();

        return result;
    }
}

file static class Extensions
{
    public static async ValueTask<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken cancellationToken = default)
    {
        var result = new List<T>();
        await foreach(var item in source.WithCancellation(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
        {
            result.Add(item);
        }

        return result;
    }
}