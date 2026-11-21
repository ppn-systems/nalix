// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nalix.Environment.Datagrams;
using static Nalix.Network.Internal.Datagram.DatagramBatchSender;

namespace Nalix.Network.Internal.Datagram;

/// <summary>
/// Reads the datagrams already queued on a socket, several per system call, with <c>recvmmsg</c> on 64-bit Linux.
/// A receive worker uses it after each awaited receive: under load the socket holds more datagrams than the one the
/// await returned, and reading them one <c>recvmsg</c> at a time paid a system call for each.
/// </summary>
/// <remarks>
/// Never waits: it returns 0 when nothing is queued, and the worker goes back to its awaited receive. Linux only
/// (see <see cref="DatagramBatchSender"/>); elsewhere <see cref="TryCreate"/> returns <see langword="null"/>.
/// One instance per receive worker: it owns pinned buffers for <see cref="MaxBatch"/> datagrams and their addresses
/// (arrays on the pinned object heap, so they never move and the GC frees them with the receiver).
/// </remarks>
internal sealed unsafe partial class DatagramBatchReceiver
{
    private const int MSG_DONTWAIT = 0x40;
    private const int AddressSlot = 128; // sizeof(struct sockaddr_storage)
    private const int Stride = DatagramEngine.MaxDatagramSize + 1; // one byte more: an oversized datagram shows

    private readonly byte[] _dataArray = GC.AllocateUninitializedArray<byte>(MaxBatch * Stride, pinned: true);
    private readonly byte[] _namesArray = GC.AllocateUninitializedArray<byte>(MaxBatch * AddressSlot, pinned: true);
    private readonly IoVector[] _vectorsArray = GC.AllocateArray<IoVector>(MaxBatch, pinned: true);
    private readonly MultiMessageHeader[] _messagesArray = GC.AllocateArray<MultiMessageHeader>(MaxBatch, pinned: true);
    private readonly byte* _data;
    private readonly byte* _names;
    private readonly IoVector* _vectors;
    private readonly MultiMessageHeader* _messages;
    private readonly SocketAddress[] _addresses;

    private DatagramBatchReceiver(AddressFamily family)
    {
        _data = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_dataArray));
        _names = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_namesArray));
        _vectors = (IoVector*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_vectorsArray));
        _messages = (MultiMessageHeader*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_messagesArray));
        _addresses = new SocketAddress[MaxBatch];
        for (int i = 0; i < MaxBatch; i++)
        {
            _addresses[i] = new SocketAddress(family);
        }
    }

    /// <summary>Creates a receiver for <paramref name="socket"/>, or <see langword="null"/> where batching is not supported.</summary>
    public static DatagramBatchReceiver? TryCreate(Socket socket) => IsSupported ? new DatagramBatchReceiver(socket.AddressFamily) : null;

    /// <summary>Gets the payload of datagram <paramref name="index"/> of the last <see cref="Receive"/>.</summary>
    public ReadOnlySpan<byte> Datagram(int index) => new(_data + (index * Stride), (int)_messages[index].SentLength);

    /// <summary>Gets the source of datagram <paramref name="index"/> of the last <see cref="Receive"/>.</summary>
    public SocketAddress Address(int index) => _addresses[index];

    /// <summary>Reads up to <see cref="MaxBatch"/> queued datagrams without waiting.</summary>
    /// <returns>The number read; 0 when none is queued, on an error, or once the socket is closed.</returns>
    public int Receive(Socket socket)
    {
        if (!IsSupported)
        {
            return 0;
        }

        for (int i = 0; i < MaxBatch; i++)
        {
            _vectors[i] = new IoVector { Base = _data + (i * Stride), Length = Stride };
            _messages[i] = new MultiMessageHeader
            {
                Name = _names + (i * AddressSlot),
                NameLength = AddressSlot,
                Vectors = &_vectors[i],
                VectorCount = 1,
            };
        }

        SafeSocketHandle handle = socket.SafeHandle;
        bool added = false;
        int count;
        try
        {
            // Keeps the descriptor from being closed, and its number reused, while the call runs.
            handle.DangerousAddRef(ref added);
            count = ReceiveMultipleMessages((int)handle.DangerousGetHandle(), _messages, MaxBatch, MSG_DONTWAIT, null);
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

        if (count <= 0)
        {
            return 0; // Nothing queued (EAGAIN) or an error: the awaited receive reports errors.
        }

        for (int i = 0; i < count; i++)
        {
            SocketAddress address = _addresses[i];
            int length = Math.Min((int)_messages[i].NameLength, address.Buffer.Length);
            new ReadOnlySpan<byte>(_names + (i * AddressSlot), length).CopyTo(address.Buffer.Span);
            address.Size = length;
        }

        return count;
    }

    [LibraryImport("libc", EntryPoint = "recvmmsg", SetLastError = true)]
    private static partial int ReceiveMultipleMessages(int fd, MultiMessageHeader* messages, uint count, int flags, void* timeout);
}
