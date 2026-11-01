# Nalix.Codec Security & Bug Audit

Branch: `audit/codec-security`
Scope: `src/Nalix.Codec` (crypto/AEAD, key derivation, envelope and transform pipeline, LZ4, serialization of untrusted input, length checks, buffer handling).
Mode: read-only. No production code under `src/` was changed.

## Result

**Confirmed findings: 0.**

No defect was confirmed by reading the code and reproducing it with a test. Per the audit rules, observations without a reproducing test are not listed as findings. Those observations are recorded below for reference.

`dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit` therefore has no `Category=Audit` tests to run. No audit reproduction tests were added to `tests/Nalix.Codec.Tests/Audit/`.

## Verification performed

| Check | Method | Result |
| :--- | :--- | :--- |
| RFC 8439 §2.8.2 ChaCha20-Poly1305 vector | Probe xUnit test | Pass (ciphertext prefix and tag match) |
| Keccak-256 of empty input | Probe xUnit test | Pass (`c5d2460186f7...a470`) |
| ChaCha20-Poly1305 vs. BCL reference | Differential fuzz, 3,000 random cases, plaintext 0–699 B, AAD 0–79 B | Pass (ciphertext and tag identical to `System.Security.Cryptography.ChaCha20Poly1305`) |
| LZ4 round-trip | Sizes 1–300 (random, zero, periodic), plus 4 KiB, 64 KiB, 100 KB and `MaxBlockSize` (256 KiB) blocks with zeros, random and mixed data | Pass |

The probe test files were used only for this verification and were deleted afterwards. They are not part of the commit.

Build note: `dotnet` was found at `/root/.dotnet` and the codec test project builds in Release (0 errors).

## Areas reviewed, no defect found

- **Envelope parsing** (`Security/Internal/EnvelopeFormat.cs`, `EnvelopeHeader.cs`): magic, version and suite checks; length checks before every slice; AEAD tag split only after `blob.Length >= pos + TagSize`.
- **Downgrade and nonce checks** (`Security/EnvelopeCipher.cs:326-337`): algorithm must equal the expected suite (SEC-38); the nonce length is checked against the suite before dispatch.
- **AEAD decrypt ordering** (`Security/Aead/ChaCha20Poly1305.cs:201-224`, `Salsa20Poly1305.cs:187-212`): the Poly1305 key is derived, the tag is recomputed over `AAD || pad || CT || pad || len(AAD) || len(CT)`, and compared with `FixedTimeEquals` before any plaintext is produced. Plaintext is not released on failure.
- **Engine buffer checks** (`Security/Engine/AeadEngine.cs`, `SymmetricEngine.cs`): output-size checks precede every `Slice`.
- **X25519 handshake** (`Security/Asymmetric/X25519.Curve.cs:91-98`, `HandshakeX25519.cs`): low-order inputs produce an all-zero result, which is rejected.
- **Proof of work** (`Security/ProofOfWork.cs`): create and verify build the same 49-byte payload, and the MAC binds `ConnectionId`. Freshness and replay are enforced by the caller (`src/Nalix.Runtime/Handlers/ProofOfWorkHandlers.cs:43-56`).
- **LZ4 decoder** (`LZ4/Engine/LZ4Decoder.cs`): literal and match lengths are checked against input and output; offset is checked against bytes already written; the final consumed-equals-expected check is present. `SpanOps.ReadVarInt` (`Internal/Memory/SpanOps.cs:101-190`) detects overflow on every branch. `MemOps.Copy` handles forward-overlapping matches. The header bound `(P-4)*255+19` is tight for valid input (it matches the maximum expansion of one sequence) and does not reject valid data.
- **Frame transform and fused inbound path** (`Transforms/FrameTransformer.cs`, `FramePipeline.cs:146-218`): the header is copied into the AAD before decryption; the LZ4 output size comes from the header and is bounded by `MaxDecompressedSize` and `MaxBlockSize`; the destination span is exactly that size; the header flags are cleared after success.
- **Serialization of untrusted input** (`Serialization/Internal/CollectionGuard.cs`, formatters, `Extensions/DataReaderExtensions.cs`, `Environment/Memory/DataReader.cs`): all counts go through `CollectionGuard` or `ReadBytes`, which bound the value and the remaining bytes before allocating. Overflow in `count * elementSize` is guarded with `long`. `DataReader.GetSpanReference` checks bounds itself. Collection formatters validate before allocation.

## Observations (not findings)

These are recorded for the maintainers. None has a demonstrated security impact, and none is backed by a failing test.

1. **Non-AEAD suites are unauthenticated by design (informational).** `EnvelopeCipher.TryDecrypt` and `SymmetricEngine.TryDecrypt` accept `Chacha20` and `Salsa20` envelopes with no tag, and the header is not covered by any MAC. Those envelopes are reachable only if the caller passes a non-AEAD `expectedAlgorithm`. The wire path rejects that: `src/Nalix.Runtime/Handlers/SystemControlHandlers.cs:180` allows only `Chacha20Poly1305` and `Salsa20Poly1305` for a cipher switch. Keep that guard in place. Consider rejecting non-AEAD suites inside `EnvelopeCipher.TryDecrypt` as defense in depth.

2. **Off-by-one in `FrameTransformer.GetPlaintextLength` (trivial).** `src/Nalix.Codec/Transforms/FrameTransformer.cs:78` uses `envelope.Length < Offset`, but `TryGetPlaintextLength` at line 95 uses `<=`. For an input of exactly `Offset` bytes, `GetPlaintextLength` slices to empty and throws `CipherException` instead of `SourceTooSmall`. Behavior is still safe.

3. **`SymmetricEngine.Encrypt` does not validate caller-supplied nonce length** (`SymmetricEngine.cs:135`). Only the caller can reach this path, not the wire, and the failure is a thrown exception, not memory corruption.

4. **`ProofOfWork.VerifySolution` is a pure function.** It does not check timestamp freshness or replay. The runtime handler does both (30 s window, already-verified guard). Any new caller must do the same.

## Suggested follow-ups (text only, no code changed)

- Add a regression test for non-AEAD suite rejection in `EnvelopeCipher.TryDecrypt` if observation 1 is adopted.
- Add a random-mutation fuzz test for `LZ4Codec.TryDecode` to confirm it never throws anything other than the documented error paths on malformed blocks. Mutation of valid blocks was not run in this audit.
- Fuzz `FramePipeline.TryProcessInbound` with mutated envelopes to confirm no exception escapes `TryProcessInbound`.

Not run in this audit: fuzzing of the serializer with mutated inputs, and the Salsa20 AEAD construction (no reference vector is available for this non-standard combination; it was reviewed by reading only).
