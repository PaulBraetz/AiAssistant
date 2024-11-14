namespace AiAssistant;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

sealed class Settings
{
    public required String PromptsCachePath { get; set; }
    public required String FunctionsStorePath { get; set; }
    public HashSet<String> UncacheablePrompts { get; set; } = ["y", "n"];
}
