// Copyright (c) Microsoft Corporation.

using System.Security.Cryptography;
using System.Text;

namespace ZavaFinance.Core.Identity;

/// <summary>
/// Derives the persisted agent session key. SECURITY CRITICAL — this key is the isolation
/// boundary between users.
/// <para>
/// The key is an HMAC-SHA256 of the validated caller identity plus the conversation id,
/// rendered as unpadded RFC 4648 Base32. It is keyed for two reasons:
/// </para>
/// <list type="number">
///   <item>Conversation IDs contain characters that are not safe in storage keys.</item>
///   <item>Operational state must not expose tenant and user object IDs.</item>
/// </list>
/// <para>
/// There is deliberately no overload that accepts a caller-supplied key.
/// </para>
/// </summary>
public interface ISessionKeyProvider
{
    string GetSessionKey(CallerIdentity caller, string conversationId);
}

public sealed class SessionKeyProvider : ISessionKeyProvider
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly byte[] _salt;

    public SessionKeyProvider(string salt)
    {
        if (string.IsNullOrWhiteSpace(salt))
        {
            throw new ArgumentException(
                "A session key salt is required. Configure SessionKeySalt from Key Vault.",
                nameof(salt));
        }

        _salt = Encoding.UTF8.GetBytes(salt);
    }

    public string GetSessionKey(CallerIdentity caller, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException(
                "A conversation id is required.", nameof(conversationId));
        }

        // Length-prefixed field encoding. A plain separator would let a crafted
        // conversation id shift bytes between fields and collide with another user.
        using var buffer = new MemoryStream();
        WriteField(buffer, caller.TenantId);
        WriteField(buffer, caller.UserObjectId);
        WriteField(buffer, conversationId);

        byte[] hash = HMACSHA256.HashData(_salt, buffer.ToArray());

        return ToBase32(hash);
    }

    private static void WriteField(Stream destination, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BitConverter.TryWriteBytes(length, bytes.Length);
        destination.Write(length);
        destination.Write(bytes);
    }

    /// <summary>
    /// RFC 4648 Base32 without padding. The alphabet is restricted to A-Z and 2-7, so the
    /// result is always a legal storage key.
    /// </summary>
    internal static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);

        int buffer = 0;
        int bitsInBuffer = 0;

        foreach (byte value in data)
        {
            buffer = (buffer << 8) | value;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                bitsInBuffer -= 5;
                builder.Append(Base32Alphabet[(buffer >> bitsInBuffer) & 0x1F]);
            }
        }

        if (bitsInBuffer > 0)
        {
            builder.Append(Base32Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);
        }

        return builder.ToString();
    }
}
