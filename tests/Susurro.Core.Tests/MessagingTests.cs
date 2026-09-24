using Susurro.Core.Messaging;

namespace Susurro.Core.Tests;

public class MessagingTests
{
    [Theory]
    [InlineData("  hola  ", "hola")]
    [InlineData("línea1\r\nlínea2", "línea1 línea2")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("a\u0007b", "ab")]
    [InlineData("x‮y", "xy")]
    [InlineData("Traé   los papeles", "Traé los papeles")]
    public void Sanitize_cleans_text(string input, string expected)
    {
        Assert.Equal(expected, MessageRules.Sanitize(input));
    }

    [Fact]
    public void Validate_rejects_empty_and_too_long()
    {
        Assert.False(MessageRules.TryValidate("   ", out _, out var e1));
        Assert.NotNull(e1);
        Assert.False(MessageRules.TryValidate(new string('x', MessageRules.MaxLength + 1), out _, out _));
        Assert.True(MessageRules.TryValidate(new string('x', MessageRules.MaxLength), out var ok, out _));
        Assert.Equal(MessageRules.MaxLength, ok.Length);
    }

    [Fact]
    public void Message_ids_are_validated()
    {
        Assert.True(MessageRules.IsValidMessageId(MessageRules.NewMessageId()));
        Assert.False(MessageRules.IsValidMessageId("zz"));
        Assert.False(MessageRules.IsValidMessageId(null));
        Assert.False(MessageRules.IsValidMessageId(new string('g', 32)));
    }

    [Fact]
    public void Duplicate_filter_detects_repeats_and_is_bounded()
    {
        var f = new DuplicateFilter(3);
        Assert.True(f.TryRegister("a"));
        Assert.False(f.TryRegister("a"));
        f.TryRegister("b");
        f.TryRegister("c");
        f.TryRegister("d"); // expulsa "a"
        Assert.Equal(3, f.Count);
        Assert.True(f.TryRegister("a"));
    }

    private static WhisperMessage Msg(string text, long seq, bool urgent = false, int secondsOffset = 0, string sender = "Oficina") =>
        new(MessageRules.NewMessageId(), text, sender, DateTimeOffset.UtcNow.AddSeconds(secondsOffset), urgent, seq, false);

    [Fact]
    public void Display_queue_shows_one_at_a_time_in_order()
    {
        var q = new DisplayQueue();
        q.Enqueue(Msg("uno", 1));
        q.Enqueue(Msg("dos", 2));
        var first = q.BeginNext();
        Assert.Equal("uno", first!.Text);
        Assert.Null(q.BeginNext()); // no se superponen
        q.CompleteCurrent();
        Assert.Equal("dos", q.BeginNext()!.Text);
        q.CompleteCurrent();
        Assert.Null(q.BeginNext());
    }

    [Fact]
    public void Display_queue_reorders_out_of_order_messages_by_sequence()
    {
        var q = new DisplayQueue();
        var now = DateTimeOffset.UtcNow;
        q.Enqueue(Msg("tres", 3) with { SentAt = now });
        q.Enqueue(Msg("uno", 1) with { SentAt = now });
        q.Enqueue(Msg("dos", 2) with { SentAt = now });
        Assert.Equal(new[] { "uno", "dos", "tres" }, q.Pending.Select(m => m.Text));
    }

    [Fact]
    public void Display_queue_orders_by_send_time_across_restarts()
    {
        var q = new DisplayQueue();
        q.Enqueue(Msg("nuevo tras reinicio", 1, secondsOffset: 0));
        q.Enqueue(Msg("viejo", 50, secondsOffset: -30));
        Assert.Equal("viejo", q.Pending[0].Text);
    }

    [Fact]
    public void Urgent_messages_jump_ahead_of_pending_normal_but_do_not_interrupt()
    {
        var q = new DisplayQueue();
        q.Enqueue(Msg("normal 1", 1));
        q.BeginNext();
        q.Enqueue(Msg("normal 2", 2));
        q.Enqueue(Msg("URGENTE", 3, urgent: true));
        Assert.Equal("normal 1", q.Current!.Text);
        q.CompleteCurrent();
        Assert.Equal("URGENTE", q.BeginNext()!.Text);
    }

    [Fact]
    public void Display_queue_is_bounded_and_drops_oldest_normal()
    {
        var q = new DisplayQueue(capacity: 2);
        q.Enqueue(Msg("a", 1));
        q.Enqueue(Msg("b", 2, urgent: true));
        var dropped = q.Enqueue(Msg("c", 3));
        Assert.Equal("a", dropped!.Text);
        Assert.Equal(2, q.PendingCount);
    }
}
