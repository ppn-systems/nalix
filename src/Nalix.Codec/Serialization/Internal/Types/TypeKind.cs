// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;

namespace Nalix.Codec.Serialization.Internal.Types;

/// <summary>
/// Classifies the type shape that matters to the serializer's layout logic.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal enum TypeKind : byte
{
    /// <summary>
    /// No special handling is required.
    /// </summary>
    None = 0,

    /// <summary>
    /// A serializable type whose byte size is known up front.
    /// </summary>
    FixedSizeSerializable = 2,
}
