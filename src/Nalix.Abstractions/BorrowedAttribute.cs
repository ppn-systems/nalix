// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions;

/// <summary>
/// Indicates that a parameter of type <see cref="IBufferLease"/> is borrowed by the method 
/// and should not be disposed of or returned as a result of ownership transfer.
/// </summary>
/// <remarks>
/// <para>
/// This helps Nalix static analysis tools identify that the buffer lifecycle is managed elsewhere.
/// </para>
/// <para>
/// The Nalix analyzers treat this as an ownership contract: a lease-typed parameter <b>without</b>
/// this attribute takes ownership of its argument, so the caller must not dispose or use the lease
/// afterwards (NALIX108, NALIX109). A member marked with this attribute must not dispose the lease,
/// pass it to an owning parameter, or store it in a non-borrowed field unless it first calls
/// <see cref="IBufferLease.Retain"/> (NALIX110).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class BorrowedAttribute : Attribute
{
}
