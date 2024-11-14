namespace AiAssistant; 
using System.ComponentModel.DataAnnotations;

public class FunctionEntity
{
    [Key]
    [Required]
    public String Name { get; set; }

    [Required]
    public String SourceCode { get; set; }

    [Required]
    public String UsingsStatements { get; set; }
}
