using AgendaBot.Api.WhatsApp;
using Microsoft.Extensions.Options;

namespace AgendaBot.Tests;

public class PhoneRateLimiterTests
{
    [Fact]
    public void Corta_al_pasar_el_tope_y_cada_numero_lleva_su_cuenta()
    {
        var limiter = new PhoneRateLimiter(Options.Create(new WhatsAppOptions { MessagesPerPhonePer10Min = 3 }));

        Assert.All(Enumerable.Range(0, 3), _ => Assert.True(limiter.TryAcquire("18095550001")));
        Assert.False(limiter.TryAcquire("18095550001"));
        Assert.True(limiter.TryAcquire("18095550002"));
    }
}
