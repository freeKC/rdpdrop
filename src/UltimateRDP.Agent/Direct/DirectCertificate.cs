using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UltimateRDP.Agent.Logging;
using UltimateRDP.Core.Transfer;

namespace UltimateRDP.Agent.Direct;

/// <summary>
/// The self signed TLS certificate of the direct connection: ECDSA P-256, created at the
/// first direct offer of the agent process and kept until it exits. It is never added to a
/// certificate store and never exported; the client pins its SHA-256, which the agent sends
/// through the RDP channel.
/// </summary>
/// <remarks>
/// SChannel, which <see cref="SslStream"/> uses on Windows, refuses a server key that exists
/// only in the memory of the process ("the platform does not support ephemeral keys"). The
/// private key is therefore a named key of the CNG user key store of the session user,
/// Microsoft Software Key Storage Provider, marked as not exportable. Its name holds the
/// session id, so that a key left by an agent that ended abruptly is replaced by the next
/// one in that session. The agent deletes it when it exits.
/// </remarks>
internal static class DirectCertificate
{
    private static readonly Lock Gate = new();
    private static CngKey? _key;
    private static ECDsaCng? _signer;
    private static X509Certificate2? _certificate;
    private static SslStreamCertificateContext? _context;
    private static string? _sha256;
    private static bool _failed;

    /// <summary>Name of the key in the CNG user key store, one per session.</summary>
    public static string KeyName { get; } = "UltimateRDP.Agent.Direct.S" + Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The certificate and the SHA-256 of its DER form in lower case hexadecimal digits,
    /// created at the first call; null when it cannot be created, which the log says once.
    /// </summary>
    public static (SslStreamCertificateContext Context, string Sha256)? Get(FileLogger logger)
    {
        lock (Gate)
        {
            if (_context is not null)
            {
                return (_context, _sha256!);
            }

            if (_failed)
            {
                return null;
            }

            try
            {
                var started = Stopwatch.GetTimestamp();
                _key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
                    KeyUsage = CngKeyUsages.Signing,
                });
                _signer = new ECDsaCng(_key);
                var request = new CertificateRequest("CN=UltimateRDP agent", _signer, HashAlgorithmName.SHA256);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
                var now = DateTimeOffset.UtcNow;
                _certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
                _sha256 = TransferHash.ToHex(_certificate.GetCertHash(HashAlgorithmName.SHA256));
                _context = SslStreamCertificateContext.Create(_certificate, additionalCertificates: null, offline: true);
                logger.Info($"Direct connection: TLS certificate ECDSA P-256 created in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms, SHA-256 {_sha256}, key {KeyName} of the user key store, not exportable.");
                return (_context, _sha256);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                _failed = true;
                logger.Warn($"Direct connection: the TLS certificate cannot be created, so the files go through the channel. {ex.Message}");
                DeleteUnderLock(logger);
                return null;
            }
        }
    }

    /// <summary>Deletes the key, when this process created one. Called when the agent exits; never throws.</summary>
    public static void Delete(FileLogger? logger)
    {
        lock (Gate)
        {
            DeleteUnderLock(logger);
        }
    }

    private static void DeleteUnderLock(FileLogger? logger)
    {
        if (_key is null)
        {
            return;
        }

        try
        {
            _context = null;
            _certificate?.Dispose();
            _certificate = null;
            _signer?.Dispose();
            _signer = null;
            _key.Delete();
            logger?.Info($"Direct connection: key {KeyName} deleted.");
        }
        catch (Exception ex) when (ex is CryptographicException or ObjectDisposedException)
        {
            logger?.Warn($"Direct connection: key {KeyName} could not be deleted; the next agent of the session replaces it. {ex.Message}");
        }
        finally
        {
            _key.Dispose();
            _key = null;
        }
    }
}
