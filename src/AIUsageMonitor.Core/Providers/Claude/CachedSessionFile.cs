using AIUsageMonitor.Core.Providers.Claude.Models;
using System.Security.Cryptography;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// The parsed state of one session transcript file, together with what is needed to decide whether it is still
/// valid and whether it can be extended by parsing only the lines appended since.
/// </summary>
/// <param name="Length">The file's size, in bytes, when it was examined.</param>
/// <param name="LastWriteUtcTicks">The file's last-write time (UTC ticks) when it was examined.</param>
/// <param name="ParsedBytes">The byte offset just past the last line that was parsed; the start of the unparsed remainder.</param>
/// <param name="Fingerprint">
/// A hash of the bytes immediately before <paramref name="ParsedBytes"/>, used to confirm that the file still begins
/// with the content that was parsed (it was appended to, not rewritten). Empty when it could not be computed.
/// </param>
/// <param name="Rows">The rows parsed from the first <paramref name="ParsedBytes"/> bytes of the file.</param>
public sealed record CachedSessionFile(
    long Length,
    long LastWriteUtcTicks,
    long ParsedBytes,
    byte[] Fingerprint,
    IReadOnlyList<SessionMessage> Rows)
{
    /// <summary>
    /// Determines whether the file on disk still has exactly the size and last-write time this entry was built from.
    /// </summary>
    /// <param name="length">The file's current size, in bytes.</param>
    /// <param name="lastWriteUtcTicks">The file's current last-write time, as UTC ticks.</param>
    /// <returns><see langword="true"/> if the entry describes the file as it is now.</returns>
    public bool Matches(long length, long lastWriteUtcTicks)
    {
        return Length == length && LastWriteUtcTicks == lastWriteUtcTicks;
    }
}

/// <summary>
/// Fingerprints the tail of the parsed part of an append-only transcript, so a cached parse can be trusted as the
/// prefix of the file's current content without re-reading all of it.
/// </summary>
internal static class SessionFileFingerprint
{
    /// <summary>
    /// The number of bytes before the parsed offset that are hashed.
    /// </summary>
    private const int WindowSize = 4096;

    /// <summary>
    /// Computes the fingerprint of the bytes ending at <paramref name="parsedBytes"/>.
    /// </summary>
    /// <param name="filePath">The transcript file.</param>
    /// <param name="parsedBytes">The offset just past the parsed content.</param>
    /// <returns>The fingerprint, or an empty array if the file could not be read (the entry then cannot be extended).</returns>
    public static byte[] Compute(string filePath, long parsedBytes)
    {
        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var size = (int)Math.Min(WindowSize, parsedBytes);
            Span<byte> window = stackalloc byte[WindowSize];
            window = window[..size];

            stream.Seek(parsedBytes - size, SeekOrigin.Begin);
            stream.ReadExactly(window);

            return SHA256.HashData(window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Determines whether the file still contains the content a fingerprint was taken from.
    /// </summary>
    /// <param name="filePath">The transcript file.</param>
    /// <param name="parsedBytes">The offset just past the parsed content.</param>
    /// <param name="fingerprint">The fingerprint recorded when the content was parsed.</param>
    /// <returns><see langword="true"/> if the bytes before <paramref name="parsedBytes"/> are unchanged.</returns>
    public static bool Matches(string filePath, long parsedBytes, byte[] fingerprint)
    {
        return fingerprint.Length > 0 && Compute(filePath, parsedBytes).AsSpan().SequenceEqual(fingerprint);
    }
}
