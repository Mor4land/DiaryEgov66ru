using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MirageDiary.Core;

/// <summary>
/// Hardened TLS validator for educational and public service domains.
/// Validates certificates signed by the Russian Ministry of Digital Development (Минцифры)
/// by verifying root/intermediate CA thumbprints, valid date ranges, and strict host matching,
/// preventing arbitrary MitM attacks.
/// </summary>
public static class RussianTrustedCertificateValidator
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gosuslugi.ru",
        "esia.gosuslugi.ru",
        "lk.gosuslugi.ru",
        "myschool.gosuslugi.ru",
        "gu-st.ru",
        "max.ru"
    };

    // Known SHA-256 and SHA-1 thumbprints of official Russian Trusted Root & Subordinate CAs (Минцифры / ГУЦ)
    private static readonly HashSet<string> KnownRussianCaThumbprints = new(StringComparer.OrdinalIgnoreCase)
    {
        // Russian Trusted Root CA (SHA-256)
        "EE3607B0BEE3068E376A264A9A08719286D9426F8EE487A321940957F864CF44",
        // Russian Trusted Sub CA (SHA-256)
        "9559C5D191253013B018596645DB9C4B72BB2BE7E1CFA2863704874C7CF0D63E",
        "2155785036C900DBB5F1BB2A1569C80C55595BD6BF94867A29BBDDBC7D88A3F2",
        // Russian Trusted Root CA (SHA-1 fallback)
        "4463C531D7CCC64FE8B48D10221E72A25A30EFFA",
        "240E7C5D478A773F808DEEEB7D6017E5E3D8E1F8",
        // Russian Trusted Sub CA (SHA-1 fallback)
        "F22EE99C25E34BA4803FF13EED94DFFCBEECAE26",
        "C18F8BE9A9A4A293E37A6DDE538B57530467554F",
        "6741AB02CF6598C09652DC34D2DC095904E32B52"
    };

    public static bool Validate(HttpRequestMessage? request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        // 1. If standard public CAs (Let's Encrypt, DigiCert, GlobalSign, etc.) validate successfully - allow immediately
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (certificate == null)
            return false;

        // 2. Reject immediately if certificate is expired or not yet valid
        DateTime now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            return false;

        // 3. Resolve host strictly from request or certificate SAN/CN
        string host = request?.RequestUri?.Host?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(host))
        {
            host = certificate.GetNameInfo(X509NameType.DnsName, false)?.Trim().ToLowerInvariant() ?? "";
        }

        if (string.IsNullOrEmpty(host))
            return false;

        // 4. Strict domain boundary check (exact match or subdomain. Never loose substring matching)
        bool isAllowedDomain = false;
        foreach (var allowed in AllowedDomains)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase) || 
                host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            {
                isAllowedDomain = true;
                break;
            }
        }

        if (!isAllowedDomain)
            return false;

        // If there's a hostname mismatch, reject immediately to prevent domain spoofing
        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            return false;

        // 5. Verify certificate chain against official Russian CA thumbprints cryptographically
        if (chain != null && chain.ChainElements.Count > 0)
        {
            // Verify that only UntrustedRoot / PartialChain errors exist, no revocation or corruption
            foreach (var status in chain.ChainStatus)
            {
                if (status.Status != X509ChainStatusFlags.UntrustedRoot && 
                    status.Status != X509ChainStatusFlags.PartialChain &&
                    status.Status != X509ChainStatusFlags.NoError)
                {
                    // Revoked, expired, untrusted constraint, or corrupted certificate
                    return false;
                }
            }

            // The root or an issuing authority in the chain MUST match an official Russian CA thumbprint
            bool hasVerifiedCaInChain = false;
            foreach (var element in chain.ChainElements)
            {
                var cert = element.Certificate;
                string sha256 = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
                string sha1 = cert.Thumbprint;

                if (KnownRussianCaThumbprints.Contains(sha256) || KnownRussianCaThumbprints.Contains(sha1))
                {
                    hasVerifiedCaInChain = true;
                    break;
                }
            }

            if (hasVerifiedCaInChain)
                return true;
        }

        // Direct certificate thumbprint check against official root CAs
        string certSha256 = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (KnownRussianCaThumbprints.Contains(certSha256) || KnownRussianCaThumbprints.Contains(certificate.Thumbprint))
        {
            return true;
        }

        return false;
    }
}
