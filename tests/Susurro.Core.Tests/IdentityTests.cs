using System.Security.Cryptography;
using Susurro.Core.Config;
using Susurro.Core.Identity;

namespace Susurro.Core.Tests;

public class IdentityTests
{
    [Fact]
    public void Id_is_the_hash_of_the_public_key()
    {
        using var a = LocalIdentity.Create();
        using var b = LocalIdentity.Create();
        Assert.True(SettingsValidator.IsValidInstanceId(a.Id));
        Assert.Equal(a.Id.ToLowerInvariant(), a.Id);
        Assert.Equal(LocalIdentity.IdFromPublicKey(a.PublicKey), a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(LocalIdentity.Matches(a.Id, a.PublicKey));
        Assert.False(LocalIdentity.Matches(a.Id, b.PublicKey)); // nadie puede usar el id de otro con su propia clave
        Assert.False(LocalIdentity.Matches(a.Id, null));
        Assert.False(LocalIdentity.Matches(null, a.PublicKey));
    }

    [Fact]
    public void Both_sides_derive_the_same_link_key_and_a_third_one_cannot()
    {
        using var a = LocalIdentity.Create();
        using var b = LocalIdentity.Create();
        using var c = LocalIdentity.Create();
        var ab = a.DeriveLinkKey(b.Id, b.PublicKey);
        var ba = b.DeriveLinkKey(a.Id, a.PublicKey);
        Assert.Equal(32, ab.Length);
        Assert.Equal(ab, ba);
        Assert.NotEqual(ab, c.DeriveLinkKey(b.Id, b.PublicKey));
        Assert.NotEqual(ab, a.DeriveLinkKey(c.Id, c.PublicKey));
    }

    [Fact]
    public void Invalid_public_key_is_rejected()
    {
        using var a = LocalIdentity.Create();
        Assert.ThrowsAny<CryptographicException>(() => a.DeriveLinkKey(new string('b', 32), RandomNumberGenerator.GetBytes(91)));

        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var other = p384.PublicKey.ExportSubjectPublicKeyInfo();
        Assert.ThrowsAny<CryptographicException>(() => a.DeriveLinkKey(LocalIdentity.IdFromPublicKey(other), other));
    }

    [Fact]
    public void Load_or_create_keeps_the_identity_and_replaces_an_unreadable_one()
    {
        var protector = new PlainKeyProtector();
        using var first = LocalIdentity.LoadOrCreate(null, protector, out var saved);
        Assert.False(string.IsNullOrEmpty(saved));

        using var again = LocalIdentity.LoadOrCreate(saved, protector, out var saved2);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(saved, saved2);

        using var replaced = LocalIdentity.LoadOrCreate("no-es-base64!!", protector, out var saved3);
        Assert.NotEqual(first.Id, replaced.Id);
        Assert.NotEqual(saved, saved3);
    }

    [Fact]
    public void Dpapi_protector_roundtrips_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new DpapiKeyProtector();
        var key = RandomNumberGenerator.GetBytes(32);
        var prot = p.Protect(key);
        Assert.DoesNotContain(Convert.ToBase64String(key), prot);
        Assert.Equal(key, p.Unprotect(prot));
        Assert.Null(p.Unprotect("no-es-base64!!"));
        Assert.Null(p.Unprotect(Convert.ToBase64String(new byte[40])));
    }
}
