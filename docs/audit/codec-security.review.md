# Nalix.Codec Security Audit: Review

Review of `docs/audit/codec-security.md` (branch `audit/codec-security`, commit `d5b92de`).
Scope: `src/Nalix.Codec`. Production code under `src/` was not changed.

## Verdict summary

The auditor reported 0 confirmed findings and listed 4 observations. Review result:

- **1 confirmed finding** (observation 2, Low). Repro test added and failing for the claimed reason.
- **3 false positives** (observations 1, 3 and 4). Observations 1 and 4 are intentional design, as documented. Observation 3 has no observable impact.
- **No new defects** found in the performance or missed-issue pass (see below).

## Verdict table

| # | Auditor observation | Verdict | Severity (auditor → review) | Evidence |
| :-- | :--- | :--- | :--- | :--- |
| 1 | Non-AEAD suites (`Chacha20`, `Salsa20`) are unauthenticated | **FALSE-POSITIVE (by design)** | informational → none | `Nalix.Abstractions/Security/CipherSuiteType.cs` remarks define a symmetric cipher as "confidentiality but not authentication". The wire path only allows AEAD suites: `src/Nalix.Runtime/Handlers/SystemControlHandlers.cs:178-180`. `AeadEngine.TryDecrypt` rejects `None` and stream suites with `UnsupportedAlgorithm`. |
| 2 | `FrameTransformer.GetPlaintextLength` guards `< Offset`, `TryGetPlaintextLength` guards `<= Offset` | **CONFIRMED** | trivial → **Low** | Repro `Audit/CodecSecurityReviewAuditTests.cs` fails: `Assert.Throws` got `CipherException` ("Envelope too short ... length=0") instead of `InternalErrorException`. The existing test only covers `Offset - 1`, so the exact-`Offset` boundary was untested. Fix: `src/Nalix.Codec/Transforms/FrameTransformer.cs:78` `<` → `<=`. The `TryGet` path already returns `false` here. |
| 3 | `SymmetricEngine.Encrypt` does not validate caller nonce length | **FALSE-POSITIVE (no impact)** | low → none | Probe test (since deleted): an 8-byte nonce for `Chacha20` throws `CipherException` from `ChaCha20.InitializeNonce` (`Security/Symmetric/ChaCha20.cs:245`) before any keystream is written. The envelope header is written only after `Encrypt` returns, so no malformed envelope is emitted. |
| 4 | `ProofOfWork.VerifySolution` is pure: no freshness or replay check | **FALSE-POSITIVE (by design)** | low → none | `ProofOfWork` is documented as a "stateless PoW challenge". Freshness and replay are enforced by the handler: `src/Nalix.Runtime/Handlers/ProofOfWorkHandlers.cs` checks `IsReliable` (replay), `Level >= POW_VERIFIED`, and the 30 s window. |

## Auditor follow-ups: status

- LZ4 random-mutation fuzz: **already covered** by `tests/Nalix.Codec.Tests/LZ4/LZ4CodecAdversarialTests.cs` (`Decode_RandomFuzzBlobs_OnlyDocumentedErrorsOrCleanSuccess`, plus the field-mutation tests). Not re-run in this review.
- Envelope and frame-pipeline mutation: **already covered** by `EnvelopeCipherMisuseTests`, `FramePipelineAdversarialTests` and `FramePipelineHeaderAuthenticationTests`. Not re-run in this review.
- Rejecting non-AEAD suites inside `EnvelopeCipher.TryDecrypt` (defense in depth): optional hardening, not a defect. Not implemented.
- Serializer mutation fuzzing: not run in this review either.

## Performance pass (static review)

Requested: verify performance findings and check for allocations, LINQ, locks and copies in the codec. Method: `grep` over `src/Nalix.Codec` for `new byte[`, `ToArray`, `ToList`, LINQ, `lock`, and string formatting, then a read of each hit's callers. No benchmarks were run, so these conclusions are from static reading.

| Candidate | Verdict | Reason |
| :--- | :--- | :--- |
| `Poly1305.Compute` / `FinalizeTag` return `new byte[16]` | not a hot-path issue | No callers in `Nalix.Codec`. The AEAD paths use the span overloads and `stackalloc`. |
| `DataReaderExtensions.ReadBytes` allocates `new byte[count]` | by design | It is a public API that returns an array. The only caller is `ReadRemainingBytes`. No formatter copies twice. |
| `StringFormatter` decodes with `GetString` | no issue | Single decode straight from the span. |
| `PacketRegistry` uses `lock (s_gate)` | no issue | Locks are taken only in `Build` and `RegisterGenerated`. The deserialization lookup takes no lock. |
| `LZ4Encoder` builds a `Debug.WriteLine` string | no issue | Inside `#if DEBUG`. |
| `FormatterProvider` builds a log string | no issue | Guarded by `IsEnabled(...)`. |
| `LiteSerializer.Serialize` returns `writer.ToArray()` | by design | The API returns `byte[]`. |
| `Salsa20Poly1305` / `ChaCha20Poly1305` | no issue | Keys and `Poly1305` state are `stackalloc` / `ref struct`. |
| Composed delegate chain in `PacketRegistry.COMPOSE_COMBINED` | minor, not a finding | Dispatch is O(number of registered fast dispatchers) per packet. No allocation on the lookup path. Not worth a change without a benchmark. |

No performance finding survived verification.

## Missed issues

No additional defects found in scope beyond observation 2. The codec is already heavily tested (adversarial LZ4, envelope misuse, header authentication, frame adversarial, and PoW tests). Areas re-read in this pass, with no defect found: `EnvelopeFormat.TryParseEnvelope` (tag split only after the length check), `EnvelopeCipher.TryDecrypt` (SEC-38 algorithm and nonce-length checks precede dispatch), `AeadEngine.TryDecrypt` (authenticated data built before decrypt; plaintext not released on failure), and `ProofOfWork.VerifySolution` (MAC is verified before the puzzle hash).

## Repro tests

Added under `tests/Nalix.Codec.Tests/Audit/` with `[Trait("Category", "Audit")]`:

- `CodecSecurityReviewAuditTests.GetPlaintextLength_WhenEnvelopeIsExactlyHeaderOffset_ThrowsSourceTooSmall`: **fails for the claimed reason** (observation 2).

The repro is expected to fail until `FrameTransformer.cs:78` is changed to `<=`. Observations 1, 3 and 4 have no tests because they are false positives.

Run: `dotnet test tests/Nalix.Codec.Tests/Nalix.Codec.Tests.csproj --configuration Release --filter Category=Audit`
Result at review time: 1 failed (the repro above, for the claimed reason), 1 passed (`probe`, now deleted), 0 compile or setup errors.
