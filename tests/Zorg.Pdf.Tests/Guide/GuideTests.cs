using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Zorg.Pdf.Tests.Model;

namespace Zorg.Pdf.Tests.Guide;

/// <summary>The guide's code can't drift from the API: C# blocks must be in <see cref="GuideSnippets"/>, JSON blocks must parse.</summary>
public sealed partial class GuideTests
{
    public static TheoryData<string> Pages() =>
        [.. Directory.GetFiles(RepoFiles.Path("docs", "guide"), "*.md").Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryCSharpBlockIsCompiledInGuideSnippets(string page)
    {
        string snippets = string.Join('\n', Directory.GetFiles(RepoFiles.Path("tests", "Zorg.Pdf.Tests", "Guide"), "GuideSnippets*.cs").Order(StringComparer.Ordinal).Select(path => Normalize(File.ReadAllText(path))));
        foreach (Match block in Block().Matches(File.ReadAllText(RepoFiles.Path("docs", "guide", page))).Where(m => m.Groups[1].Value == "csharp"))
        {
            string code = Normalize(block.Groups[2].Value);
            Assert.True(snippets.Contains(code, StringComparison.Ordinal), $"{page}: this block isn't in GuideSnippets.cs:\n{code}");
        }
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryWholeJsonDocumentParses(string page)
    {
        foreach (Match block in Block().Matches(File.ReadAllText(RepoFiles.Path("docs", "guide", page))).Where(m => m.Groups[1].Value == "json"))
        {
            string json = block.Groups[2].Value;
            if (json.Contains("\"$schema\"", StringComparison.Ordinal))
            {
                Assert.Empty(PdfDocument.ParseJson(json).Validate().Diagnostics);
            }
        }
    }

    // Indentation differs between the page and the class; the lines must match in order.
    private static string Normalize(string code) =>
        string.Join('\n', code.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    [GeneratedRegex(@"```(\w+)\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Block();
}
