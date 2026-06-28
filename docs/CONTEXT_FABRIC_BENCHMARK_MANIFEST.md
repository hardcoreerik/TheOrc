# Context Fabric Benchmark Manifest

This document pins the manifest shape used by the checked-in Context Fabric benchmark fixtures in `OrchestratorIDE.UnitTests/TestData/ContextFabric/`.

The goal is simple:

- identify the exact source we imported
- identify the exact parser and segmenter versions that touched it
- pin the deterministic outputs we expect after a clean rebuild

This is intentionally smaller than a full benchmark-program spec. It is the minimum reproducibility contract that current CF-1 tests actually enforce.

## Current Repo Truth

The pinned Darwin, Constitution, and Federalist fixtures all use the same manifest pattern today:

- immutable source locator plus edition text
- source checksum
- parser and segmenter version identifiers
- expected document identity and normalized checksum
- expected segment-count and segment-ID fingerprints

That shape is what `ContextFabricCf1Tests` verifies during import and rebuild.

## Field Reference

| Field | Type | Meaning |
|---|---|---|
| `FixtureId` | string | Stable fixture handle used by tests and docs. |
| `SourceUrl` | string | Canonical public source used to assemble or download the fixture. |
| `DownloadedAtUtc` | DateTimeOffset (ISO-8601 string) | UTC timestamp for when the pinned source text or PDF was captured. |
| `Edition` | string | Human-readable edition or assembly note. |
| `MediaType` | string | Imported media type, such as `text/plain` or `application/pdf`. |
| `SourceSha256` | string | SHA-256 of the pinned source artifact committed to the repo. |
| `ParserId` | string | Parser family identifier used during import. |
| `ParserVersion` | string | Exact parser version identifier used during import. |
| `SegmenterVersion` | string | Exact segmenter version identifier used during import. |
| `ExpectedDocumentId` | string | Deterministic document ID expected after import. |
| `ExpectedNormalizedSha256` | string | SHA-256 of the normalized text artifact expected after import. |
| `ExpectedSegmentCount` | integer | Expected number of stored segments after deterministic segmentation. |
| `ExpectedSegmentIdsSha256` | string | SHA-256 over the ordered segment-ID list. |
| `ExpectedFirstSegmentId` | string | First deterministic segment ID, useful for quick drift checks. |
| `ExpectedLastSegmentId` | string | Last deterministic segment ID, useful for quick drift checks. |

## Sample JSON

This sample matches the field shape currently used by the checked-in fixtures:

```json
{
  "FixtureId": "united-states-constitution-full",
  "SourceUrl": "https://www.archives.gov/founding-docs/constitution-transcript",
  "DownloadedAtUtc": "2026-06-28T15:24:53.0022180Z",
  "Edition": "National Archives transcript, assembled with Bill of Rights and Amendments XI-XXVII transcript pages",
  "MediaType": "text/plain",
  "SourceSha256": "89e67bfca2c305fd8f1ef120f5a8b7e737c77dc84d556f8a0763e7a0608f1fc0",
  "ParserId": "fabric-text-markdown",
  "ParserVersion": "fabric-text-markdown-1.0",
  "SegmenterVersion": "fabric-segmenter-1.0",
  "ExpectedDocumentId": "doc-992af21452035a62e279c749",
  "ExpectedNormalizedSha256": "89e67bfca2c305fd8f1ef120f5a8b7e737c77dc84d556f8a0763e7a0608f1fc0",
  "ExpectedSegmentCount": 159,
  "ExpectedSegmentIdsSha256": "aca55df8eb9a8a2216332e4a8a6e9cfd75c0befe330a285858f7a3e355bbe81e",
  "ExpectedFirstSegmentId": "seg-ead9b15440f54d69d075c79f",
  "ExpectedLastSegmentId": "seg-c7a57e2b0937f22e2405c884"
}
```

## Optional Future Extensions

Do not add these until tests actually consume them:

- `LicenseClass`
- `PublicReportAllowed`
- `TelemetryAllowed`
- `HiveDistributionPolicy`
- `QuestionSetId`
- `AnswerKeyPolicy`

Those are valid benchmark-program concerns, but today they belong in corpus-program docs rather than the pinned-fixture contract.
