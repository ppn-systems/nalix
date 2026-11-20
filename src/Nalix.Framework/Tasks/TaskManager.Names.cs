// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nalix.Framework.Tasks;

/// <summary>
/// Provides a standardized naming scheme for worker groups, worker instances,
/// and recurring jobs.
/// <para>
/// Conventions:
/// - Groups use path-style segments with '/' separators, because they often describe hierarchy.
/// - Workers and recurring jobs use dot-style segments with '.' separators, because they read like identifiers.
/// - All names are normalized to lowercase-safe tokens before they are concatenated.
/// </para>
/// </summary>
/// <remarks>
/// This is a general-purpose library. Specific domains (TCP, limiter, etc.)
/// should build on top of these helpers rather than hardcoding names here.
/// </remarks>
[StackTraceHidden]
[DebuggerStepThrough]
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TaskNaming
{
    /// <summary>
    /// Canonical short tags used when building task names.
    /// Applications may add their own tags, but these keep the built-in naming
    /// scheme consistent across the framework.
    /// </summary>
    public static class Tags
    {
        /// Tag for TCP-related tasks.
        public const string Tcp = "tcp";

        /// Tag for WebSocket-related tasks.
        public const string WebSocket = "ws";

        /// Tag for generic network tasks.
        public const string Net = "net";

        /// Tag for time synchronization or scheduling tasks.
        public const string Time = "time";

        /// Tag for synchronization-related tasks.
        public const string Sync = "sync";

        /// Tag for wheel-based timing tasks.
        public const string Wheel = "wheel";

        /// <summary>
        /// Tag for tasks that process data or requests.
        /// </summary>
        public const string Process = "proc";

        /// <summary>
        /// Tag for tasks that synchronize time or state.
        /// </summary>
        public const string Worker = "worker";

        /// <summary>
        /// Tag for tasks that accept connections or requests.
        /// </summary>
        public const string Accept = "accept";

        /// <summary>
        /// Tag for tasks that perform cleanup operations.
        /// </summary>
        public const string Cleanup = "cleanup";

        /// <summary>
        /// Tag for tasks related to service operations.
        /// </summary>
        public const string Service = "service";

        /// <summary>
        /// Tag for tasks that dispatch work or messages.
        /// </summary>
        public const string Dispatch = "dispatch";

        /// <summary>
        /// Tag for tasks that handle logging or telemetry.
        /// </summary>
        public const string Logging = "log";

        /// <summary>
        /// Tag for tasks that manage proxies or intermediaries.
        /// </summary>
        public const string Proxy = "proxy";
    }

    /// <summary>
    /// Builds recurring job names using the framework's dot-style convention.
    /// </summary>
    public static class Recurring
    {
        /// <summary>
        /// Builds a recurring job name that combines a sanitized prefix, a built-in tag,
        /// and a fixed-width hexadecimal instance key.
        /// </summary>
        /// <remarks>
        /// This keeps job names stable and sortable while still allowing the caller to
        /// provide a human-readable prefix.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static string CleanupJobId(string prefix, int instanceKey) => $"{SanitizeToken(prefix)}.{Tags.Cleanup}.{instanceKey:X8}";
    }

    /// <summary>
    /// Sanitizes an arbitrary string into a safe token for task names.
    /// </summary>
    /// <remarks>
    /// The sanitizer keeps only letters, digits, hyphen, underscore, and period.
    /// Any other character is replaced with an underscore so callers can pass in
    /// user input without accidentally breaking the naming format.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [SkipLocalsInit]
    public static string SanitizeToken(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "-";
        }

        ReadOnlySpan<char> input = MemoryExtensions.AsSpan(s);
        Span<char> buf = input.Length <= 256
            ? stackalloc char[input.Length]
            : new char[input.Length];

        int k = 0;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            buf[k++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.'
                ? c : '_';
        }

        return new string(buf[..k]);
    }
}
