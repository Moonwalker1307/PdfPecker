using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PdfPecker.Cli;

/// <summary>Commands about who made a PDF and who may open it: sign, verify, encrypt and decrypt.</summary>
internal static class SecurityCommands
{
    private const string CertPasswordEnv = "PDFPECKER_CERT_PASSWORD";

    public static int Sign(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "sign <input.pdf> <output.pdf> --cert <certificate.pfx> [--cert-password-env VAR] [--reason TEXT] [--page N --rect x,y,width,height]";
        var a = Common.WithPassword(new OptionSet())
            .Value("cert").Value("cert-password").Value("cert-password-env")
            .Value("reason").Value("location").Value("contact").Value("name").Value("field")
            .Value("page").Value("rect").Value("text").Value("certify").Value("format").Value("hash").Value("timestamp-url")
            .Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        var certPath = a.Require("cert");

        var certPassword = Common.Password(a, "cert-password", "cert-password-env", CertPasswordEnv);
        using var certificate = LoadCertificate(certPath, certPassword);
        var options = new PdfSignatureOptions(certificate)
        {
            Reason = a.Get("reason"),
            Location = a.Get("location"),
            ContactInfo = a.Get("contact"),
            SignerName = a.Get("name"),
            FieldName = a.Get("field"),
            TimestampUrl = a.Get("timestamp-url"),
        };
        if (a.Get("certify") is { } certify) options.Certification = ParseCertification(certify);
        if (a.Get("format") is { } format)
            options.Format = format.ToLowerInvariant() switch
            {
                "pades" => PdfSignatureFormat.PAdES,
                "pkcs7" or "cms" => PdfSignatureFormat.Pkcs7Detached,
                _ => throw new UsageException($"--format must be pades or pkcs7, not '{format}'."),
            };
        if (a.Get("hash") is { } hash)
            options.HashAlgorithm = hash.ToLowerInvariant().Replace("-", "") switch
            {
                "sha256" => HashAlgorithmName.SHA256,
                "sha384" => HashAlgorithmName.SHA384,
                "sha512" => HashAlgorithmName.SHA512,
                _ => throw new UsageException($"--hash must be sha256, sha384 or sha512, not '{hash}'."),
            };
        if (a.Has("page") || a.Has("rect") || a.Has("text"))
        {
            var appearance = new PdfSignatureAppearance { Page = a.GetInt("page") ?? 1, Text = a.Get("text") };
            if (a.Get("rect") is { } rect)
            {
                var parts = rect.Split(',');
                if (parts.Length != 4 || parts.Any(p => !float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    throw new UsageException($"--rect must be four numbers in points, x,y,width,height, not '{rect}'.");
                float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
                appearance.X = F(0);
                appearance.Y = F(1);
                appearance.Width = F(2);
                appearance.Height = F(3);
            }

            options.Appearance = appearance;
        }

        var signed = PdfSigner.Sign(Common.ReadPdf(io, positionals[0]), options, Common.Password(a), ct);
        io.WriteOutput(output, signed);
        Common.Report(io, output, signed, "Signed");
        return 0;
    }

    private static PdfCertificationLevel ParseCertification(string s) => s.ToLowerInvariant() switch
    {
        "none" => PdfCertificationLevel.None,
        "no-changes" => PdfCertificationLevel.NoChanges,
        "forms" => PdfCertificationLevel.FormFilling,
        "forms-and-comments" or "annotations" => PdfCertificationLevel.FormFillingAndAnnotations,
        _ => throw new UsageException($"--certify must be none, no-changes, forms or annotations, not '{s}'."),
    };

    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
#else
        return new X509Certificate2(path, password);
#endif
    }

    private static X509Certificate2 LoadPublicCertificate(string path)
    {
        var bytes = File.ReadAllBytes(path);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadCertificate(bytes);
#else
        return new X509Certificate2(bytes);
#endif
    }

    public static int Verify(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "verify <input.pdf> [--trusted-root <ca.cer>]... [--allow-sha1] [--check-revocation]";
        var a = new OptionSet().Value("password").Value("password-env").Flag("quiet|q")
            .Value("trusted-root").Flag("allow-sha1").Flag("check-revocation").Parse(args);
        var input = Common.Positionals(a, 1, 1, usage)[0];

        var options = new PdfSignatureVerificationOptions { AllowSha1 = a.Has("allow-sha1") };
        if (a.Has("check-revocation")) options.RevocationMode = X509RevocationMode.Online;
        var roots = a.GetAll("trusted-root").ToList();
        if (roots.Count > 0)
        {
            var collection = new X509Certificate2Collection();
            foreach (var root in roots) collection.Add(LoadPublicCertificate(root));
            options.TrustedRoots = collection;
        }

        var results = PdfSignatureVerifier.Verify(Common.ReadPdf(io, input), options, Common.Password(a), ct);
        if (results.Count == 0)
        {
            io.Error.WriteLine("The PDF has no signatures.");
            return 1;
        }

        bool allValid = true;
        foreach (var r in results)
        {
            allValid &= r.Valid;
            var kind = r.IsDocumentTimestamp ? "document timestamp" : "signature";
            io.Output.Write(System.Text.Encoding.UTF8.GetBytes(
                $"{(r.Valid ? "VALID  " : "INVALID")} {kind} {r.FieldName}\n"
                + $"  signer:   {r.SignerName ?? r.SignerCertificate?.Subject ?? "unknown"}\n"
                + (r.SigningTime is { } t ? $"  time:     {t.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}\n" : "")
                + (r.Reason != null ? $"  reason:   {r.Reason}\n" : "")
                + $"  intact:   {YesNo(r.IsIntact)}   whole document: {YesNo(r.CoversWholeDocument)}   trusted chain: {YesNo(r.ChainTrusted)}\n"
                + string.Concat(r.Problems.Select(p => "  problem:  " + p + "\n"))
                + string.Concat(r.ChainErrors.Select(p => "  chain:    " + p + "\n"))));
        }

        io.Output.Flush();
        return allValid ? 0 : 1;
    }

    private static string YesNo(bool value) => value ? "yes" : "NO";

    public static int Encrypt(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "encrypt <input.pdf> <output.pdf> --user-password PW [--owner-password PW] [--allow print,copy,modify,annotate,forms,accessibility,assemble]";
        var a = Common.WithPassword(new OptionSet())
            .Value("user-password").Value("owner-password").Value("user-password-env").Value("owner-password-env").Value("allow").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);

        var security = new PdfSecurity
        {
            UserPassword = Common.Password(a, "user-password", "user-password-env"),
            OwnerPassword = Common.Password(a, "owner-password", "owner-password-env"),
        };
        if (security.UserPassword == null && security.OwnerPassword == null)
            throw new UsageException("Give a password with --user-password or --owner-password (or the -env forms, which read an environment variable).");
        if (a.Get("allow") is { } allow) security.Permissions = ParsePermissions(allow);

        var result = PdfProtector.Encrypt(Common.ReadPdf(io, positionals[0]), security, Common.Password(a), ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result, "Encrypted");
        return 0;
    }

    private static PdfPermissions ParsePermissions(string list)
    {
        var permissions = PdfPermissions.None;
        foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            permissions |= item.ToLowerInvariant() switch
            {
                "print" => PdfPermissions.Print | PdfPermissions.PrintHighQuality,
                "copy" => PdfPermissions.Copy,
                "modify" => PdfPermissions.Modify,
                "annotate" => PdfPermissions.Annotate,
                "forms" => PdfPermissions.FillForms,
                "accessibility" => PdfPermissions.ExtractForAccessibility,
                "assemble" => PdfPermissions.Assemble,
                "all" => PdfPermissions.All,
                "none" => PdfPermissions.None,
                _ => throw new UsageException($"--allow lists print, copy, modify, annotate, forms, accessibility, assemble, all or none, not '{item}'."),
            };
        }

        return permissions;
    }

    public static int Decrypt(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "decrypt <input.pdf> <output.pdf> --password PW";
        var a = Common.WithPassword(new OptionSet()).Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        var result = PdfProtector.Decrypt(Common.ReadPdf(io, positionals[0]), Common.Password(a), ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result, "Decrypted");
        return 0;
    }
}
