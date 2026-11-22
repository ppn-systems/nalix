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
/// pass it to an owning parameter, return it, store it or its memory in a non-borrowed field, or
/// capture it in a lambda queued to the thread pool unless it first calls
/// <see cref="IBufferLease.Retain"/> (NALIX110). One call to <see cref="IBufferLease.Retain"/>
/// grants one release.
/// </para>
/// <para>
/// The contract belongs to the interface member or base method: implementations and overrides
/// inherit this attribute from it, and must not add it on their own (NALIX119).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class BorrowedAttribute : Attribute
{
}
