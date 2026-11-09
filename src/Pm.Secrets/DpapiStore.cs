using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Pm.Secrets;

/// <summary>DPAPI: the ciphertext is bound to the current Windows user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiStore : FileSecretStore
{
    public DpapiStore() : base(".bin") { }

    public override string Name => "Windows DPAPI";
    public override bool Secure => true;

    protected override byte[] Protect(byte[] value) =>
        ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser);

    protected override byte[] Unprotect(byte[] value) =>
        ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser);
}
