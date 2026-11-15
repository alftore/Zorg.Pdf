# Existing PDFs

Zorg.Pdf reads PDFs from any source, not only its own. The reader is built for hostile input: it handles damaged files by scanning for objects, bounds every allocation with `PdfReadLimits`, and is fuzzed in the repository. It reads files encrypted without an open password (RC4, AES-128 and AES-256). Files that need a password to open are refused.

## Page count and metadata

```csharp
PdfFileInfo info = await PdfFileInfo.ReadAsync(File.OpenRead("contract.pdf"), cancellationToken: cancellationToken);
Console.WriteLine($"{info.Title}: {info.PageCount} pages, the first {info.Pages[0].Width:0} × {info.Pages[0].Height:0} pt");
```

`PdfFileInfo` also has the author, subject, keywords, producer, language, PDF version, and whether the file is tagged or encrypted. Its `Diagnostics` note what was repaired along the way (ZPDF-P001 to P004) and what an encrypted file's author doesn't allow (P005). A file that can't be read at all throws `PdfReadException`.

## Merge, extract and split

`PdfFiles` copies whole pages into a new file, with their fonts, images and annotations. Links between copied pages keep working. Links to pages left behind, and anything that runs scripts or launches programs, are dropped. Page ranges look like `1-3,5`, `2-` or `-4`.

```csharp
await using FileStream pack = File.Create("pack.pdf");
PdfFileResult merged = await PdfFiles.MergeAsync([new PdfPageSource(cover), new PdfPageSource(contract) { Pages = "2-" }], pack,
    new PdfFileOptions { Title = "Signing pack" }, cancellationToken);

await using FileStream summary = File.Create("summary.pdf");
await PdfFiles.ExtractAsync(contract, "1-3", summary, new PdfFileOptions { Title = "Contract summary", Language = "en" }, cancellationToken);
```

Tags, bookmarks and form logic belong to a whole file, so they aren't kept, and the output is untagged. Merging isn't for PDF/UA documents yet. The metadata you give in `PdfFileOptions` replaces the original's.

On the command line:

```bash
zorg-pdf info contract.pdf
```

```bash
zorg-pdf merge cover.pdf contract.pdf:2- -o pack.pdf --title "Signing pack"
```

```bash
zorg-pdf split contract.pdf -o pages
```

## Using a page as a background

A page of an existing PDF can sit behind every page of a new document, as letterhead. See [letters](letters.md).
