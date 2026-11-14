# .NET versions

The library targets one .NET version and runs on later ones unchanged. This page keeps what each new version offers a PDF library, so the target can be reconsidered when a new LTS ships or the current one leaves support.

Rule of thumb: move the target to the next LTS when the current one leaves support, and add a second target before that only for an API with a measured gain.

## .NET 10 (LTS, the current target)

- Supported until 2028-11-14.
- What the library will build on: `System.IO.Compression` (`ZLibStream`, `ZLibCompressionOptions`) for Flate streams, plain `Stream` for streamed output (Zorg.Ssh measured Pipelines as slower than pooled buffers), `System.Text.Json` source generation for the document model, `Vector128/256`, C# 14 (`field`, extension members).

## .NET 11 (STS, 2026-11-10)

- Supported until about November 2028, about the same time as .NET 10.
- CI runs the tests on .NET 11.
- To check once there is real code: Runtime Async (the async paths here are I/O-bound, so it may help allocations), and the `Reset()` on the streamless Deflate and ZLib encoders for many small content streams.
- Decision: stay on `net10.0`.
