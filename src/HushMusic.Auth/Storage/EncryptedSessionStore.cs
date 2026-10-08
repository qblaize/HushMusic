using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using HushMusic.Auth.Cookies;

namespace HushMusic.Auth.Storage;

/// <summary>
/// The cookie jar on disk: JSON protected with DPAPI for the current Windows user ("LOCAL=user"),
/// written atomically. Not thread-safe; the caller serializes access.
/// </summary>
internal sealed class EncryptedSessionStore(string filePath, ILogger logger)
{
    private const string ProtectionDescriptor = "LOCAL=user";

    public string FilePath { get; } = filePath;

    private string TempPath => FilePath + ".tmp";

    public async Task SaveAsync(IReadOnlyList<StoredCookie> cookies, CancellationToken cancellationToken)
    {
        var blob = new SessionBlob { SavedAt = DateTimeOffset.UtcNow, Cookies = [.. cookies] };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(blob, AuthJsonContext.Default.SessionBlob);
        byte[] protectedBytes;
        try
        {
            protectedBytes = await ProtectAsync(plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using (var stream = new FileStream(
            TempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(TempPath, FilePath, overwrite: true);
    }

    /// <summary>
    /// Returns the stored cookies, or null when there is no usable session. A blob that cannot be decrypted
    /// or parsed (other Windows user, corrupted file, old format) is deleted.
    /// </summary>
    public async Task<IReadOnlyList<StoredCookie>?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read failure is not proof of corruption (e.g. a scanner holding the file), so keep the blob.
            logger.LogWarning("Could not read the stored session ({ExceptionType}); continuing signed out.", ex.GetType().Name);
            return null;
        }

        try
        {
            var plaintext = await UnprotectAsync(data, cancellationToken).ConfigureAwait(false);
            SessionBlob? blob;
            try
            {
                blob = JsonSerializer.Deserialize(plaintext, AuthJsonContext.Default.SessionBlob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (blob is null || blob.Version != SessionBlob.CurrentVersion || blob.Cookies is null || !IsWellFormed(blob.Cookies))
            {
                throw new InvalidDataException("Unexpected session blob content.");
            }

            return blob.Cookies;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the type and HRESULT are logged: messages of JSON errors could quote fragments of the blob.
            logger.LogWarning(
                "The stored session could not be decrypted or is corrupt ({ExceptionType}, 0x{HResult:X8}); deleting it and continuing signed out.",
                ex.GetType().Name,
                ex.HResult);
            Delete();
            return null;
        }
    }

    /// <summary>Deletes the blob and any half-written temp copy. Best effort, never throws.</summary>
    public void Delete()
    {
        TryDelete(FilePath);
        TryDelete(TempPath);
    }

    private static bool IsWellFormed(List<StoredCookie> cookies)
    {
        foreach (var cookie in cookies)
        {
            if (cookie is null || !CookieJar.IsValidName(cookie.Name) || cookie.Value is null
                || string.IsNullOrEmpty(cookie.Domain) || cookie.Path is null)
            {
                return false;
            }
        }

        return true;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete {File} ({ExceptionType}).", Path.GetFileName(path), ex.GetType().Name);
        }
    }

    private static async Task<byte[]> ProtectAsync(byte[] plaintext, CancellationToken cancellationToken)
    {
        var provider = new DataProtectionProvider(ProtectionDescriptor);
        var input = CryptographicBuffer.CreateFromByteArray(plaintext);
        var output = await provider.ProtectAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        CryptographicBuffer.CopyToByteArray(output, out var result);
        return result;
    }

    private static async Task<byte[]> UnprotectAsync(byte[] data, CancellationToken cancellationToken)
    {
        // Unprotect must use the parameterless provider; the descriptor is read from the blob.
        var provider = new DataProtectionProvider();
        var input = CryptographicBuffer.CreateFromByteArray(data);
        var output = await provider.UnprotectAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        CryptographicBuffer.CopyToByteArray(output, out var result);
        return result ?? [];
    }
}
