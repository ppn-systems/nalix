// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

using Nalix.Abstractions.Exceptions;

namespace Nalix.Network.Internal.Security;

/// <summary>
/// Handles highly optimized persistence of IP networks and addresses in plain-text format.
/// </summary>
internal static class NetworkStore
{
    /// <summary>
    /// Loads IP networks from a line-separated plain-text file.
    /// </summary>
    public static List<IPNetwork> Load(string filePath, int maxRecords)
    {
        List<IPNetwork> records = new();
        if (!File.Exists(filePath))
        {
            return records;
        }

        int[] backoffDelays = [100, 250, 500, 1000];
        int retryCount = 0;

        while (true)
        {
            try
            {
                using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                using StreamReader reader = new(fs);

                string? line;
                int lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (records.Count >= maxRecords)
                    {
                        break;
                    }

                    line = line.Trim();
                    if (string.IsNullOrEmpty(line) || line is ['#', ..])
                    {
                        continue;
                    }

                    if (IPNetwork.TryParse(line, out IPNetwork network))
                    {
                        records.Add(network);
                    }
                    else if (IPAddress.TryParse(line, out IPAddress? ip))
                    {
                        records.Add(new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
                    }
                    else
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                        {
                            // "[NW.NetworkStore] invalid line in file={FilePath} line={LineNumber} content={Content}", filePath, lineNumber, line);
                        }
                    }
                }

                return records;
            }
            catch (IOException) when (retryCount < backoffDelays.Length)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    int delay = backoffDelays[retryCount];
                    // ex, "[NW.NetworkStore] file locked, retrying in {Delay}ms file={FilePath}", delay, filePath);
                }
                System.Threading.Thread.Sleep(backoffDelays[retryCount]);
                retryCount++;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    // ex, "[NW.NetworkStore] error loading file={FilePath}", filePath);
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Saves IP networks to disk using an atomic write approach.
    /// </summary>
    public static void Save(string filePath, IEnumerable<IPNetwork> records, string listDescription)
    {
        string tmpPath = filePath + ".tmp";

        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            using (FileStream fs = new(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(fs))
            {
                writer.WriteLine($"# Nalix Connection Guard - {listDescription}");
                writer.WriteLine($"# Generated at {DateTime.UtcNow:u}");
                writer.WriteLine("# Each non-empty line must be a valid IP address or CIDR range.");
                writer.WriteLine("# Empty lines and lines starting with '#' are ignored.");
                writer.WriteLine("#");
                writer.WriteLine("# Format examples:");
                writer.WriteLine("# ------------------------------------------------------------------------------");
                writer.WriteLine("# 1. Single IPv4 Address (Equivalent to /32):");
                writer.WriteLine("#    192.168.1.15");
                writer.WriteLine("#");
                writer.WriteLine("# 2. IPv4 CIDR Range:");
                writer.WriteLine("#    192.168.1.0/24      # Matches 192.168.1.0 to 192.168.1.255 (256 IPs)");
                writer.WriteLine("#    172.16.0.0/16       # Matches 172.16.0.0 to 172.16.255.255 (65k IPs)");
                writer.WriteLine("#    10.0.0.0/8          # Matches 10.0.0.0 to 10.255.255.255 (16.7M IPs)");
                writer.WriteLine("#");
                writer.WriteLine("# 3. Single IPv6 Address (Equivalent to /128):");
                writer.WriteLine("#    2001:db8::1");
                writer.WriteLine("#");
                writer.WriteLine("# 4. IPv6 CIDR Range:");
                writer.WriteLine("#    2001:db8::/32       # Matches all IPv6 addresses starting with 2001:db8");
                writer.WriteLine("# ------------------------------------------------------------------------------");
                writer.WriteLine();

                foreach (IPNetwork network in records)
                {
                    writer.WriteLine(network.ToString());
                }

                fs.Flush(true); // Ensure physical write to disk
            }

            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Do not crash server on save error
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try
                {
                    File.Delete(tmpPath);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    // Ignore deletion errors during cleanup
                }
            }
        }
    }
}





