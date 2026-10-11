using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DynamicBay.Core;

namespace DynamicBay.Services.Link;

/// <summary>
/// This device's identity on the link: a self-signed ECDSA P-256 certificate, created once per Windows user and kept
/// DPAPI-protected in %AppData%\DynamicBay\link\identity.bin. Other devices pin its fingerprint when pairing.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    /// <summary>First 16 hex characters of the SHA-256 of the public key (SubjectPublicKeyInfo).</summary>
    public string DeviceId { get; }
    /// <summary>SHA-256 of the whole certificate (hex), what the other side pins.</summary>
    public string Fingerprint { get; }

    private SslStreamCertificateContext? _context;
    internal SslStreamCertificateContext Context => _context ??= SslStreamCertificateContext.Create(Certificate, null, offline: true);

    private DeviceIdentity(X509Certificate2 cert)
    {
        Certificate = cert;
        DeviceId = IdOf(cert);
        Fingerprint = FingerprintOf(cert);
    }

    public static string IdOf(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()))[..16];

    public static string FingerprintOf(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static string DefaultPath => Path.Combine(AppSettings.Folder, "link", "identity.bin");

    /// <summary>A new identity (not saved). <paramref name="machine"/> only goes into the certificate's name.</summary>
    public static DeviceIdentity Create(string? machine = null) => new(Load(CreatePfx(machine ?? Environment.MachineName)));

    /// <summary>The saved identity, or a new one that is saved right away. A file that cannot be read is set aside.</summary>
    public static DeviceIdentity LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            try
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                return new DeviceIdentity(Load(pfx));
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                Log.Error("Link identity", ex); // message only, never the key
                AtomicFile.SetAside(path);
            }
        }
        var bytes = CreatePfx(Environment.MachineName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        File.Move(tmp, path, overwrite: true);
        return new DeviceIdentity(Load(bytes));
    }

    private static readonly byte[] Entropy = "DynamicBay-link-identity"u8.ToArray();

    private static byte[] CreatePfx(string machine)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string cn = new string(machine.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray());
        var req = new CertificateRequest($"CN=DynamicBay {cn}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), // server authentication
            new Oid("1.3.6.1.5.5.7.3.2"), // client authentication
        }, false));
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        return cert.Export(X509ContentType.Pfx);
    }

    // Schannel needs a key it can open by name: an ephemeral one fails the TLS handshake, so the key goes into the
    // user's key store for as long as this object lives (removed again on Dispose, the file above is the real copy).
    private static X509Certificate2 Load(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);

    public void Dispose() => Certificate.Dispose();
}
