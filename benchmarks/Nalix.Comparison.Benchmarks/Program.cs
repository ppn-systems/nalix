// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Comparison.Benchmarks.Harness;
using Nalix.Comparison.Benchmarks.Libraries;

IBenchLibrary[] all =
[
    new RawTcpLibrary(),
    new NalixLibrary(NalixMode.Tcp),
    new NalixLibrary(NalixMode.TcpAead),
    new NalixLibrary(NalixMode.WebSocket),
    new NalixLibrary(NalixMode.TcpNoCompression),
    new NalixLibrary(NalixMode.TcpInline),
    new KestrelWebSocketLibrary(),
    new SignalRLibrary(),
    new GrpcLibrary(duplex: false, tls: false),
    new GrpcLibrary(duplex: true, tls: false),
    new GrpcLibrary(duplex: false, tls: true),
    new GrpcLibrary(duplex: true, tls: true),
];

return await HarnessMain.RunAsync(args, all.ToDictionary(l => l.Name));
