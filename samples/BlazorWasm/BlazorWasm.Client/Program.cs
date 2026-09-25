// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007

using BlazorWasm.Client;
using BlazorWasm.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

WebAssemblyHostBuilder builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// wwwroot/appsettings.json -> "Nalix" section.
builder.Services.Configure<NalixClientOptions>(builder.Configuration.GetSection("Nalix"));

// One WebSocket session for the whole tab. In Blazor WASM "Singleton" and "Scoped" both live
// for the lifetime of the tab; Singleton makes that explicit.
builder.Services.AddSingleton<NalixClient>();

await builder.Build().RunAsync();
