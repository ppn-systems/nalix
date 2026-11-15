// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#pragma warning disable CA1711

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Defines bitwise flags that describe the state or properties of a network packet.
/// </summary>
/// <remarks>
/// This enumeration is marked with <see cref="System.FlagsAttribute"/> so that multiple flags can be combined using a bitwise OR.
/// </remarks>
[System.Flags]
public enum PacketFlags : byte
{
    /// <summary>
    /// No flags are set.
    /// The packet is uncompressed, unencrypted, and not fragmented.
    /// Typically used for simple, default, or test packets.
    /// </summary>
    NONE = 0x00,

    /// <summary>
    /// The packet payload has been compressed to reduce its size.
    /// Should be decompressed on the receiving side before processing.
    /// </summary>
    COMPRESSED = 1 << 1,

    /// <summary>
    /// The packet payload has been encrypted for secure transmission.
    /// Should be decrypted with the correct key on the receiving side.
    /// </summary>
    ENCRYPTED = 1 << 2,

    /// <summary>
    /// The frame is one chunk of a larger message that exceeded the maximum allowed size.
    /// Set by the transport on every fragment chunk header; the receiver detects fragments by this
    /// bit (never by payload content) and reassembles them in order.
    /// </summary>
    FRAGMENTED = 1 << 3,

    /// <summary>
    /// The packet is a system-level message that does not contain user data.
    /// Examples include ping, heartbeat, handshake, or system error notifications.
    /// </summary>
    SYSTEM = 1 << 7

    // -----------------------------
    // Ghi chú sử dụng cho PacketFlags
    // -----------------------------

    // NONE
    // Không có cờ nào được bật.
    // Dùng khi gói tin là đơn giản nhất, không mã hóa, không nén, không phân mảnh.
    // Thường dùng cho các gói dữ liệu mặc định hoặc test.

    // COMPRESSED
    // Gói tin đã được nén để giảm kích thước truyền tải.
    // Thường dùng khi payload lớn và cần tiết kiệm băng thông.
    // Cần giải nén ở phía nhận trước khi xử lý.

    // ENCRYPTED
    // Gói tin đã được mã hóa để đảm bảo an toàn khi truyền qua mạng.
    // Dùng cho dữ liệu nhạy cảm như tài khoản, mật khẩu, token xác thực, v.v.
    // Phía nhận phải giải mã đúng key hoặc sẽ bị lỗi giải mã.

    // FRAGMENTED
    // Gói tin đã bị phân mảnh vì vượt quá giới hạn kích thước cho phép.
    // Dùng cho những payload lớn (như file, hình ảnh, bản đồ, v.v.)
    // Cần ghép lại ở phía nhận theo đúng thứ tự.

    // SYSTEM
    // Gói tin cấp hệ thống, không mang dữ liệu người dùng.
    // Dùng cho ping, heartbeat, handshake, thông báo lỗi hệ thống.
    // Phân biệt với gói ứng dụng thông thường.
}
