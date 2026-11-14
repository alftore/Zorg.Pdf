using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Pdf.Tool;

/// <summary>A parsed command line. Options take <c>--name value</c> or <c>--name=value</c>.</summary>
internal sealed class CliArguments
{
    private static readonly string[] Commands = ["render", "validate", "watch", "schema"];

    /// <summary>render, validate, watch, schema, help or version.</summary>
    public string Command { get; private set; } = "help";

    /// <summary>The document file, or <c>-</c> for standard input.</summary>
    public string Input { get; private set; } = "";

    public string? Output { get; private set; }

    public string? Data { get; private set; }

    public List<(string Slot, string Path)> Images { get; } = [];

    public List<string> FontFolders { get; } = [];

    public List<string> FontFiles { get; } = [];

    public string? DefaultFont { get; private set; }

    public bool SystemFonts { get; private set; }

    public bool PdfUA { get; private set; }

    public bool WarningsAsErrors { get; private set; }

    public bool Json { get; private set; }

    public bool Verbose { get; private set; }

    public static bool TryParse(string[] args, [NotNullWhen(true)] out CliArguments? parsed, [NotNullWhen(false)] out string? error)
    {
        var result = new CliArguments();
        var positional = new List<string>();
        var options = new List<string>();
        parsed = null;
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "-" || !arg.StartsWith('-'))
            {
                positional.Add(arg);
                continue;
            }

            string name = arg;
            string? inline = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }

            bool takesValue = name is "-o" or "--output" or "-d" or "--data" or "--image" or "--fonts" or "--font" or "--default-font";
            string? value = inline;
            if (takesValue && value is null)
            {
                if (i + 1 >= args.Length)
                {
                    error = $"{name} needs a value.";
                    return false;
                }

                value = args[++i];
            }
            else if (!takesValue && inline is not null)
            {
                error = $"{name} doesn't take a value.";
                return false;
            }

            options.Add(name);
            switch (name)
            {
                case "-h" or "--help":
                    result.Command = "help";
                    parsed = result;
                    return true;
                case "--version":
                    result.Command = "version";
                    parsed = result;
                    return true;
                case "-o" or "--output":
                    result.Output = value;
                    break;
                case "-d" or "--data":
                    result.Data = value;
                    break;
                case "--image":
                    int split = value!.IndexOf('=', StringComparison.Ordinal);
                    if (split <= 0 || split == value.Length - 1)
                    {
                        error = $"--image takes slot=file, such as --image logo=logo.png, not \"{value}\".";
                        return false;
                    }

                    result.Images.Add((value[..split], value[(split + 1)..]));
                    break;
                case "--fonts":
                    result.FontFolders.Add(value!);
                    break;
                case "--font":
                    result.FontFiles.Add(value!);
                    break;
                case "--default-font":
                    result.DefaultFont = value;
                    break;
                case "--system-fonts":
                    result.SystemFonts = true;
                    break;
                case "--pdfua":
                    result.PdfUA = true;
                    break;
                case "--warnaserror":
                    result.WarningsAsErrors = true;
                    break;
                case "--json":
                    result.Json = true;
                    break;
                case "-v" or "--verbose":
                    result.Verbose = true;
                    break;
                default:
                    error = $"Unknown option {name}.";
                    return false;
            }
        }

        if (positional.Count == 0)
        {
            parsed = result; // No command: help.
            return true;
        }

        result.Command = positional[0];
        if (Array.IndexOf(Commands, result.Command) < 0)
        {
            error = $"Unknown command \"{result.Command}\". The commands are render, validate, watch and schema.";
            return false;
        }

        int expected = result.Command == "schema" ? 1 : 2;
        if (positional.Count != expected)
        {
            error = positional.Count < expected
                ? $"{result.Command} needs a document: zorg-pdf {result.Command} <document.json>."
                : $"Unexpected argument \"{positional[expected]}\".";
            return false;
        }

        if (expected == 2)
        {
            result.Input = positional[1];
            if (result.Input == "-" && result.Output is null && result.Command != "validate")
            {
                error = "A document read from standard input needs --output (a file, or - for standard output).";
                return false;
            }
        }

        // Options that do nothing for the command are mistakes worth naming.
        string[] allowed = result.Command switch
        {
            "schema" => [],
            "validate" => ["-d", "--data", "--image", "--warnaserror", "--json", "-v", "--verbose"],
            _ => [],
        };
        if (result.Command != "render" && result.Command != "watch")
        {
            string? wrong = options.Find(o => Array.IndexOf(allowed, o) < 0);
            if (wrong is not null)
            {
                error = $"{wrong} doesn't apply to {result.Command}.";
                return false;
            }
        }

        parsed = result;
        return true;
    }
}
