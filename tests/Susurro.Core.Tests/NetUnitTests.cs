using Susurro.Core.Net;

namespace Susurro.Core.Tests;

public class NetUnitTests
{
    private const string Low = "00000000000000000000000000000001";
    private const string High = "ffffffffffffffffffffffffffffffff";

    [Fact]
    public void Arbiter_preferred_connection_is_dialed_by_lower_id()
    {
        Assert.True(SessionArbiter.IsPreferred(Low, Low, High));
        Assert.True(SessionArbiter.IsPreferred(Low, High, Low));
        Assert.False(SessionArbiter.IsPreferred(High, Low, High));
    }

    [Theory]
    // existente, candidata -> ¿reemplaza?
    [InlineData(High, Low, true)]   // llega la preferida: reemplaza
    [InlineData(Low, High, false)]  // llega la no preferida con una preferida viva: se rechaza
    [InlineData(Low, Low, true)]    // la preferida se re-marcó: la vieja está muerta
    [InlineData(High, High, true)]  // la no preferida se re-marcó: la vieja está muerta
    public void Arbiter_rules(string existingDialer, string candidateDialer, bool expected)
    {
        Assert.Equal(expected, SessionArbiter.ShouldReplace(existingDialer, candidateDialer, Low, High));
        // ¡Y ambos extremos llegan a la misma conclusión!
        Assert.Equal(expected, SessionArbiter.ShouldReplace(existingDialer, candidateDialer, High, Low));
    }

    [Fact]
    public void Arbiter_converges_regardless_of_arrival_order()
    {
        // Ambos lados terminan con la conexión marcada por Low, sin importar el orden de llegada.
        string Resolve(string first, string second) =>
            SessionArbiter.ShouldReplace(first, second, Low, High) ? second : first;
        Assert.Equal(Low, Resolve(Low, High));
        Assert.Equal(Low, Resolve(High, Low));
    }

    [Fact]
    public void Backoff_grows_caps_and_resets()
    {
        var steps = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };
        var b = new Backoff(steps, jitter: 0);
        Assert.Equal(1, b.Next().TotalSeconds);
        Assert.Equal(2, b.Next().TotalSeconds);
        Assert.Equal(4, b.Next().TotalSeconds);
        Assert.Equal(4, b.Next().TotalSeconds);
        Assert.Equal(4, b.Failures);
        b.Reset();
        Assert.Equal(1, b.Next().TotalSeconds);
    }

    [Fact]
    public void Backoff_jitter_stays_within_bounds()
    {
        var b = new Backoff(new[] { TimeSpan.FromSeconds(10) }, jitter: 0.2);
        for (var i = 0; i < 100; i++)
        {
            var d = b.Next().TotalSeconds;
            Assert.InRange(d, 8, 12);
        }
    }

    [Theory]
    [InlineData("192.168.1.20", 47810, "192.168.1.20", 47810)]
    [InlineData("192.168.1.20:5000", 47810, "192.168.1.20", 5000)]
    [InlineData(" OFICINA ", 47810, "OFICINA", 47810)]
    public void Host_port_parsing(string input, int def, string host, int port)
    {
        Assert.True(NetworkInfo.TryParseHostPort(input, def, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4:0")]
    [InlineData("1.2.3.4:99999")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("dos palabras")]
    public void Host_port_parsing_rejects_invalid(string input)
    {
        Assert.False(NetworkInfo.TryParseHostPort(input, 47810, out _, out _));
    }
}
