using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Imaging;
using Zorg.Pdf.Markdown;

namespace Zorg.Pdf.Tool;

/// <summary>Where the tool reads and writes: the real console, or writers a test can read back.</summary>
/// <param name="Out">Results.</param>
/// <param name="Error">Messages, and results too when the PDF itself goes to standard output.</param>
/// <param name="OpenInput">Standard input, for a document read from <c>-</c>.</param>
/// <param name="OpenOutput">Standard output as bytes, for a PDF written to <c>-</c>.</param>
internal sealed record CliConsole(TextWriter Out, TextWriter Error, Func<Stream> OpenInput, Func<Stream> OpenOutput)
{
    public static CliConsole System { get; } = new(Console.Out, Console.Error, Console.OpenStandardInput, Console.OpenStandardOutput);
}

/// <summary>The <c>zorg-pdf</c> commands: render, validate, watch and schema.</summary>
internal static class Cli
{
    /// <summary>Done, with nothing worse than warnings (unless <c>--warnaserror</c>).</summary>
    public const int Ok = 0;

    /// <summary>The document or its data has problems, or the render failed.</summary>
    public const int Problems = 1;

    /// <summary>The command line is wrong, or a file couldn't be read or written.</summary>
    public const int Usage = 2;

    /// <summary>How long watch waits after a change for more changes, so one save renders once.</summary>
    internal static TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Families tried, in order, when installed fonts are used and nothing names a default.</summary>
    private static readonly string[] DefaultFamilies = ["Arial", "Liberation Sans", "DejaVu Sans", "Noto Sans", "Segoe UI"];

    internal const string Help = """
        zorg-pdf: render Zorg.Pdf JSON documents, or Markdown, to tagged PDF.

        Usage:
          zorg-pdf render <document.json> [options]     Render to PDF
          zorg-pdf validate <document.json> [options]   Check a document and its data without rendering
          zorg-pdf watch <document.json> [options]      Render, then render again on every change
          zorg-pdf preview <document.json|file.pdf>     Draw a page as PNG, to check the layout
          zorg-pdf info <file.pdf>                      Show any PDF's pages, sizes and metadata
          zorg-pdf schema                               Print the document JSON Schema

        Options:
          -o, --output <file.pdf>    Where the PDF goes; "-" for standard output. Default: the document's name with .pdf
          -d, --data <data.json>     Table rows, images and the template model
              --image <slot=file>    An image, or a PDF for a background, for a data slot; repeatable
              --fonts <folder>       Use the TrueType fonts in a folder; repeatable
              --font <file.ttf>      Use a font file; repeatable
              --default-font <name>  The family for text whose style names none
              --system-fonts         Also use installed fonts. On when no --fonts or --font is given
              --pdfua                Claim PDF/UA-1 and treat accessibility warnings as errors
              --pdfa <2a|2u|2b>      Claim PDF/A-2 for archiving (sRGB output intent, no CMYK images)
              --warnaserror          Exit with 1 when there are warnings
              --json                 Print results as JSON, one object per line
              --preview              With render and watch: also draw pages as PNG beside the PDF
              --page <n|all>             The page to draw, from 1 (default 1), or all
              --dpi <n>                  The PNG's resolution, 18 to 600 (default 96)
          -v, --verbose              Also print information, such as images that were shrunk
          -h, --help                 Show this help
              --version              Show the version

        A document ending in .md or .markdown is read as Markdown; its images are read from beside it, or
        given with --image. A document of "-" is read from standard input. Exit codes: 0 done, 1 problems in the document or its
        data, 2 a wrong command line or a file that couldn't be read or written.
        """;

    /// <summary>Runs one command line.</summary>
    public static async Task<int> RunAsync(string[] args, CliConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        if (!CliArguments.TryParse(args, out CliArguments? parsed, out string? error))
        {
            await console.Error.WriteLineAsync($"zorg-pdf: {error}{Environment.NewLine}Run 'zorg-pdf --help' for the options.").ConfigureAwait(false);
            return Usage;
        }

        switch (parsed.Command)
        {
            case "help":
                await console.Out.WriteLineAsync(Help).ConfigureAwait(false);
                return Ok;
            case "version":
                await console.Out.WriteLineAsync(Version()).ConfigureAwait(false);
                return Ok;
            case "schema":
                await console.Out.WriteLineAsync(PdfJsonSchema.Current).ConfigureAwait(false);
                return Ok;
            case "validate":
                return await ValidateAsync(parsed, console, cancellationToken).ConfigureAwait(false);
            case "render":
                return await RenderAsync(parsed, console, cancellationToken).ConfigureAwait(false);
            case "preview":
                return await PreviewAsync(parsed, console, cancellationToken).ConfigureAwait(false);
            case "info":
                return await InfoAsync(parsed, console, cancellationToken).ConfigureAwait(false);
            default:
                return await WatchAsync(parsed, console, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Version() =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static async Task<int> ValidateAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken)
    {
        var report = new Report("validate", args);
        await LoadAsync(args, console, report, cancellationToken).ConfigureAwait(false);
        await report.PrintAsync(console.Out).ConfigureAwait(false);
        return report.ExitCode;
    }

    private static async Task<int> RenderAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken)
    {
        Report report = await RenderOnceAsync(args, console, cancellationToken).ConfigureAwait(false);
        await report.PrintAsync(args.Output == "-" ? console.Error : console.Out).ConfigureAwait(false);
        return report.ExitCode;
    }

    /// <summary>Pages, sizes, metadata and problems of any PDF; exit 1 when it can't be read.</summary>
    private static async Task<int> InfoAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken)
    {
        var report = new Report("info", args);
        byte[]? file = await ReadAsync(args.Input, console, report, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            await report.PrintAsync(console.Error).ConfigureAwait(false);
            return report.ExitCode;
        }

        PdfFileInfo info;
        try
        {
            info = PdfFileInfo.Read(file);
        }
        catch (PdfReadException ex)
        {
            if (args.Json)
            {
                await console.Out.WriteLineAsync(InfoJson(null, ex.Message)).ConfigureAwait(false);
            }
            else
            {
                await console.Error.WriteLineAsync($"{args.Input}: {ex.Message}").ConfigureAwait(false);
            }

            return Problems;
        }

        if (args.Json)
        {
            await console.Out.WriteLineAsync(InfoJson(info, null)).ConfigureAwait(false);
            return Ok;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{args.Input}: PDF {info.Version}, {info.PageCount} page{(info.PageCount == 1 ? "" : "s")}");
        text.Append(info.IsTagged ? ", tagged" : ", not tagged").Append(info.IsEncrypted ? ", encrypted" : "").AppendLine();
        foreach ((string label, string? value) in new[] { ("Title", info.Title), ("Author", info.Author), ("Subject", info.Subject), ("Keywords", info.Keywords), ("Language", info.Language), ("Creator", info.Creator), ("Producer", info.Producer) })
        {
            if (value is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {label + ":",-10} {value}").AppendLine();
            }
        }

        // Runs of pages with the same size, as "pages 1-3: 210 x 297 mm (595 x 842 pt)".
        for (int i = 0; i < info.Pages.Length;)
        {
            int j = i;
            while (j + 1 < info.Pages.Length && info.Pages[j + 1] with { Number = 0 } == info.Pages[i] with { Number = 0 })
            {
                j++;
            }

            PdfPageInfo page = info.Pages[i];
            string pages = i == j ? $"page {i + 1}" : $"pages {i + 1}-{j + 1}";
            string rotation = page.Rotation == 0 ? "" : $", rotated {page.Rotation}°";
            text.Append(CultureInfo.InvariantCulture, $"  {pages}: {page.Width * 25.4 / 72:0} x {page.Height * 25.4 / 72:0} mm ({page.Width:0.##} x {page.Height:0.##} pt){rotation}").AppendLine();
            i = j + 1;
        }

        foreach (PdfDiagnostic diagnostic in info.Diagnostics)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {(diagnostic.Severity == DiagnosticSeverity.Warning ? "warning" : "info")} {diagnostic.Code}: {diagnostic.Message}").AppendLine();
        }

        await console.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        return Ok;
    }

    private static string InfoJson(PdfFileInfo? info, string? error)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("command", "info");
            json.WriteBoolean("ok", info is not null);
            if (error is not null)
            {
                json.WriteString("error", error);
            }

            if (info is not null)
            {
                json.WriteString("version", info.Version);
                json.WriteNumber("pageCount", info.PageCount);
                json.WriteBoolean("tagged", info.IsTagged);
                json.WriteBoolean("encrypted", info.IsEncrypted);
                foreach ((string name, string? value) in new[] { ("title", info.Title), ("author", info.Author), ("subject", info.Subject), ("keywords", info.Keywords), ("language", info.Language), ("creator", info.Creator), ("producer", info.Producer) })
                {
                    if (value is not null)
                    {
                        json.WriteString(name, value);
                    }
                }

                json.WriteStartArray("pages");
                foreach (PdfPageInfo page in info.Pages)
                {
                    json.WriteStartObject();
                    json.WriteNumber("number", page.Number);
                    json.WriteNumber("width", Math.Round(page.Width, 3));
                    json.WriteNumber("height", Math.Round(page.Height, 3));
                    json.WriteNumber("rotation", page.Rotation);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("diagnostics");
                foreach (PdfDiagnostic d in info.Diagnostics)
                {
                    json.WriteStartObject();
                    json.WriteString("code", d.Code);
                    json.WriteString("severity", d.Severity == DiagnosticSeverity.Warning ? "warning" : "info");
                    json.WriteString("message", d.Message);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<int> PreviewAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken)
    {
        Report report;
        byte[]? pdf;
        if (args.Input.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            report = new Report("preview", args);
            pdf = await ReadAsync(args.Input, console, report, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var memory = new MemoryStream();
            report = await RenderOnceAsync(args, console, cancellationToken, memory).ConfigureAwait(false);
            pdf = report.HasErrors ? null : memory.ToArray();
        }

        if (pdf is not null)
        {
            string target = args.Output ?? Path.ChangeExtension(args.Input == "-" ? "stdin" : args.Input, ".png");
            await WritePreviewsAsync(args, pdf, target, report, cancellationToken).ConfigureAwait(false);
        }

        await report.PrintAsync(console.Out).ConfigureAwait(false);
        return report.ExitCode;
    }

    /// <summary>Draws the pages asked for as PNG: <paramref name="target"/> for one page, name-N.png for each of several.</summary>
    private static async Task WritePreviewsAsync(CliArguments args, byte[] pdf, string target, Report report, CancellationToken cancellationToken)
    {
        try
        {
            int count = PdfPreview.PageCount(pdf);
            if (args.Page > count)
            {
                report.Fail(Usage, string.Create(CultureInfo.InvariantCulture, $"There's no page {args.Page}: the document has {count}."));
                return;
            }

            int[] pages = args.Page == 0 ? [.. Enumerable.Range(1, count)] : [args.Page];
            foreach (int page in pages)
            {
                string file = pages.Length == 1 ? target : Path.Combine(Path.GetDirectoryName(target) ?? "", string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(target)}-{page}{Path.GetExtension(target)}"));
                byte[] png = await PdfPreview.RenderPagePngAsync(pdf, page - 1, new PreviewOptions { Dpi = args.Dpi }, cancellationToken).ConfigureAwait(false);
                string folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
                Directory.CreateDirectory(folder);
                string temporary = Path.Combine(folder, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
                await File.WriteAllBytesAsync(temporary, png, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, file, overwrite: true);
                report.Previewed(file, page, count);
            }
        }
        catch (PdfPreviewException ex)
        {
            report.Fail(Problems, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Fail(Usage, $"Can't write the preview: {ex.Message}");
        }
    }

    /// <summary>Reads, checks and renders once, collecting everything that happened in a report.</summary>
    private static async Task<Report> RenderOnceAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken, MemoryStream? memory = null)
    {
        var report = new Report(memory is null ? "render" : "preview", args);
        long started = Stopwatch.GetTimestamp();
        (PdfDocument? document, PdfData data) = await LoadAsync(args, console, report, cancellationToken).ConfigureAwait(false);
        if (document is null || report.HasErrors)
        {
            return report;
        }

        (FontCollection fonts, IFontResolver? resolver) = await FontsAsync(args, report, cancellationToken).ConfigureAwait(false);
        if (report.HasErrors)
        {
            return report;
        }

        var options = new RenderOptions
        {
            Fonts = fonts,
            FontResolver = resolver,
            Data = data,
            Conformance = args.PdfUA ? PdfConformance.PdfUA1 : PdfConformance.None,
            Archive = args.Archive,
            ImageProcessor = new ImagingProcessor(),
            Hyphenator = Zorg.Pdf.Hyphenation.PatternHyphenator.Default, // Used where a style says hyphens: auto.
        };

        string output = memory is not null ? "memory" : args.Output ?? Path.ChangeExtension(args.Input, ".pdf");
        string? temporary = null;
        try
        {
            RenderResult result;
            if (memory is not null)
            {
                result = await document.RenderAsync(memory, options, cancellationToken).ConfigureAwait(false);
            }
            else if (output == "-")
            {
                Stream stdout = console.OpenOutput();
                await using (stdout.ConfigureAwait(false))
                {
                    result = await document.RenderAsync(stdout, options, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Written beside the target and moved over it, so a viewer never sees half a file, and a failed
                // render leaves the last good one in place.
                string folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
                Directory.CreateDirectory(folder);
                temporary = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
                FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                await using (file.ConfigureAwait(false))
                {
                    result = await document.RenderAsync(file, options, cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporary, output, overwrite: true);
                temporary = null;
            }

            report.Rendered(output, result, Stopwatch.GetElapsedTime(started));
            if (args.Preview && memory is null && output != "-")
            {
                await WritePreviewsAsync(args, await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false), Path.ChangeExtension(output, ".png"), report, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (PdfDocumentException ex)
        {
            report.Add(args.Input, ex.Diagnostics);
        }
        catch (PdfLimitException ex)
        {
            report.Fail(Problems, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Fail(Usage, $"Can't write {output}: {ex.Message} If a viewer has it open, close it there.");
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }

        return report;
    }

    /// <summary>The document, checked, and its data with the image files given; problems go to the report.</summary>
    private static async Task<(PdfDocument? Document, PdfData Data)> LoadAsync(CliArguments args, CliConsole console, Report report, CancellationToken cancellationToken)
    {
        byte[]? json = await ReadAsync(args.Input, console, report, cancellationToken).ConfigureAwait(false);
        PdfDocument? document = null;
        ImmutableArray<string> markdownImages = [];
        if (json is not null && IsMarkdown(args.Input))
        {
            MarkdownResult markdown = MarkdownDocument.Parse(Encoding.UTF8.GetString(json));
            report.Add(args.Input, markdown.Diagnostics);
            document = markdown.Document;
            markdownImages = markdown.Images;
            report.Add(args.Input, document.Validate().Diagnostics);
        }
        else if (json is not null)
        {
            DocumentParseResult parsed = PdfDocument.TryParseJson(json);
            report.Add(args.Input, parsed.Diagnostics);
            document = parsed.Document;
            if (document is not null)
            {
                report.Add(args.Input, document.Validate().Diagnostics);
            }
        }

        PdfData data = PdfData.Empty;
        if (args.Data is { } dataPath && await ReadAsync(dataPath, console, report, cancellationToken).ConfigureAwait(false) is { } dataJson)
        {
            try
            {
                data = PdfData.ParseJson(Encoding.UTF8.GetString(dataJson));
            }
            catch (PdfDocumentException ex)
            {
                report.Add(dataPath, ex.Diagnostics);
            }
        }

        foreach ((string slot, string path) in args.Images)
        {
            if (!File.Exists(path))
            {
                report.Fail(Usage, $"Can't read {path} for image slot \"{slot}\": the file doesn't exist.");
                continue;
            }

            data = data.WithImage(slot, async ct => await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        }

        // Markdown images are read from beside the file, unless --image gives them; never from outside its folder.
        foreach (string slot in markdownImages.Where(s => !args.Images.Any(i => i.Slot == s)))
        {
            if (MarkdownImage(args.Input, slot) is { } path)
            {
                data = data.WithImage(slot, async ct => await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
            }
        }

        return (document, data);
    }

    private static bool IsMarkdown(string input) =>
        input.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file for a Markdown image path, if it's a relative path to a file in the Markdown file's folder or below.</summary>
    private static string? MarkdownImage(string input, string slot)
    {
        if (input == "-" || slot.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(slot))
        {
            return null;
        }

        string folder = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".");
        string path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(slot)));
        string prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    private static async Task<byte[]?> ReadAsync(string path, CliConsole console, Report report, CancellationToken cancellationToken)
    {
        try
        {
            if (path == "-")
            {
                using var buffer = new MemoryStream();
                Stream stdin = console.OpenInput();
                await using (stdin.ConfigureAwait(false))
                {
                    await stdin.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                return buffer.ToArray();
            }

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Fail(Usage, ex is FileNotFoundException or DirectoryNotFoundException ? $"Can't read {path}: the file doesn't exist." : $"Can't read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The fonts given, read again on every render so watch sees new files. Installed fonts are used when asked for
    /// or when no fonts are given, with the first of a few common families as the default.
    /// </summary>
    private static async Task<(FontCollection Fonts, IFontResolver? Resolver)> FontsAsync(CliArguments args, Report report, CancellationToken cancellationToken)
    {
        FontCollection fonts = FontCollection.Empty;
        foreach (string file in args.FontFiles)
        {
            FontCollection one = File.Exists(file) ? FontCollection.FromFiles([file]) : FontCollection.Empty;
            if (one.Families.IsEmpty)
            {
                report.Fail(Usage, File.Exists(file)
                    ? $"Can't use {file}: it isn't a TrueType font or collection with a regular face (CFF fonts aren't supported yet)."
                    : $"Can't read {file}: the file doesn't exist.");
            }

            fonts = fonts.AddRange(one);
        }

        foreach (string folder in args.FontFolders)
        {
            if (!Directory.Exists(folder))
            {
                report.Fail(Usage, $"Can't read the font folder {folder}: it doesn't exist.");
                continue;
            }

            fonts = fonts.AddRange(FontCollection.FromFolder(folder));
        }

        bool system = args.SystemFonts || (args.FontFiles.Count == 0 && args.FontFolders.Count == 0);
        IFontResolver? resolver = system ? FontResolvers.System() : null;
        string? defaultFamily = args.DefaultFont ?? fonts.DefaultFamily;
        if (defaultFamily is null && resolver is not null)
        {
            foreach (string family in DefaultFamilies)
            {
                if (await resolver.ResolveAsync(new FontRequest(family, FontWeight.Normal, FontStyle.Normal), cancellationToken).ConfigureAwait(false) is not null)
                {
                    defaultFamily = family;
                    break;
                }
            }
        }

        if (defaultFamily is null)
        {
            report.Fail(Problems, fonts.Families.IsEmpty
                ? "No fonts to render with. Pass --fonts <folder> or --font <file.ttf>."
                : "No default font. Pass --default-font <family>.");
            return (fonts, resolver);
        }

        return (fonts.WithDefault(defaultFamily), resolver);
    }

    private static async Task<int> WatchAsync(CliArguments args, CliConsole console, CancellationToken cancellationToken)
    {
        if (args.Input == "-" || args.Output == "-")
        {
            await console.Error.WriteLineAsync("zorg-pdf: watch reads and writes files, not standard input or output.").ConfigureAwait(false);
            return Usage;
        }

        StringComparer comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new HashSet<string>(comparer) { Path.GetFullPath(args.Input) };
        if (args.Data is { } data)
        {
            files.Add(Path.GetFullPath(data));
        }

        files.UnionWith(args.Images.Select(i => Path.GetFullPath(i.Path)));
        files.UnionWith(args.FontFiles.Select(Path.GetFullPath));
        string[] folders = [.. args.FontFolders.Where(Directory.Exists).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)) + Path.DirectorySeparatorChar)];

        using var changed = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();
        try
        {
            void OnChange(string path)
            {
                if (files.Contains(path) || folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                {
                    changed.Release();
                }
            }

            IEnumerable<(string Folder, bool Recursive)> roots = files
                .Select(f => (Path.GetDirectoryName(f)!, false))
                .Concat(folders.Select(f => (f, true)))
                .Distinct();
            foreach ((string folder, bool recursive) in roots)
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += (_, e) => OnChange(e.FullPath);
                watcher.Created += (_, e) => OnChange(e.FullPath);
                watcher.Renamed += (_, e) => OnChange(e.FullPath); // Editors that save by renaming a temporary file.
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }

            await (await RenderOnceAsync(args, console, cancellationToken).ConfigureAwait(false)).PrintAsync(console.Out).ConfigureAwait(false);
            if (!args.Json)
            {
                await console.Out.WriteLineAsync($"Watching {files.Count + folders.Length} {(files.Count + folders.Length == 1 ? "file" : "files and folders")}. Press Ctrl+C to stop.").ConfigureAwait(false);
            }

            while (true)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                while (changed.Wait(0, CancellationToken.None))
                {
                    // One render for every change that arrived while waiting.
                }

                await (await RenderOnceAsync(args, console, cancellationToken).ConfigureAwait(false)).PrintAsync(console.Out, DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Ok;
        }
        finally
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; it's hidden and named .tmp.
        }
    }

    /// <summary>Core's message for a missing data slot names the .NET API; this says how to supply it here.</summary>
    private const string DataHint = " With zorg-pdf: --data <data.json> with a property of that name, or --image <slot=file> for an image.";

    /// <summary>What one command did: diagnostics by file, tool messages, and the PDF it wrote.</summary>
    private sealed class Report(string command, CliArguments args)
    {
        private readonly List<(string File, PdfDiagnostic Diagnostic)> _diagnostics = [];
        private readonly List<string> _errors = [];
        private int _failure = Ok;
        private readonly List<(string File, int Page, int Pages)> _previews = [];
        private (string File, RenderResult Result, TimeSpan Elapsed)? _rendered;

        public bool HasErrors => _failure != Ok || _diagnostics.Exists(d => d.Diagnostic.Severity == DiagnosticSeverity.Error);

        public int ExitCode =>
            _failure != Ok ? _failure
            : HasErrors || (args.WarningsAsErrors && _diagnostics.Exists(d => d.Diagnostic.Severity == DiagnosticSeverity.Warning)) ? Problems
            : Ok;

        public void Add(string file, ImmutableArray<PdfDiagnostic> diagnostics) =>
            _diagnostics.AddRange(diagnostics.Select(d => (Name(file), d.Code == "ZPDF-D001" ? d with { Message = d.Message + DataHint } : d)));

        public void Fail(int code, string message)
        {
            _failure = Math.Max(_failure, code);
            _errors.Add(message);
        }

        public void Previewed(string file, int page, int pages) => _previews.Add((file, page, pages));

        public void Rendered(string file, RenderResult result, TimeSpan elapsed)
        {
            Add(args.Input, result.Diagnostics);
            _rendered = (file, result, elapsed);
        }

        /// <summary>Writes the report as text lines (each after <paramref name="prefix"/>) or one JSON object.</summary>
        public async Task PrintAsync(TextWriter writer, string prefix = "")
        {
            IEnumerable<(string File, PdfDiagnostic Diagnostic)> shown = _diagnostics
                .Where(d => args.Verbose || d.Diagnostic.Severity != DiagnosticSeverity.Info);
            if (args.Json)
            {
                await writer.WriteLineAsync(Json(shown)).ConfigureAwait(false);
                return;
            }

            foreach ((string file, PdfDiagnostic d) in shown)
            {
                string where = d.Path.Length == 0 ? file : $"{file}:{d.Path}";
                string page = d.Page is { } p ? string.Create(CultureInfo.InvariantCulture, $" (page {p})") : "";
                await writer.WriteLineAsync($"{prefix}{where}: {Severity(d.Severity)} {d.Code}: {d.Message}{page}").ConfigureAwait(false);
            }

            foreach (string error in _errors)
            {
                await writer.WriteLineAsync($"{prefix}error: {error}").ConfigureAwait(false);
            }

            if (_rendered is ({ } output, { } result, TimeSpan elapsed) && output != "memory")
            {
                string where = output == "-" ? "standard output" : output;
                await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{prefix}{where}: {result.PageCount} {(result.PageCount == 1 ? "page" : "pages")}, {Size(result.BytesWritten)}, {elapsed.TotalMilliseconds:0} ms")).ConfigureAwait(false);
            }
            foreach ((string file, int page, int pages) in _previews)
            {
                await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{prefix}{file}: page {page} of {pages}")).ConfigureAwait(false);
            }

            if (command == "validate" && ExitCode == Ok)
            {
                await writer.WriteLineAsync($"{Name(args.Input)}: valid").ConfigureAwait(false);
            }
        }

        private string Json(IEnumerable<(string File, PdfDiagnostic Diagnostic)> shown)
        {
            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                json.WriteString("command", command);
                json.WriteBoolean("ok", ExitCode == Ok);
                if (_rendered is ({ } output, { } result, TimeSpan elapsed))
                {
                    json.WriteString("file", output);
                    json.WriteNumber("pages", result.PageCount);
                    json.WriteNumber("bytes", result.BytesWritten);
                    json.WriteNumber("milliseconds", (long)elapsed.TotalMilliseconds);
                }

                json.WriteStartArray("previews");
                foreach ((string file, int page, _) in _previews)
                {
                    json.WriteStartObject();
                    json.WriteString("file", file);
                    json.WriteNumber("page", page);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("diagnostics");
                foreach ((string file, PdfDiagnostic d) in shown)
                {
                    json.WriteStartObject();
                    json.WriteString("file", file);
                    json.WriteString("code", d.Code);
                    json.WriteString("severity", Severity(d.Severity));
                    json.WriteString("path", d.Path);
                    if (d.Page is { } page)
                    {
                        json.WriteNumber("page", page);
                    }

                    json.WriteString("message", d.Message);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("errors");
                foreach (string error in _errors)
                {
                    json.WriteStringValue(error);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string Name(string file) => file == "-" ? "stdin" : file;

        private static string Severity(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };

        private static string Size(long bytes) => bytes < 1 << 20
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.0} MB");
    }
}
