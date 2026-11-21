using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Nogrammerly.Core;

namespace Nogrammerly;

/// <summary>
/// One-time set-up for the Word add-in: a certificate so Word trusts pages served from this PC, and an entry
/// that tells Word where the add-in's manifest is. Both are for the current user only and need no admin rights.
/// </summary>
internal static class AddinSetup
{
    private const string FriendlyName = "Nogrammerly add-in (this PC only)";
    private const string WordKey = @"Software\Microsoft\Office\16.0\WEF\Developer";

    /// <summary>Where the add-in's files are installed, next to the app.</summary>
    public static string SiteRoot => Path.Combine(AppContext.BaseDirectory, "WordAddin");

    // The certificate. It is for "localhost" only and cannot sign other certificates, so trusting it does not
    // let anyone pose as another site. Its private key stays in this user's Windows key store and can't be exported.

    /// <summary>A usable certificate made earlier, or null. "Usable" means it has its key, is in date and is still trusted.</summary>
    public static X509Certificate2? FindCertificate()
    {
        using var mine = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        using var roots = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        mine.Open(OpenFlags.ReadOnly);
        roots.Open(OpenFlags.ReadOnly);
        return mine.Certificates
            .Where(c => c.FriendlyName == FriendlyName && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(7))
            .FirstOrDefault(c => roots.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count > 0);
    }

    /// <summary>
    /// Makes the certificate and asks Windows to trust it. Windows shows its own confirmation, and the user
    /// may say no: then nothing is kept and this returns null.
    /// </summary>
    public static X509Certificate2? CreateCertificate()
    {
        RemoveCertificates();
        var certificate = NewCertificate(keepKey: true);
        using var mine = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        mine.Open(OpenFlags.ReadWrite);
        mine.Add(certificate);
        try
        {
            using var roots = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            roots.Open(OpenFlags.ReadWrite);
            // Only the public half goes in the trusted list.
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
            publicOnly.FriendlyName = FriendlyName;
            roots.Add(publicOnly);
            return certificate;
        }
        catch (CryptographicException)
        {
            mine.Remove(certificate);
            return null;
        }
    }

    /// <summary>
    /// A self-signed certificate for localhost. Trusted by nothing until added to a store. With
    /// <paramref name="keepKey"/> false, its key is deleted when the certificate is disposed.
    /// </summary>
    internal static X509Certificate2 NewCertificate(bool keepKey)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=Nogrammerly add-in (this PC only)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server authentication.
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var unsaved = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(3));
        // Windows TLS needs the key in its key store, which a round trip through a file format arranges.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var saved = X509CertificateLoader.LoadPkcs12(unsaved.Export(X509ContentType.Pkcs12, password), password, keepKey ? X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.UserKeySet);
        saved.FriendlyName = FriendlyName;
        return saved;
    }

    /// <summary>Removes every certificate this app made. Windows asks before removing one from the trusted list.</summary>
    public static void RemoveCertificates()
    {
        foreach (var name in new[] { StoreName.Root, StoreName.My })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var old in store.Certificates.Where(c => c.FriendlyName == FriendlyName).ToList()) store.Remove(old);
            }
            catch (CryptographicException) { } // The user said no; leave it.
        }
    }

    // Word. Word learns about an add-in that isn't from its store through a registry entry naming the add-in's
    // manifest file. The installer writes that entry, pointing at a file that doesn't exist yet, because an app
    // installed as a package can't: Windows keeps a package's registry writes in a private copy Word never reads.
    // Files are not affected, so creating the manifest file is what switches the add-in on, and deleting it, off.

    /// <summary>Writes the manifest for this PC's address, which makes the add-in appear in Word.</summary>
    public static void RegisterWithWord(int port, string manifestPath)
    {
        var template = File.ReadAllText(Path.Combine(SiteRoot, "manifest.xml"));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, AddinSite.ManifestFor(template, AddinSite.Origin(port)));
        // Covers a copy that was not installed as a package, or was installed by an older installer.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(WordKey);
            if (key.GetValue(AddinSite.ManifestId) is not string existing || !File.Exists(existing)) key.SetValue(AddinSite.ManifestId, manifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>
    /// Opens Word with the add-in loaded. Word shows a registered add-in's button only after it has been added
    /// once; opening a document that refers to the add-in does that, so the user needn't hunt through Word's menus.
    /// </summary>
    public static void OpenInWord(string manifestPath)
    {
        var document = Path.Combine(Path.GetDirectoryName(manifestPath)!, "Nogrammerly for Word.docx");
        File.WriteAllBytes(document, StarterDocument.Build(File.ReadAllText(manifestPath),
            "Nogrammerly is ready. Its pane is on the right, and its button is on the Home tab. You can close this document without saving it."));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(document) { UseShellExecute = true })?.Dispose();
    }

    public static void RemoveFromWord(string manifestPath)
    {
        try { File.Delete(Path.Combine(Path.GetDirectoryName(manifestPath)!, "Nogrammerly for Word.docx")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { File.Delete(manifestPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
