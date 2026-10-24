// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Network.Options;

/// <summary>
/// Selects which HTTP forwarded header is trusted to carry the real client IP address.
/// </summary>
public enum ForwardedClientIpHeader : byte
{
    /// <summary>
    /// Reads <c>CF-Connecting-IP</c>, then <c>X-Real-IP</c>, then <c>X-Forwarded-For</c>,
    /// using the first header that is present.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Reads only <c>CF-Connecting-IP</c> (Cloudflare). Other forwarded headers are ignored.
    /// </summary>
    CfConnectingIp = 1,

    /// <summary>
    /// Reads only <c>X-Real-IP</c>. Other forwarded headers are ignored.
    /// </summary>
    XRealIp = 2,

    /// <summary>
    /// Reads only <c>X-Forwarded-For</c>. Other forwarded headers are ignored.
    /// </summary>
    XForwardedFor = 3,
}
