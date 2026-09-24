using System.Security.Cryptography;
using Susurro.Core.Pairing;

namespace Susurro.Core.Tests;

public class PairingTests
{
    [Fact]
    public void Generated_codes_are_valid_and_random()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => PairingCode.Generate()).ToList();
        Assert.All(codes, c => Assert.Equal(c, PairingCode.Normalize(c)));
        Assert.True(codes.Distinct().Count() > 195);
    }

    [Theory]
    [InlineData("k7qm-4xpd", "K7QM4XPD")]
    [InlineData(" K7QM 4XPD ", "K7QM4XPD")]
    [InlineData("O1LI-2345", "01112345")]
    public void Normalize_accepts_human_variations(string input, string expected)
    {
        Assert.Equal(expected, PairingCode.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("K7QM4XP")]
    [InlineData("K7QM4XPDD")]
    [InlineData("K7QM-4XP!")]
    [InlineData("UUUUUUUU")] // U no pertenece al alfabeto de Crockford
    public void Normalize_rejects_invalid(string input)
    {
        Assert.Null(PairingCode.Normalize(input));
    }

    [Fact]
    public void Format_inserts_dash()
    {
        Assert.Equal("K7QM-4XPD", PairingCode.Format("K7QM4XPD"));
    }

    [Fact]
    public void Both_sides_derive_the_same_pair_key()
    {
        using var joiner = new PairingKeyExchange();
        using var host = new PairingKeyExchange();
        var nj = RandomNumberGenerator.GetBytes(16);
        var nh = RandomNumberGenerator.GetBytes(16);
        var idJ = new string('a', 32);
        var idH = new string('b', 32);
        var sharedJ = joiner.DeriveShared(host.PublicKey);
        var sharedH = host.DeriveShared(joiner.PublicKey);
        Assert.Equal(sharedJ, sharedH);

        var t = PairingCrypto.Transcript(idJ, idH, joiner.PublicKey, host.PublicKey, nj, nh);
        const string code = "K7QM4XPD";
        var ck = PairingCrypto.ConfirmKey(code, t);
        Assert.Equal(PairingCrypto.ConfirmProof(ck, 'J', t, sharedJ), PairingCrypto.ConfirmProof(ck, 'J', t, sharedH));
        Assert.NotEqual(PairingCrypto.ConfirmProof(ck, 'J', t, sharedJ), PairingCrypto.ConfirmProof(ck, 'H', t, sharedJ));
        Assert.Equal(PairingCrypto.PairKey(sharedJ, t, code), PairingCrypto.PairKey(sharedH, t, code));
        Assert.Equal(32, PairingCrypto.PairKey(sharedJ, t, code).Length);
    }

    [Fact]
    public void Wrong_code_produces_different_proof()
    {
        using var a = new PairingKeyExchange();
        using var b = new PairingKeyExchange();
        var shared = a.DeriveShared(b.PublicKey);
        var t = PairingCrypto.Transcript(new string('a', 32), new string('b', 32), a.PublicKey, b.PublicKey, new byte[16], new byte[16]);
        var good = PairingCrypto.ConfirmProof(PairingCrypto.ConfirmKey("K7QM4XPD", t), 'J', t, shared);
        var bad = PairingCrypto.ConfirmProof(PairingCrypto.ConfirmKey("K7QM4XPE", t), 'J', t, shared);
        Assert.NotEqual(good, bad);
    }

    [Fact]
    public void Man_in_the_middle_with_different_shared_secret_cannot_reuse_proof()
    {
        using var joiner = new PairingKeyExchange();
        using var host = new PairingKeyExchange();
        using var mitm = new PairingKeyExchange();
        var t = PairingCrypto.Transcript(new string('a', 32), new string('b', 32), joiner.PublicKey, host.PublicKey, new byte[16], new byte[16]);
        var ck = PairingCrypto.ConfirmKey("K7QM4XPD", t);
        var proofWithMitm = PairingCrypto.ConfirmProof(ck, 'J', t, joiner.DeriveShared(mitm.PublicKey));
        var expectedByHost = PairingCrypto.ConfirmProof(ck, 'J', t, host.DeriveShared(joiner.PublicKey));
        Assert.NotEqual(proofWithMitm, expectedByHost);
    }

    [Fact]
    public void Invitation_expires_and_is_exhausted_after_max_failures()
    {
        var inv = new PairingInvitation("K7QM4XPD", DateTime.UtcNow.AddMinutes(5));
        Assert.True(inv.IsValid(DateTime.UtcNow));
        Assert.False(inv.IsValid(DateTime.UtcNow.AddMinutes(6)));
        for (var i = 1; i < PairingInvitation.MaxFailures; i++) Assert.False(inv.RegisterFailure());
        Assert.True(inv.RegisterFailure());
        Assert.False(inv.IsValid(DateTime.UtcNow));
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
