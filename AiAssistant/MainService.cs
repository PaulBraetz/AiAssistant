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
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;

class MainService : BackgroundService
{
    public MainService(
        IChatClient client,
        IVectorStore vectorStore,
        CacheablePromptFactory promptFactory,
        IHostApplicationLifetime lifetime,
        Functions functions,
        Settings settings)
    {
        _client = client;
        _vectorStore = vectorStore;
        _promptFactory = promptFactory;
        _lifetime = lifetime;
        _functions = functions;
        _settings = settings;

        _tools =
        [
            AIFunctionFactory.Create(GetRandomInteger),
            AIFunctionFactory.Create(ExecuteCommand),
            AIFunctionFactory.Create(OpenBrowser),
            AIFunctionFactory.Create(Beep),
            AIFunctionFactory.Create(AddFunction),
            AIFunctionFactory.Create(RemoveFunction),
            AIFunctionFactory.Create(TakeScreenshot),
            AIFunctionFactory.Create(AnalyzeLocalImage)
        ];
    }

    private readonly IChatClient _client;
    private readonly IVectorStore _vectorStore;
    private readonly CacheablePromptFactory _promptFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Functions _functions;
    private readonly Settings _settings;

    private static readonly Style _assistantStyle = new(Color.SeaGreen1_1);
    private static readonly Style _userStyle = new(Color.DarkOrange);
    private static readonly Style _systemStyle = new(Color.Purple4_1);
    private static readonly Style _toolStyle = new(Color.BlueViolet);
    private static readonly Style _mutedStyle = new(Color.Grey);

    //private readonly Queue<ChatMessage> _queuedMessages = [];

    private readonly List<AITool> _tools;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var promptsCache = _vectorStore.GetCollection<String, CacheablePrompt>("prompts");
        await promptsCache.CreateCollectionIfNotExistsAsync(stoppingToken);

        var history = CreateHistory();
        var options = await CreateOptions(stoppingToken);

        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                //while(_queuedMessages.TryDequeue(out var msg))
                //{
                //    history.Add(msg);
                //    _ = await GetResponse(history, options, stoppingToken);
                //}

                Refresh(history);

                var promptStr = await PromptUserString("prompt:", history, stoppingToken);
                history.Add(new ChatMessage(ChatRole.User, promptStr));
                var prompt = await _promptFactory.Create(promptStr, stoppingToken);

                if(await PromptCache(prompt, promptsCache, history, stoppingToken))
                    continue;

                var response = await GetResponse(history, options, stoppingToken);

                prompt.Response = response.Message.Text ?? String.Empty;
                _ = await promptsCache.UpsertAsync(prompt, cancellationToken: stoppingToken);
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { } catch(Exception ex) when(PromptUserConfirmation($"ignore exception of type {ex.GetType()}?"))
            { }
        }
    }

    private static List<ChatMessage> CreateHistory()
    {
        return
        [
            new (
                ChatRole.System,
                """
                You are a helpful assistant that is required to absolutely follow all of the following rules; they take priority over any of the previous instructions:
                1. When executing any function whose description contains 'SAFETY-CRITICAL', make sure to verify the user wants to execute it by asking for verification. The user may grant one-time verification by confirming the assistants confirmation request by responding something like 'y', 'yes', 'do it' and similar. The user may grant perpetual verification by explicitly stating: "I grant perpetual permission to execute this function.". No other message may be used to grant perpetual permission. Do not request perpetual permission yourself. Only ever ask for one-time permission.
                2. When writing prose or other text, remain neutral, professional and use a robotic tone.
                3. When accessing files, never under any circumstance read, write or move files that are named 'appsettings.secrets.json'.
                """)
        ];
    }

    private async Task<ChatOptions> CreateOptions(CancellationToken stoppingToken)
    {
        _ = await Task.WhenAll(
                    ( await _functions.GetAll(stoppingToken) )
                    .Select(f =>
                        AddFunctionCore(
                            f.SourceCode,
                            f.UsingsStatements,
                            (f, ct) =>
                            {
                                _tools.Add(AIFunctionFactory.Create(f, target: null));
                                return Task.FromResult(false);
                            },
                            stoppingToken)));
        var options = new ChatOptions() { Tools = _tools };
        return options;
    }

    private async Task<ChatCompletion> GetResponse(List<ChatMessage> history, ChatOptions options, CancellationToken ct)
    {
        var signal = new AutoResetEvent(initialState: false);
        var completeTask = Task.Run(() => _client.CompleteAsync(history, options, ct)
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

    private async Task<Boolean> PromptCache(CacheablePrompt prompt, IVectorStoreRecordCollection<String, CacheablePrompt> promptsCache, List<ChatMessage> history, CancellationToken ct)
    {
        if(_settings.UncacheablePrompts.Contains(prompt.Prompt))
            return false;

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
    private void PrintSettings()
    {
        var table = new Table()
        {
            Expand = true,
            Width = Int32.MaxValue
        }.AddColumns("setting", "value");

        var props = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach(var prop in props)
        {
            _ = table.AddRow(new Text(prop.Name, _mutedStyle), new Text(prop.GetValue(_settings)?.ToString() ?? String.Empty));
        }

        AnsiConsole.Clear();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine("press any key to return");
        _ = Console.ReadKey();
    }
    private async Task<String> PromptUserString(String prompt, List<ChatMessage> history, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = AnsiConsole.Prompt(new TextPrompt<String>($"[darkorange]{prompt}[/] "));
        switch(result)
        {
            case "/r":
                Refresh(history);
                return await PromptUserString(prompt, history, cancellationToken);
            case "/s":
                PrintSettings();
                Refresh(history);
                return await PromptUserString(prompt, history, cancellationToken);
            case "/q":
                _lifetime.StopApplication();
                await Task.Delay(500, cancellationToken);
                return await PromptUserString(prompt, history, cancellationToken);
            default:
                return result;
        }
    }

    private static void Refresh(List<ChatMessage> history)
    {
        PrintTable(history);
        PrintHints();
    }

    private static void PrintTable(List<ChatMessage> history)
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
            const Single blendFactor = 0.75f;
            var blendColor = Color.Black;
            var (role, bgColor) = msg.Role == ChatRole.Assistant
                ? (new Text("assistant", _assistantStyle), _assistantStyle.Foreground.Blend(blendColor, blendFactor))
                : msg.Role == ChatRole.User
                ? (new Text("user", _userStyle), _userStyle.Foreground.Blend(blendColor, blendFactor))
                : msg.Role == ChatRole.System
                ? (new Text("system", _systemStyle), _systemStyle.Foreground.Blend(blendColor, blendFactor))
                : msg.Role == ChatRole.Tool
                ? (new Text("tool", _toolStyle), _toolStyle.Foreground.Blend(blendColor, blendFactor))
                : (new Text("unknown"), Color.Default);
            _ = table.AddRow([
                    role,
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
                        }).ToString(), new Style(background:bgColor))]);
        }

        AnsiConsole.Clear();
        AnsiConsole.Write(table);
    }

    private static void PrintHints() => AnsiConsole.Write(new Text("/r - resize\t/q - quit\t/s - settings\n", _mutedStyle));

    [Description("Gets a random integer that is bound to an exclusive upper bound that must be less than 100.")]
    static Int32 GetRandomInteger([Description("The exclusive upper bound of the random number to be generated. max must be greater than or equal to 0 and less than 100.")] Int32 max)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 100);
        return Random.Shared.Next(max);
    }
    [Description("Opens the users browser at the url provided.")]
    static void OpenBrowser([Description("The url to open the browser at. SAFETY-CRITICAL")] String url) => Process.Start(@"C:\Program Files\Mozilla Firefox\firefox.exe", url);
    [Description("Executes a terminal command locally on the User machine and returns the standard output as a string, after the command has finished executing. SAFETY-CRITICAL")]
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
    [Description("Lets the console beep with the provided times and frequencies.")]
    static void Beep(
        [Description("The frequencies to play and how long to play them. Elements must be two comma-separated items, " +
                     "the first the frequency in Hertz, the second the time to play the frequency in milliseconds. " +
                     "For pauses, use a zero Hertz frequency. Both values must be integers.")]
        String[] intervals)
    {
        for(var i = 0; i < intervals.Length; i++)
        {
            if(intervals[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [{ } freqStr, { } delStr]
             && Int32.TryParse(freqStr, out var freq)
             && Int32.TryParse(delStr, out var del))
            {
                Console.Beep(freq, del);
            }
        }
    }

    [Description("Generates and compiles a static method from provided C# source code." +
                 "The method will be made available as an AI function tool to the assistant." +
                 "It will be available via the name provided in the implementation." +
                 "Returns 'SUCCESS' if the compilation succeeds. If not, returns 'ERROR' followed by compilation errors." +
                 "SAFETY-CRITICAL")]
    public Task<String> AddFunction(
        [Description("A string containing the C# source code of the static function to be compiled. " +
                     "The function should be a valid public static method definition without a containing class, " +
                     "as it will be added to a generated static class. The source code inside may only use " +
                     "types found in the base dotnet 8 libraries. The code may not include any using statements." +
                     "The source code may use all the latest C# language features, as it is compiled using the latest flag." +
                     "All parameters must be annotated with a 'Description(string description)' attribute that explains in detail the parameter, its constraints and how it is used in the function." +
                     "The method must be annotated wih a 'Description(string description)' attribute that explains in detail the functionality and return value(s) of the function." +
                     "The source code will be compiled using release mode." +
                     "The function may return values and be asynchronous if required." +
                     "The function description MUST contain 'SAFETY-CRITICAL' if it is potentially dangerous or risky to use. Examples are functions that access system resources, compile and run code, or execute commands; anything that may compromise the security or safety of the users computer or person.")]
        String sourceCode,
        [Description("A string containing all the using statements required for the function to compile correctly. Any and all namespaces required must be imported here,as no implicit usings are provided.")]
        String usingStatements,
        CancellationToken ct) =>
        AddFunctionCore(sourceCode, usingStatements, async (f, ct) =>
        {
            var function = new FunctionEntity()
            {
                Name = f.Name,
                SourceCode = sourceCode,
                UsingsStatements = usingStatements
            };

            if(await _functions.Insert(function, ct))
            {
                _tools.Add(AIFunctionFactory.Create(f, target: null));
                return true;
            }

            return false;
        }, ct);
    [Description("Deletes a function from the list of tools available to the assistant." +
                 "SAFETY-CRITICAL")]
    async Task<String> RemoveFunction([Description("The name of the function to be deleted from the tools.")] String functionName, CancellationToken ct)
    {
        if(_tools.RemoveAll(t => t is AIFunction { } f && f.Metadata.Name == functionName) > 0
            && await _functions.Delete(functionName, ct))
        {
            return "SUCCESS";
        } else
        {
            return "FAILURE: A function with the name provided does not exist.";
        }
    }
    [Description("Takes a screenshot of the primary display, stores it in a temporary file and returns the path to that file." +
                 "SAFETY-CRITICAL")]
    static String TakeScreenshot()
    {
        var primary = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;
        var w = primary.Width;
        var h = primary.Height;

        var image = new System.Drawing.Bitmap(w, h);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.CopyFromScreen(0, 0, 0, 0, new(w, h));

        var result = Path.GetTempFileName();
        image.Save(result, System.Drawing.Imaging.ImageFormat.Png);

        return result;
    }
    [Description("Analyzes an image found at a local path. The analysis is done by an llm, the result of the completion request is returned by the function." +
                 "SAFETY-CRITICAL")]
    async Task<ChatCompletion> AnalyzeLocalImage(
        [Description("The path of the local png image file to analyze.")]
        String localPath,
        CancellationToken ct)
    {
        var imageBytes = await File.ReadAllBytesAsync(localPath, ct);
        var imageData = Convert.ToBase64String(imageBytes);
        var url = $"data:image/jpeg;base64,{imageData}";
        var response = await _client.CompleteAsync([
            new(ChatRole.System,
                """
                "You are an advanced language model trained to provide objective and detailed descriptions of visual content. Your task is to analyze the image presented to you and describe its contents precisely. Please ensure your description follows these guidelines:                          

                1. **Objectivity**: State only what is visible in the image without interpretation or assumptions.                                                                                                                                                                                      
                2. **Detail**: Include all relevant elements, such as objects, people, colors, and spatial arrangements.                                                                                                                                                                                
                3. **Clarity**: Use clear and concise language to ensure the description is easy to understand.                                                                                                                                                                                         
                4. **Neutrality**: Avoid subjective opinions or emotional language.                                                                                                                                                                                                                     
                5. **Comprehensiveness**: Cover all noticeable aspects of the image without omitting significant details.                                                                                                                                                                               

                Begin your description with a general overview before providing a more detailed breakdown of the components present in the image."               
                """),
            new(ChatRole.User,
            """
            Examine the following image.
            """)
            {
                Contents = [new ImageContent(url)]
            }], cancellationToken: ct);

        return response;
    }
    async Task<String> AddFunctionCore(
        String sourceCode,
        String usingStatements,
        Func<MethodInfo, CancellationToken, Task<Boolean>> insertEntity,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var code = $@"
        using System.ComponentModel;
        {usingStatements}                                                                                                                                                                                                                                                                
                                                                                                                                                                                                                                                                                     
        public static class GeneratedClass                                                                                                                                                                                                                                           
        {{                                                                                                                                                                                                                                                                           
            {sourceCode}                                                                                                                                                                                                                                                             
        }}";

        var syntaxTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: ct);

        ct.ThrowIfCancellationRequested();

        var compilation = CSharpCompilation.Create(
            assemblyName: null,
            syntaxTrees: new[] { syntaxTree },
            references: Basic.Reference.Assemblies.ReferenceAssemblies.Net80,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms, cancellationToken: ct);

        ct.ThrowIfCancellationRequested();

        if(result.Success)
        {
            _ = ms.Seek(0, SeekOrigin.Begin);
            var assembly = Assembly.Load(ms.ToArray());

            var type = assembly.GetType("GeneratedClass");

            if(type != null)
            {
                var methodInfo = type.GetMethods(BindingFlags.Static | BindingFlags.Public).Single();
                if(!await insertEntity.Invoke(methodInfo, ct))
                    return "ERROR: Another function with the same name already exists, try using a different name.";
            }

            return "SUCCESS";
        } else
        {
            var errors = "ERROR: " + String.Join(Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => diagnostic.ToString()));
            return errors;
        }
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