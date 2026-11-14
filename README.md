# Zorg.Pdf

Async-first PDF generation for .NET 10, written in C# only. Documents and reports from code, from a JSON document model, or from templates and data, in the spirit of jsreport, with tagged (accessible) output, no native binaries and no NuGet dependencies.

**Status: early 0.1 development, not released.** The document model exists: build a document in C# or JSON, validate it, and round-trip it through JSON (schema in [schema/](schema/document-0.1.schema.json), examples in [docs/examples/](docs/examples), diagnostic codes in [docs/diagnostics.md](docs/diagnostics.md)). Rendering it to PDF is the next slice; the engine behind it is proven by an internal spike that passes veraPDF PDF/UA-1. What isn't supported is in [docs/limits.md](docs/limits.md).

## Goals

- **Async first:** fonts, images and data load asynchronously, table rows can come from `IAsyncEnumerable<T>`, pages stream to the output while layout continues, and every operation can be cancelled.
- **Easy for agents:** one document model described by a published JSON Schema, validation errors that point at the exact path, PNG page previews for visual self-checks, and ready-made `AIFunction` tools.
- **Easy reports:** templates plus data, with headers and footers, page X of Y, repeating table headers, tables of contents, page groups and charts.
- **Accessible and archivable:** tagged PDF by default, with PDF/UA and PDF/A checked against veraPDF.
- MIT licensed, with no runtime dependencies in the core package, NativeAOT and trimming clean.
- Native code only as an opt-in add-on, never a requirement.

## How this is built

Zorg.Pdf is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security problems as [SECURITY.md](SECURITY.md) describes. Local scratch files go in `.tmp/`, which is gitignored.

## License

MIT. See [LICENSE](LICENSE). Package choices are recorded in [docs/dependencies.md](docs/dependencies.md).
