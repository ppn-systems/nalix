// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Configuration;
using Nalix.Environment.IO;
using Nalix.Environment.Options;
using Nalix.Environment.Random;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Hosting.Internal;
using Nalix.Hosting.Options;
using Nalix.Network.Options;
using Nalix.Runtime.Options;

namespace Nalix.Hosting;

/// <summary>
/// Provides bootstrapping and global initialization for Nalix server-side hosting.
/// </summary>
public static partial class Bootstrap
{
    private static readonly Lock s_lock;
    private static readonly string s_serverGC;
    private static bool s_isHighPrecisionTimerEnabled;

    internal static DiagnosticChannel? DiagnosticChannel;

    static Bootstrap()
    {
        s_lock = new();
        Console.CancelKeyPress += OnProcessExit;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        s_serverGC = System.Runtime.GCSettings.IsServerGC ? "Server GC" : "Workstation GC";
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        lock (s_lock)
        {
            Console.CancelKeyPress -= OnProcessExit;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;


            // Gracefully stop all background workers to allow in-flight tasks to complete.
            _ = InstanceManager.Instance.GetExistingInstance<TaskManager>()?
                                        .CancelAllWorkers();

            // Flush any pending configuration changes to disk on shutdown.
            // This ensures that if the server was running with defaults
            ConfigurationManager.Instance.Flush();

            // Dispose InstanceManager to trigger disposal of all registered services
            // (including ConnectionGuard, which saves the ban database to disk).
            if (InstanceManager.IsCreated)
            {
                InstanceManager.Instance.Dispose();
            }

            if (s_isHighPrecisionTimerEnabled && OperatingSystem.IsWindows())
            {
                _ = TimeEndPeriod(1);
                s_isHighPrecisionTimerEnabled = false;
            }

            DiagnosticChannel?.Dispose();
            DiagnosticChannel = null;
        }
    }

    /// <summary>
    /// Initializes the Nalix Hosting environment with server-optimized settings.
    /// This is called automatically by the module initializer, but can be called manually if needed.
    /// </summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage",
        "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Architectural requirement to auto-configure server defaults")]
    internal static void Initialize()
    {
        _ = nameof(Csprng);

        // Server typically uses server.ini to avoid conflicts with client.ini in dual-deployment scenarios
        ConfigurationManager.Instance.SetConfigFilePath(Path.Combine(Directories.ConfigurationDirectory, "server.ini"));

        // 1. Enable packet pooling (explicitly) for maximum server throughput


        // 2. Initialize all core server options to provide full templates in server.ini

        // Framework-level options
        _ = ConfigurationManager.Instance.Get<BufferOptions>();
        _ = ConfigurationManager.Instance.Get<CompressionOptions>();
        _ = ConfigurationManager.Instance.Get<FragmentOptions>();
        _ = ConfigurationManager.Instance.Get<ObjectPoolOptions>();
        _ = ConfigurationManager.Instance.Get<SecurityOptions>();
        _ = ConfigurationManager.Instance.Get<SnowflakeOptions>();
        _ = ConfigurationManager.Instance.Get<TaskManagerOptions>();

        // Network-level options
        _ = ConfigurationManager.Instance.Get<SequenceOptions>();
        _ = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        _ = ConfigurationManager.Instance.Get<DatagramOptions>();
        _ = ConfigurationManager.Instance.Get<DatagramEngineOptions>();
        _ = ConfigurationManager.Instance.Get<NetworkSocketOptions>();
        _ = ConfigurationManager.Instance.Get<ConnectionHubOptions>();
        _ = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        _ = ConfigurationManager.Instance.Get<ConnectionGuardOptions>();
        _ = ConfigurationManager.Instance.Get<ConnectionBanStoreOptions>();
        _ = ConfigurationManager.Instance.Get<ConnectionBlacklistStoreOptions>();
        _ = ConfigurationManager.Instance.Get<TrustedProxyOptions>();
        _ = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();
        _ = ConfigurationManager.Instance.Get<ForwardedHeadersOptions>();
        _ = ConfigurationManager.Instance.Get<NetworkWebSocketOptions>();
        _ = ConfigurationManager.Instance.Get<ProxyProtocolOptions>();

        // Runtime-level options
        _ = ConfigurationManager.Instance.Get<DispatchOptions>();
        _ = ConfigurationManager.Instance.Get<PacketDrainOptions>();
        _ = ConfigurationManager.Instance.Get<TokenBucketOptions>();
        _ = ConfigurationManager.Instance.Get<SessionStoreOptions>();
        _ = ConfigurationManager.Instance.Get<DirectiveGuardOptions>();

        // 3. Hosting specific options
        HostingOptions hostingOptions = ConfigurationManager.Instance.Get<HostingOptions>();

        // 3.1. ThreadPool tuning for high-concurrency server workloads
        if (hostingOptions.MinWorkerThreads > 0 || hostingOptions.MinCompletionPortThreads > 0)
        {
            ThreadPool.GetMinThreads(out int worker, out int iocp);
            _ = ThreadPool.SetMinThreads(
                hostingOptions.MinWorkerThreads > 0 ? hostingOptions.MinWorkerThreads : worker,
                hostingOptions.MinCompletionPortThreads > 0 ? hostingOptions.MinCompletionPortThreads : iocp);
        }

        // 3.2. Global exception handling for production stability
        if (hostingOptions.EnableGlobalExceptionHandling)
        {
            REGISTER_GLOBAL_EXCEPTION_HANDLERS();
        }

        // 3.3. High-precision timer for low-latency networking
        if (hostingOptions.EnableHighPrecisionTimer && OperatingSystem.IsWindows())
        {
            if (TimeBeginPeriod(1) == 0)
            {
                s_isHighPrecisionTimerEnabled = true;
            }
        }

        // Persist all server-side defaults to server.ini
        ConfigurationManager.Instance.Flush();

        // 5. Show high-end startup diagnostics if running in interactive mode
        if (System.Environment.UserInteractive && !hostingOptions.DisableStartupBanner)
        {
            PRINT_STARTUP_BANNER(hostingOptions);
        }
    }

    private static void PRINT_STARTUP_BANNER(HostingOptions options)
    {
        if (!Console.IsOutputRedirected && !options.DisableConsoleClear)
        {
            Console.Clear();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(Resource.Banner1);
        Console.WriteLine(Resource.Banner2);
        Console.WriteLine(Resource.Banner3);
        Console.WriteLine(Resource.Banner4);
        Console.WriteLine(Resource.Banner5);
        Console.ResetColor();

        Console.WriteLine($"[INIT] Version   : {typeof(Bootstrap).Assembly.GetName().Version}");
        Console.WriteLine($"[INIT] PID       : {System.Environment.ProcessId}");
        Console.WriteLine($"[INIT] OS        : {System.Environment.OSVersion}");
        Console.WriteLine($"[INIT] Arch      : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"[INIT] Runtime   : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"[INIT] Config    : {ConfigurationManager.Instance.ConfigFilePath}");
        Console.WriteLine($"[INIT] GC Mode   : {s_serverGC}");
        Console.WriteLine($"[INIT] Timer     : {GET_TIMER_STATUS()}");
        Console.WriteLine($"[INIT] Started   : {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"[INIT] Processors: {System.Environment.ProcessorCount}");
        Console.WriteLine($"[INIT] Local IP  : {GET_LOCAL_IP_ADDRESSES()}");
        Console.WriteLine($"[INIT] Public IP : {GET_PUBLIC_IP_HINT()}");
        Console.WriteLine();
        Console.WriteLine();

        if (!System.Runtime.GCSettings.IsServerGC)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(Resource.ServerGC);
            Console.ResetColor();
        }

        Console.WriteLine(Resource.Separator);
    }

    private static string GET_TIMER_STATUS()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Default";
        }

        return s_isHighPrecisionTimerEnabled ? "High precision 1ms" : "Default";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "<Pending>")]
    private static void REGISTER_GLOBAL_EXCEPTION_HANDLERS()
    {
        AppDomain.CurrentDomain.UnhandledException += static (s, e) =>
        {
            ILogger? logger = InstanceManager.Instance.GetExistingInstance<ILogger>();

            if (logger != null)
            {
                Exception? ex = e.ExceptionObject as Exception;
                logger.LogCritical(ex, "[BOOTSTRAP] Unhandled exception occurred. IsTerminating: {IsTerminating}", e.IsTerminating);
            }

            if (e.IsTerminating)
            {
                ConfigurationManager.Instance.Flush();
            }
        };

        TaskScheduler.UnobservedTaskException += static (s, e) =>
        {
            ILogger? logger = InstanceManager.Instance.GetExistingInstance<ILogger>();
            logger?.LogError(e.Exception, "[BOOTSTRAP] Unobserved task exception occurred.");
            e.SetObserved();
        };
    }

    private static string GET_LOCAL_IP_ADDRESSES()
    {
        try
        {
            List<string> addresses = [];

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus is not OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    IPAddress address = unicast.Address;

                    if (address.AddressFamily is AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address))
                    {
                        addresses.Add($"{GET_NETWORK_INTERFACE_DISPLAY_NAME(networkInterface)}={address}");
                    }
                }
            }

            return addresses.Count == 0 ? "Unavailable" : string.Join(", ", addresses);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return "Unavailable";
        }
    }

    private static string GET_NETWORK_INTERFACE_DISPLAY_NAME(NetworkInterface networkInterface)
    {
        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Wireless80211)
        {
            return "Wi-Fi";
        }

        if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ethernet)
        {
            return networkInterface.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase)
                ? "Virtual"
                : "Ethernet";
        }

        return networkInterface.Name;
    }

    private static string GET_PUBLIC_IP_HINT()
    {
        // Public IP should not be resolved through outbound HTTP during bootstrap.
        // Use environment/config so startup remains deterministic and offline-safe.
        string? publicIp = System.Environment.GetEnvironmentVariable("NALIX_PUBLIC_IP");

        return string.IsNullOrWhiteSpace(publicIp) ? "Not configured" : publicIp;
    }

    #region P/Invoke for Windows timer resolution

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint uPeriod);

    #endregion P/Invoke for Windows timer resolution
}
