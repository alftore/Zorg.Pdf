using System;
using System.IO;

namespace Zorg.Pdf.Tests.Model;

/// <summary>Files in the repository, found by walking up from the test binaries to the solution file.</summary>
internal static class RepoFiles
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Zorg.Pdf.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find Zorg.Pdf.slnx above the test binaries.");
    });

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root.Value, .. parts]);
}
