# Audit: `src/Nalix.Environment` and `src/Nalix.Abstractions`

Scope: BufferLease ref counting and pooling, DataReader bounds, fragment assembler, LEB128 decoding,
and the Abstractions primitives that touch buffers (Bytes32, MemoryPacket, PacketDeserializer,
SerializerBounds). No production code was changed. Each finding below has a failing repro test under
`tests/Nalix.Environment.Tests/Audit/` (`[Trait("Category", "Audit")]`).

Run: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`

## Findings

### ENV-01 — Stale second `Dispose` on a reused BufferLease shell releases the new owner's buffer

- **Severity:** High (cross-owner buffer release; the released array can be re-rented while still in use)
- **Location:** `src/Nalix.Environment/Memory/BufferLease.cs:477-537` (`Dispose`), with shell reuse at
  `:80-123` (`RENT_LEASE_SHELL`) and `:557-563` (thread-local push)
- **Description:** `Dispose` treats any call with `_refCount > 0` as a legitimate release. After the
  first release, `ResetForPool()` puts the same `BufferLease` object back in the thread-local cache, and
  the next `Rent` pops that same object and calls `Initialize`, which sets `_refCount = 1`. A caller
  still holding the old reference and calling `Dispose` again therefore decrements the *new* owner's
  count to 0. The method then exchanges `_buffer` to null, returns the array to `ByteArrayPool`, and
  leaves the new owner holding an empty lease. The `Debug.Fail` guard at `:486-488` only fires when the
  count is already `<= 0`, so it does not detect this case in Debug builds either.
- **Impact:** The array is returned to the shared pool while the new owner still treats the lease as
  live. If the array is re-rented to another connection or request, two owners can read and write the
  same bytes, which is a cross-session data leak or corruption. The doc comment on `Dispose`
  ("Double-dispose is tolerated") understates this, since the tolerance only holds until the shell is
  reused.
- **Repro test:** `BufferLeaseAuditTests.Dispose_WhenStaleHandleReleasedAfterShellReuse_KeepsNewOwnerBuffer`
  (fails: "A stale second Dispose released the buffer owned by the new lease.")
- **Suggested fix (text only):** Make the handle single-use. Options: (a) bump a per-shell generation
  counter on every `Initialize` and have consumers hold a `(lease, generation)` token; (b) do not recycle
  shells that have been released through `Dispose` when a second dispose is possible, and instead return
  the array only (shells are cheap); (c) keep the shell out of the free-list for at least one extra
  rent cycle. Option (a) is the closest to the existing design and does not change the public API shape.

### ENV-02 — LEB128 `uint` decode silently drops bits of a 5th byte above 4 bits

- **Severity:** Medium (wire-level length prefix decodes to a different value without error)
- **Location:** `src/Nalix.Environment/Memory/Leb128.cs:140-146` (fast path, 5th byte) and `:170-190`
  (`TryReadSlow`, shift 28)
- **Description:** A 32-bit LEB128 value's 5th byte may only carry 4 payload bits (`0x0F`). The fast path
  shifts `(b4 & 0x7F) << 28` and checks only the continuation bit. Bits 4-6 of that byte are shifted
  past bit 31 and discarded, so a 5-byte sequence encoding 2^32 (`80 80 80 80 10`) decodes successfully
  to `0`. The slow path has the same gap. The existing overlong check (`Throw.OverlongLeb128`, which
  says "corrupt") is meant to reject such input, but it only covers the continuation bit.
- **Impact:** `SocketConnection.Receive.VarInt.cs:41` uses `Leb128.TryRead` for the frame length. A
  peer can send a length prefix that decodes to a small valid value (here `0`), so the framer accepts
  a frame size that a canonical decoder would reject. That makes framing ambiguous between components
  that decode the same prefix differently.
- **Repro test:** `Leb128AuditTests.TryRead_WhenFifthByteExceedsUInt32Range_ThrowsFormatException`
  (fails: "Assert.Throws() Failure: No exception was thrown")
- **Suggested fix (text only):** On the 5th byte, reject any value with `(b & 0x70) != 0` by calling
  `Throw.OverlongLeb128()`. In `TryReadSlow`, when `shift == 28`, apply the same `(b & 0x70) != 0` check
  before accumulating. Add the case to `Leb128Tests` next to the existing `-1` vector.

## Checked and not reported

These were read in detail and found correct, or were dropped because no test could back them:

- **`DataReader` bounds** (`Memory/DataReader.cs`): `GetSpanReference` and `Advance` compare as `uint`,
  so negative counts are rejected. No overrun found.
- **`FragmentAssembler.Add`** (`Fragments/FragmentAssembler.cs`): overflow checks use `long`, the buffer
  growth bound is `MaxStreamBytes`, the initial allocation is capped at `DefaultInitialStreamCapacity`,
  and ordered delivery is enforced. No overflow or out-of-range write found.
- **`FragmentHeader.ReadFrom`**: `IsFragmentedFrame` guards the length before reading. The XML comment
  calls the header "7-byte" while `WireSize` is 8 (magic included). This is a documentation nit, not a bug.
- **`BufferLease` shell return through `IObjectPoolManager`**: `ObjectPool.ReturnFast` calls
  `ResetForPool()`, so `IsReliable` and `EncryptedOnWire` are cleared on this path. Not a finding.
- **Fragment memory budget**: `MaxOpenStreams` × `MaxStreamBytes` allows about 1 GiB of buffered
  payload per connection, and there is no cross-connection cap. The attacker must send the bytes, so
  this is not amplification. Enforcing a global budget needs a new API, so no test can back it and it is
  not recorded as a finding.
- **`Bytes32` (`Abstractions/Primitives/Bytes32.cs`)**: `AsSpan()` (length 4 of `ulong`) and `Wipe`
  cover exactly 32 bytes. `Equals` and `IsZero` are correct on all three paths.
- **`MemoryPacket`, `PacketDeserializer`, `SerializerBounds`**: thin wrappers, no defects found.

## Not reviewed (out of time budget)

`Configuration/Binding/IniConfig.cs` (1562 lines), `Configuration/ConfigurationManager.cs`,
`IO/Directories.*` (permission handling), `Random/Csprng.cs` and `OsCsprng.cs`, `Hashing/Sha1.cs`,
`Hashing/XxHash32.cs`, `Memory/DataWriter.cs`, and `Sequencing/SequenceCounter.cs`. These should be
audited in a follow-up pass.
