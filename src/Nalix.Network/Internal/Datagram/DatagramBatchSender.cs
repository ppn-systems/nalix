// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nalix.Network.Internal.Datagram;

/// <summary>
/// Sends several datagrams in one system call with <c>sendmmsg</c> on 64-bit Linux. Each <c>sendto</c> is a system
/// call of its own, and its fixed cost dominates when a session sends a burst (a large message split into many
/// datagrams, a window of retransmissions).
/// </summary>
/// <remarks>
/// <para>
/// Linux only. Windows has no system call that sends datagrams to several destinations at once (<c>WSASendMsg</c>
/// sends one; UDP send offload only splits one buffer for one destination), and macOS has no public one, so there
/// <see cref="IsSupported"/> is <see langword="false"/>, the native function is never called and every datagram goes
/// through <see cref="Socket.SendTo(ReadOnlySpan{byte}, SocketFlags, SocketAddress)"/> as before.
/// </para>
/// Best effort: <see cref="TrySend"/> returns 0 whenever it did not send the first datagram (another platform, a full
/// send buffer, an error for that destination), and the caller sends it the regular way, which also reports or drops
/// the error as before.
/// </remarks>
internal static unsafe partial class DatagramBatchSender
{
    /// <summary>The most datagrams sent per call.</summary>
    public const int MaxBatch = 8;

    // A sockaddr_in6 is 28 bytes; each destination is copied into a slot of this size.
    private const int AddressSlot = 32;

    private const int EPERM = 1;
    private const int ENOSYS = 38;

    private static int s_disabled = OperatingSystem.IsLinux() && IntPtr.Size == 8 ? 0 : 1;

    /// <summary>Gets a value indicating whether batches are sent with one system call.</summary>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsSupported => Volatile.Read(ref s_disabled) == 0;

    /// <summary>
    /// Sends datagram <c>i</c>, <c>buffer[(i * stride)..][..lengths[i]]</c>, to <c>targets[i]</c>, in order.
    /// </summary>
    /// <returns>The number of leading datagrams sent; 0 when the first was not sent.</returns>
    public static int TrySend(
        Socket socket, ReadOnlySpan<byte> buffer, int stride, ReadOnlySpan<int> lengths, ReadOnlySpan<SocketAddress?> targets)
    {
        if (!IsSupported)
        {
            return 0;
        }

        int count = Math.Min(Math.Min(lengths.Length, targets.Length), MaxBatch);
        byte* names = stackalloc byte[MaxBatch * AddressSlot];
        IoVector* vectors = stackalloc IoVector[MaxBatch];
        MultiMessageHeader* messages = stackalloc MultiMessageHeader[MaxBatch];

        SafeSocketHandle handle = socket.SafeHandle;
        bool added = false;

        fixed (byte* data = buffer)
        {
            try
            {
                // Keeps the descriptor from being closed, and its number reused, while the call runs.
                handle.DangerousAddRef(ref added);

                int prepared = 0;
                for (; prepared < count; prepared++)
                {
                    SocketAddress? target = targets[prepared];
                    if (target is null || target.Size > AddressSlot)
                    {
                        break;
                    }

                    byte* name = names + (prepared * AddressSlot);
                    target.Buffer.Span[..target.Size].CopyTo(new Span<byte>(name, AddressSlot));

                    vectors[prepared] = new IoVector { Base = data + (prepared * stride), Length = (nuint)lengths[prepared] };
                    messages[prepared] = new MultiMessageHeader
                    {
                        Name = name,
                        NameLength = (uint)target.Size,
                        Vectors = &vectors[prepared],
                        VectorCount = 1,
                    };
                }

                if (prepared == 0)
                {
                    return 0;
                }

                int sent = SendMultipleMessages((int)handle.DangerousGetHandle(), messages, (uint)prepared, 0);
                if (sent > 0)
                {
                    return sent;
                }

                // Otherwise the send buffer is full or the first destination failed: the regular path handles it.
                if (Marshal.GetLastPInvokeError() is ENOSYS or EPERM)
                {
                    // Not available here (an old kernel, a seccomp filter): never try again.
                    Volatile.Write(ref s_disabled, 1);
                }

                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            finally
            {
                if (added)
                {
                    handle.DangerousRelease();
                }
            }
        }
    }

    // Linux only: callers check IsSupported (Linux, 64-bit) first, so the binding is never loaded elsewhere.
    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static partial int SendMultipleMessages(int fd, MultiMessageHeader* messages, uint count, int flags);

    /// <summary>A <c>struct iovec</c> (64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoVector
    {
        public byte* Base;
        public nuint Length;
    }

    /// <summary>A <c>struct mmsghdr</c>: a <c>struct msghdr</c> followed by <c>msg_len</c> (64-bit, 64 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct MultiMessageHeader
    {
        [FieldOffset(0)]
        public byte* Name;

        [FieldOffset(8)]
        public uint NameLength;

        [FieldOffset(16)]
        public IoVector* Vectors;

        [FieldOffset(24)]
        public nuint VectorCount;

        [FieldOffset(32)]
        public void* Control;

        [FieldOffset(40)]
        public nuint ControlLength;

        [FieldOffset(48)]
        public int Flags;

        [FieldOffset(56)]
        public uint SentLength;
    }
}
