using CommandLine;

namespace PiwoConverter.Console;

public class Options
{
    [Option('i', 
        "input", 
        Required = true, 
        HelpText = "Path to the input mp4 file.")]
    public required string InputPath { get; set; }
}