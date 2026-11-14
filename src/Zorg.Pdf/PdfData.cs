using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Pdf;

/// <summary>
/// Data for a render, by slot name: the rows of tables bound with <see cref="TableRows.Bound(string)"/>, and the
/// bytes of images that name a slot with <see cref="ImageSource.FromSlot(string)"/>. Rows are read once, as the table
/// is laid out, so a source can stream a million rows without holding them. Images are loaded before the first page,
/// so a missing or damaged image stops the render before anything is written.
/// </summary>
/// <remarks>
/// An <see cref="IAsyncEnumerable{T}"/> source needs <see cref="PdfDocument.RenderAsync"/>; the synchronous
/// <see cref="PdfDocument.Render"/> refuses it (ZPDF-R002) rather than block on it.
/// </remarks>
/// <example>
/// <code>
/// var data = PdfData.Empty.WithRows("lines", repository.StreamInvoiceLinesAsync(id), line =>
///     [line.Description, line.Hours.ToString("N1"), line.Amount.ToString("N2")]);
///
/// await Invoice.RenderAsync(response.Body, options with { Data = data }, cancellationToken);
/// </code>
/// </example>
public sealed class PdfData
{
    private PdfData(ImmutableDictionary<string, RowSource> rows, ImmutableDictionary<string, ImageSlot> images) => (Rows, Images) = (rows, images);

    /// <summary>No data.</summary>
    public static PdfData Empty { get; } = new(
        ImmutableDictionary<string, RowSource>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, ImageSlot>.Empty.WithComparers(StringComparer.Ordinal));

    internal ImmutableDictionary<string, RowSource> Rows { get; }

    internal ImmutableDictionary<string, ImageSlot> Images { get; }

    /// <summary>
    /// Reads data from JSON: an object whose properties are slots. An array is a table's rows, each an array of cells
    /// written as in a document; an object with <c>"base64"</c> is an image.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The data.</returns>
    /// <exception cref="PdfDocumentException">The JSON isn't data; the diagnostics say where, with JSON pointers.</exception>
    /// <example>
    /// <code>
    /// PdfData data = PdfData.ParseJson("""
    ///     { "lines": [["1", "Consulting", "7.5", "9 375,00"]], "logo": { "base64": "iVBORw0KGgo..." } }
    ///     """);
    /// </code>
    /// </example>
    public static PdfData ParseJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Json.DataJson.Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>The bytes of a JPEG or PNG for a slot.</summary>
    /// <param name="slot">The slot name an image's source names.</param>
    /// <param name="bytes">The file's bytes.</param>
    /// <returns>The data with the slot set; this instance is unchanged.</returns>
    /// <example>
    /// <code>
    /// var data = PdfData.Empty.WithImage("logo", await File.ReadAllBytesAsync("logo.png", cancellationToken));
    /// </code>
    /// </example>
    public PdfData WithImage(string slot, ReadOnlyMemory<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return new PdfData(Rows, Images.SetItem(slot, new ImageSlot(bytes, null)));
    }

    /// <summary>A JPEG or PNG for a slot, loaded when the render starts, such as from blob storage.</summary>
    /// <param name="slot">The slot name an image's source names.</param>
    /// <param name="load">Loads the bytes. It's called once per render, before the first page, and only when the document uses the slot.</param>
    /// <returns>The data with the slot set; this instance is unchanged.</returns>
    /// <example>
    /// <code>
    /// var data = PdfData.Empty.WithImage("logo", async ct => (await blobs.DownloadContentAsync(ct)).Value.Content.ToMemory());
    /// </code>
    /// </example>
    public PdfData WithImage(string slot, Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> load)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentNullException.ThrowIfNull(load);
        return new PdfData(Rows, Images.SetItem(slot, new ImageSlot(null, load)));
    }

    /// <summary>Rows for a slot, each a list of cells (strings convert to cells).</summary>
    /// <param name="slot">The slot name a table's rows are bound to.</param>
    /// <param name="rows">The rows.</param>
    /// <returns>The data with the slot set; this instance is unchanged.</returns>
    public PdfData WithRows(string slot, IEnumerable<IReadOnlyList<Cell>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentNullException.ThrowIfNull(rows);
        return new PdfData(Rows.SetItem(slot, new RowSource(new SyncRows(rows), IsAsync: false)), Images);
    }

    /// <summary>Rows for a slot from an asynchronous source, such as a database query.</summary>
    /// <param name="slot">The slot name a table's rows are bound to.</param>
    /// <param name="rows">The rows, read as the table is laid out.</param>
    /// <returns>The data with the slot set; this instance is unchanged.</returns>
    public PdfData WithRows(string slot, IAsyncEnumerable<IReadOnlyList<Cell>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        ArgumentNullException.ThrowIfNull(rows);
        return new PdfData(Rows.SetItem(slot, new RowSource(rows, IsAsync: true)), Images);
    }

    /// <summary>Rows for a slot from items, with a function that makes each item's cells.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="slot">The slot name.</param>
    /// <param name="items">The items.</param>
    /// <param name="cells">Makes one row's cells from an item.</param>
    /// <returns>The data with the slot set.</returns>
    public PdfData WithRows<T>(string slot, IEnumerable<T> items, Func<T, IReadOnlyList<Cell>> cells)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cells);
        return WithRows(slot, Map(items, cells));
    }

    /// <summary>Rows for a slot from asynchronous items, with a function that makes each item's cells.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="slot">The slot name.</param>
    /// <param name="items">The items, read as the table is laid out.</param>
    /// <param name="cells">Makes one row's cells from an item.</param>
    /// <returns>The data with the slot set.</returns>
    public PdfData WithRows<T>(string slot, IAsyncEnumerable<T> items, Func<T, IReadOnlyList<Cell>> cells)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(cells);
        return WithRows(slot, MapAsync(items, cells));
    }

    private static IEnumerable<IReadOnlyList<Cell>> Map<T>(IEnumerable<T> items, Func<T, IReadOnlyList<Cell>> cells)
    {
        foreach (T item in items)
        {
            yield return cells(item);
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<Cell>> MapAsync<T>(IAsyncEnumerable<T> items, Func<T, IReadOnlyList<Cell>> cells, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (T item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return cells(item);
        }
    }

    /// <summary>A slot's image: its bytes, or a function that loads them (which a synchronous render can't wait for).</summary>
    internal sealed record ImageSlot(ReadOnlyMemory<byte>? Bytes, Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Load);

    /// <summary>A slot's rows, and whether reading them can wait on I/O.</summary>
    internal sealed record RowSource(IAsyncEnumerable<IReadOnlyList<Cell>> Rows, bool IsAsync);

    /// <summary>A synchronous sequence seen as asynchronous: every step completes at once, so a sync render never waits.</summary>
    private sealed class SyncRows(IEnumerable<IReadOnlyList<Cell>> rows) : IAsyncEnumerable<IReadOnlyList<Cell>>
    {
        public IAsyncEnumerator<IReadOnlyList<Cell>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(rows.GetEnumerator(), cancellationToken);

        private sealed class Enumerator(IEnumerator<IReadOnlyList<Cell>> rows, CancellationToken cancellationToken) : IAsyncEnumerator<IReadOnlyList<Cell>>
        {
            public IReadOnlyList<Cell> Current => rows.Current;

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(rows.MoveNext());
            }

            public ValueTask DisposeAsync()
            {
                rows.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
