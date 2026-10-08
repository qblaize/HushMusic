using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using HushMusic.Core.Abstractions;

namespace HushMusic.Auth.Storage;

/// <summary>
/// Small named secrets, one DPAPI blob ("LOCAL=user") per name under <c>secure\secrets\</c>, written atomically.
/// Names are limited to lowercase letters, digits, '.' and '-' so they always map to a plain file name.
/// </summary>
internal sealed partial class DpapiSecretStore(IAppPaths paths, ILogger<DpapiSecretStore> logger) : ISecretStore, IDisposable
{
    private const string ProtectionDescriptor = "LOCAL=user";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] data;
            try
            {
                data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not read secret {Name} ({ExceptionType})", name, ex.GetType().Name);
                return null;
            }

            try
            {
                var plaintext = await UnprotectAsync(data, cancellationToken).ConfigureAwait(false);
                try
                {
                    return Encoding.UTF8.GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Another Windows user's blob or a corrupt file: it can never be read, so drop it.
                logger.LogWarning(
                    "Secret {Name} could not be decrypted ({ExceptionType}, 0x{HResult:X8}); deleting it",
                    name,
                    ex.GetType().Name,
                    ex.HResult);
                TryDelete(path);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        var temp = path + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (value is null)
            {
                TryDelete(path);
                TryDelete(temp);
                return;
            }

            var plaintext = Encoding.UTF8.GetBytes(value);
            byte[] protectedBytes;
            try
            {
                protectedBytes = await ProtectAsync(plaintext, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    internal static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    private string PathFor(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("A secret name may only contain a-z, 0-9, '.' and '-', and must start with a letter or digit.", nameof(name));
        }

        return Path.Combine(paths.Secure, "secrets", name + ".bin");
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete {File} ({ExceptionType})", Path.GetFileName(path), ex.GetType().Name);
        }
    }

    private static async Task<byte[]> ProtectAsync(byte[] plaintext, CancellationToken cancellationToken)
    {
        var provider = new DataProtectionProvider(ProtectionDescriptor);
        var output = await provider.ProtectAsync(CryptographicBuffer.CreateFromByteArray(plaintext)).AsTask(cancellationToken).ConfigureAwait(false);
        CryptographicBuffer.CopyToByteArray(output, out var result);
        return result;
    }

    private static async Task<byte[]> UnprotectAsync(byte[] data, CancellationToken cancellationToken)
    {
        // Unprotect must use the parameterless provider; the descriptor is read from the blob.
        var provider = new DataProtectionProvider();
        var output = await provider.UnprotectAsync(CryptographicBuffer.CreateFromByteArray(data)).AsTask(cancellationToken).ConfigureAwait(false);
        CryptographicBuffer.CopyToByteArray(output, out var result);
        return result ?? [];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
