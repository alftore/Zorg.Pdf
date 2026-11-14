using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Pdf.Fonts;
using Zorg.Pdf.Tests.Fonts;
using Zorg.Pdf.Tests.Images;
using Zorg.Pdf.Tests.Model;
using Zorg.Pdf.Tests.Writing;
using Zorg.Pdf.Tool;

namespace Zorg.Pdf.Tests.Tool;

/// <summary>The zorg-pdf command-line tool, run in-process against files in a temporary folder.</summary>
public sealed class CliTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("zorg-pdf-cli-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task RenderWritesThePdfBesideTheDocumentAndSaysWhatItDid()
    {
        string document = Copy("invoice.json");

        (int code, string output, _) = await RunAsync("render", document, "--data", RepoFiles.Path("docs", "examples", "data", "invoice.json"), "--font", TestFonts.RequirePath(), "--pdfua");

        Assert.Equal(Cli.Ok, code);
        string pdf = Path.ChangeExtension(document, ".pdf");
        PdfFile.CheckStructure(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken));
        Assert.Matches(@": \d+ pages?, \d+\.\d KB, \d+ ms", output);
        Assert.Equal(["invoice.json", "invoice.pdf"], Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // No temporary file left.
    }

    [Fact]
    public async Task ProblemsArePrintedWithTheFileAndJsonPointer()
    {
        string document = Write("broken.json", """{ "title": "x", "content": [{ "type": "paragraph", "inlines": ["ok"] }, { "type": "paragrph" }] }""");

        (int code, string output, _) = await RunAsync("validate", document);
        (int jsonCode, string json, _) = await RunAsync("validate", document, "--json");
        (int renderCode, _, _) = await RunAsync("render", document, "--font", TestFonts.RequirePath());

        Assert.Equal(Cli.Problems, code);
        Assert.StartsWith($"{document}:/content/1: error ZPDF-J005: Unknown block type \"paragrph\".", output, StringComparison.Ordinal);
        Assert.Equal(Cli.Problems, jsonCode);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("/content/1", parsed.RootElement.GetProperty("diagnostics")[0].GetProperty("path").GetString());
        Assert.Equal(Cli.Problems, renderCode);
        Assert.False(File.Exists(Path.ChangeExtension(document, ".pdf")));
    }

    [Fact]
    public async Task WarningsPassUnlessWarningsAreErrors()
    {
        string document = Write("template.json", """{ "title": "x", "content": [{ "type": "paragraph", "inlines": ["Dear ", { "bind": "customer.name" }] }] }""");
        string data = Write("data.json", """{ "customer": {} }""");
        string font = TestFonts.RequirePath();

        (int code, string output, _) = await RunAsync("render", document, "-d", data, "--font", font);
        (int strict, _, _) = await RunAsync("render", document, "-d", data, "--font", font, "--warnaserror");

        Assert.Equal(Cli.Ok, code);
        Assert.Contains($"{document}:/content/0/inlines/1/bind: warning ZPDF-B001", output, StringComparison.Ordinal);
        Assert.Equal(Cli.Problems, strict);
    }

    [Fact]
    public async Task AMissingDataSlotSaysHowToSupplyItHere()
    {
        (int code, string output, _) = await RunAsync("render", Copy("invoice.json"), "--font", TestFonts.RequirePath());

        Assert.Equal(Cli.Problems, code);
        Assert.Contains("ZPDF-D001", output, StringComparison.Ordinal);
        Assert.Contains("--data <data.json>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImagesComeFromFilesNamedOnTheCommandLine()
    {
        string document = Write("logo.json", """{ "title": "Logo", "content": [{ "type": "image", "source": { "$data": "logo" }, "alt": "Logo" }] }""");
        string png = Path.Combine(_folder, "logo.png");
        await File.WriteAllBytesAsync(png, TestImages.Png(8, 8, 2, 8, (x, y) => [x * 30, y * 30, 0]), TestContext.Current.CancellationToken);

        (int code, string output, _) = await RunAsync("render", document, "--image", $"logo={png}", "--font", TestFonts.RequirePath(), "-o", Path.Combine(_folder, "out", "logo.pdf"));

        Assert.True(code == Cli.Ok, output);
        Assert.Contains("/Subtype /Image", PdfFile.Text(await File.ReadAllBytesAsync(Path.Combine(_folder, "out", "logo.pdf"), TestContext.Current.CancellationToken)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentsComeFromStandardInputAndPdfsGoToStandardOutput()
    {
        byte[] json = await File.ReadAllBytesAsync(RepoFiles.Path("docs", "examples", "letter.json"), TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var console = new CliConsole(stdout, stderr, () => new MemoryStream(json), () => new NonClosingStream(output));

        int code = await Cli.RunAsync(["render", "-", "-o", "-", "--font", TestFonts.RequirePath()], console, TestContext.Current.CancellationToken);

        Assert.True(code == Cli.Ok, stderr.ToString());
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(output.ToArray(), 0, 5), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString()); // The PDF has standard output to itself; the summary went to standard error.
        Assert.StartsWith("standard output: 1 page", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("render")]
    [InlineData("render", "a.json", "b.json")]
    [InlineData("frobnicate")]
    [InlineData("schema", "--json")]
    [InlineData("validate", "a.json", "-o", "a.pdf")]
    [InlineData("render", "a.json", "--image", "logo")]
    [InlineData("render", "a.json", "--output")]
    [InlineData("render", "a.json", "--pdfua=yes")]
    [InlineData("render", "-")]
    [InlineData("watch", "-", "-o", "x.pdf")]
    public async Task WrongCommandLinesExitWithTwo(params string[] args)
    {
        (int code, string output, string error) = await RunAsync(args);

        Assert.Equal(Cli.Usage, code);
        Assert.Empty(output);
        Assert.StartsWith("zorg-pdf: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesThatCantBeReadExitWithTwo()
    {
        (int code, string output, _) = await RunAsync("render", Path.Combine(_folder, "missing.json"));
        (int fonts, string fontsOutput, _) = await RunAsync("render", Copy("letter.json"), "--fonts", Path.Combine(_folder, "nowhere"));

        Assert.Equal(Cli.Usage, code);
        Assert.Contains("missing.json: the file doesn't exist.", output, StringComparison.Ordinal);
        Assert.Equal(Cli.Usage, fonts);
        Assert.Contains("font folder", fontsOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFontFolderWithoutFontsSaysSo()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "fonts"));

        (int code, string output, _) = await RunAsync("render", Copy("letter.json"), "--fonts", Path.Combine(_folder, "fonts"));

        Assert.Equal(Cli.Problems, code);
        Assert.Contains("No fonts to render with", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpVersionAndSchemaPrintAndExitWithZero()
    {
        (int help, string helpText, _) = await RunAsync();
        (int version, string versionText, _) = await RunAsync("--version");
        (int schema, string schemaText, _) = await RunAsync("schema");

        Assert.Equal((Cli.Ok, Cli.Ok, Cli.Ok), (help, version, schema));
        Assert.Contains("zorg-pdf render <document.json>", helpText, StringComparison.Ordinal);
        Assert.Matches(@"^\d+\.\d+\.\d+", versionText);
        using JsonDocument parsed = JsonDocument.Parse(schemaText);
        Assert.Equal(PdfJsonSchema.Id, parsed.RootElement.GetProperty("$id").GetString());
    }

    [Fact]
    public async Task WatchRendersAgainAfterEachSave()
    {
        string document = Copy("letter.json");
        string pdf = Path.ChangeExtension(document, ".pdf");
        var output = new LockedWriter();
        var console = new CliConsole(output, output, () => Stream.Null, () => Stream.Null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Cli.Debounce = TimeSpan.FromMilliseconds(50);

        Task<int> watch = Cli.RunAsync(["watch", document, "--font", TestFonts.RequirePath()], console, stop.Token);
        await Until(() => output.ToString().Contains("Watching", StringComparison.Ordinal));
        byte[] first = await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(document, (await File.ReadAllTextAsync(document, TestContext.Current.CancellationToken)).Replace("\"title\": \"", "\"title\": \"Changed: ", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await Until(() => !File.ReadAllBytes(pdf).AsSpan().SequenceEqual(first));
        await File.WriteAllTextAsync(document, "{ broken", TestContext.Current.CancellationToken);
        await Until(() => output.ToString().Contains("ZPDF-J003", StringComparison.Ordinal));

        await stop.CancelAsync();
        Assert.Equal(Cli.Ok, await watch);
        Assert.True(File.Exists(pdf)); // A broken save keeps the last good PDF.
    }

    [Fact]
    public void FontCollectionsCanBeReadFromFilesAndFolders()
    {
        string font = TestFonts.RequirePath();
        string fonts = Path.Combine(_folder, "fonts", "nested");
        Directory.CreateDirectory(fonts);
        File.Copy(font, Path.Combine(fonts, "body.ttf"));
        File.WriteAllText(Path.Combine(fonts, "notes.ttf"), "not a font");

        FontCollection fromFiles = FontCollection.FromFiles([font, Path.Combine(fonts, "notes.ttf")]);
        FontCollection fromFolder = FontCollection.FromFolder(Path.Combine(_folder, "fonts"));

        FontFamily family = Assert.Single(fromFiles.Families.Values);
        Assert.NotNull(family.Regular);
        Assert.Equal(family.Name, fromFiles.DefaultFamily);
        Assert.Equal(family.Name, Assert.Single(fromFolder.Families.Keys));
        Assert.Equal("Body", FontCollection.Empty.Add(new FontFamily("Body")).AddRange(fromFolder).DefaultFamily);
        Assert.Equal(family.Name, FontCollection.Empty.AddRange(fromFolder).DefaultFamily);
    }

    [Fact]
    public void FontFamiliesTakeTheFaceNearestRegularAndBoldWeight()
    {
        string[] arial = [@"C:\Windows\Fonts\arial.ttf", @"C:\Windows\Fonts\arialbd.ttf", @"C:\Windows\Fonts\ariblk.ttf", @"C:\Windows\Fonts\ariali.ttf"];
        string[] dejaVu = ["/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/dejavu/DejaVuSans-ExtraLight.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Oblique.ttf"];
        string[]? files = Array.TrueForAll(arial, File.Exists) ? arial : Array.TrueForAll(dejaVu, File.Exists) ? dejaVu : null;
        Assert.SkipWhen(files is null, "Needs Arial (Windows) or DejaVu Sans with ExtraLight (Linux).");

        // Some light and black faces name a family of their own; the regular file's family is the one checked.
        string name = Assert.Single(FontCollection.FromFiles([files[0]]).Families.Keys);
        FontFamily family = FontCollection.FromFiles(files.Reverse()).Families[name];

        Assert.Equal(FontSource.FromFile(Path.GetFullPath(files[0])), family.Regular); // Not ExtraLight.
        Assert.Equal(FontSource.FromFile(Path.GetFullPath(files[1])), family.Bold);    // Not Black.
        Assert.Equal(FontSource.FromFile(Path.GetFullPath(files[3])), family.Italic);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await Cli.RunAsync(args, new CliConsole(output, error, () => Stream.Null, () => Stream.Null), TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    private string Copy(string example)
    {
        string path = Path.Combine(_folder, example);
        File.Copy(RepoFiles.Path("docs", "examples", example), path);
        return path;
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out after 10 seconds.");
    }

    /// <summary>A writer the watch loop writes to while the test reads it.</summary>
    private sealed class LockedWriter : StringWriter
    {
        private readonly Lock _lock = new();

        public override Task WriteLineAsync(string? value)
        {
            lock (_lock)
            {
                base.WriteLine(value);
            }

            return Task.CompletedTask;
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return base.ToString();
            }
        }
    }

    /// <summary>Lets the test read the stream after the tool has disposed it.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }
}
