// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Internal.Ws;

public sealed class WsFrameReaderTests
{
    [Fact]
    public void Dispose_WhenCreated_DisposesFragmentAssembler()
    {
        Type wsReaderType = typeof(TcpSession).Assembly.GetType("Nalix.SDK.Transport.Internal.Ws.WsFrameReader")!;
        Type stateType = typeof(TcpSession).Assembly.GetType("Nalix.SDK.Transport.SessionState")!;
        object state = Activator.CreateInstance(stateType)!;

        ConstructorInfo ctor = wsReaderType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        Func<System.Net.WebSockets.ClientWebSocket> wsProvider = () => null!;
        Action<IBufferLease> onMsg = _ => { };
        Action<Exception, System.Net.WebSockets.ClientWebSocket?> onDisc = (_, _) => { };
        using IDisposable reader = (IDisposable)ctor.Invoke([wsProvider, new TransportOptions(), state, new WebSocketTransportOptions(), onMsg, onDisc]);

        FieldInfo? assemblerField = wsReaderType.GetField("_assembler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(assemblerField);

        object? assembler = assemblerField!.GetValue(reader);
        Assert.NotNull(assembler);

        reader.Dispose();

        FieldInfo? disposedField = assembler!.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(disposedField);
        Assert.True((bool)disposedField!.GetValue(assembler)!);
    }
}
