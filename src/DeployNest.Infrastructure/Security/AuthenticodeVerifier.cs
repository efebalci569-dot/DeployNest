using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeployNest.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Security;

/// <summary>
/// Verifies Authenticode signatures with the Windows WinVerifyTrust API (the same check Windows
/// performs for "verified publisher" prompts) and reads the signer certificate subject.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AuthenticodeVerifier(ILogger<AuthenticodeVerifier> logger) : ISignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x80;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CertERevocationFailure = unchecked((int)0x800B010E);
    private const int CryptERevocationOffline = unchecked((int)0x80092013);

    public SignatureCheck Verify(string filePath)
    {
        var result = WinVerifyTrustFile(filePath);
        var revocationUnchecked = result is CertERevocationFailure or CryptERevocationOffline;

        if (result is TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown)
        {
            logger.LogInformation("İmzasız dosya: {File}", Path.GetFileName(filePath));
            return new SignatureCheck(SignatureStatus.NotSigned, null, $"0x{result:X8}");
        }

        if (result != 0 && !revocationUnchecked)
        {
            logger.LogWarning("Geçersiz imza: {File} (0x{Code:X8})", Path.GetFileName(filePath), result);
            return new SignatureCheck(SignatureStatus.Invalid, ReadSigner(filePath), $"0x{result:X8}");
        }

        var subject = ReadSigner(filePath);
        if (subject is null)
            return new SignatureCheck(SignatureStatus.Error, null, "signer-unreadable");

        logger.LogInformation("Geçerli imza: {File} — {Subject}", Path.GetFileName(filePath), subject);
        return new SignatureCheck(SignatureStatus.Valid, subject, revocationUnchecked ? "revocation-unchecked" : null);
    }

    private string? ReadSigner(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Reading the embedded Authenticode signer has no non-obsolete equivalent.
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException ex)
        {
            logger.LogDebug(ex, "İmzalayan sertifika okunamadı");
            return null;
        }
    }

    private static int WinVerifyTrustFile(string filePath)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = pathPtr,
        };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = WtdRevokeWholeChain,
            dwUnionChoice = WtdChoiceFile,
            pFile = fileInfoPtr,
            dwStateAction = WtdStateActionVerify,
            dwProvFlags = WtdRevocationCheckChainExcludeRoot,
        };

        var action = GenericVerifyV2;
        try
        {
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WinTrustData pWvtData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
