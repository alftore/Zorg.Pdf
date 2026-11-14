using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Zorg.Pdf.Tool;

/// <summary>A parsed command line. Options take <c>--name value</c> or <c>--name=value</c>.</summary>
internal sealed class CliArguments
{
    private static readonly string[] Commands = ["render", "validate", "watch", "preview", "info", "merge", "extract", "split", "schema"];

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

    public PdfArchive Archive { get; private set; }

    public bool WarningsAsErrors { get; private set; }

    public bool Json { get; private set; }

    public bool Verbose { get; private set; }

    /// <summary>Also draw pages as PNG (render and watch), or the page to draw: 1-based, 0 for every page.</summary>
    public bool Preview { get; private set; }

    public int Page { get; private set; } = 1;

    public double Dpi { get; private set; } = 96;

    /// <summary>merge: the files, each with the pages to take ("a.pdf:1-3"), null for all.</summary>
    public List<(string File, string? Pages)> Inputs { get; } = [];

    /// <summary>extract: the pages to take.</summary>
    public string? Pages { get; private set; }

    /// <summary>merge and extract: new metadata, null to keep the first file's.</summary>
    public string? Title { get; private set; }

    public string? Author { get; private set; }

    public string? Subject { get; private set; }

    public string? Keywords { get; private set; }

    public string? Language { get; private set; }

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

            bool takesValue = name is "-o" or "--output" or "-d" or "--data" or "--image" or "--fonts" or "--font" or "--default-font" or "--page" or "--dpi" or "--pdfa" or "--pages" or "--title" or "--author" or "--subject" or "--keywords" or "--lang";
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
                case "--pdfa":
                    PdfArchive? archive = value switch { "2a" => PdfArchive.PdfA2a, "2u" => PdfArchive.PdfA2u, "2b" => PdfArchive.PdfA2b, _ => null };
                    if (archive is null)
                    {
                        error = $"--pdfa takes 2a, 2u or 2b, not \"{value}\".";
                        return false;
                    }

                    result.Archive = archive.Value;
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
                case "--preview":
                    result.Preview = true;
                    break;
                case "--pages":
                    result.Pages = value;
                    break;
                case "--title":
                    result.Title = value;
                    break;
                case "--author":
                    result.Author = value;
                    break;
                case "--subject":
                    result.Subject = value;
                    break;
                case "--keywords":
                    result.Keywords = value;
                    break;
                case "--lang":
                    result.Language = value;
                    break;
                case "--page":
                    if (value == "all")
                    {
                        result.Page = 0;
                    }
                    else if (int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page) && page >= 1)
                    {
                        result.Page = page;
                    }
                    else
                    {
                        error = $"--page takes a page number from 1, or all, not \"{value}\".";
                        return false;
                    }

                    break;
                case "--dpi":
                    if (double.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out double dpi) && dpi is >= 18 and <= 600)
                    {
                        result.Dpi = dpi;
                    }
                    else
                    {
                        error = $"--dpi takes a resolution from 18 to 600, not \"{value}\".";
                        return false;
                    }

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
            error = $"Unknown command \"{result.Command}\". The commands are render, validate, watch, preview, info, merge, extract, split and schema.";
            return false;
        }

        if (result.Command == "merge")
        {
            if (positional.Count < 2 || result.Output is null)
            {
                error = "merge needs files and --output: zorg-pdf merge a.pdf b.pdf:2-4 -o out.pdf.";
                return false;
            }

            foreach (string input in positional.Skip(1))
            {
                // "file.pdf:1-3" takes some pages; a colon in a Windows drive letter isn't a range.
                int colon = input.LastIndexOf(':');
                bool range = colon > 1 && input[(colon + 1)..].Length > 0 && input[(colon + 1)..].All(c => char.IsAsciiDigit(c) || c is ',' or '-' or ' ');
                result.Inputs.Add(range ? (input[..colon], input[(colon + 1)..]) : (input, null));
            }

            positional.RemoveRange(2, positional.Count - 2);
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
            if (result.Input == "-" && result.Output is null && result.Command is not ("validate" or "preview" or "info"))
            {
                error = "A document read from standard input needs --output (a file, or - for standard output).";
                return false;
            }
        }

        // Options that do nothing for the command are mistakes worth naming.
        string[] allowed = result.Command switch
        {
            "schema" => [],
            "info" => ["--json"],
            "merge" or "extract" => ["-o", "--output", "--pages", "--title", "--author", "--subject", "--keywords", "--lang", "--json"],
            "split" => ["-o", "--output", "--json"],
            "validate" => ["-d", "--data", "--image", "--warnaserror", "--json", "-v", "--verbose"],
            _ => [],
        };
        if (result.Command == "preview")
        {
            string? wrong = options.Find(o => o is "--preview" or "--warnaserror");
            if (wrong is not null)
            {
                error = $"{wrong} doesn't apply to preview.";
                return false;
            }
        }
        else if (result.Command == "validate" && options.Exists(o => o is "--page" or "--dpi" or "--preview"))
        {
            error = $"{options.Find(o => o is "--page" or "--dpi" or "--preview")} doesn't apply to validate.";
            return false;
        }

        if (result.Command is not ("render" or "watch" or "preview"))
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
