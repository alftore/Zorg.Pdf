using System;
using System.Threading;
using Zorg.Pdf.Tool;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // The first Ctrl+C stops watching cleanly; a second one ends the process.
    e.Cancel = !stop.IsCancellationRequested;
    stop.Cancel();
};

return await Cli.RunAsync(args, CliConsole.System, stop.Token).ConfigureAwait(false);
