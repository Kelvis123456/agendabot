using System.Net;

namespace AgendaBot.Tests;

[Collection("api")]
public class HealthTests(ApiFactory api)
{
    [Fact]
    public async Task Health_responde_ok_con_la_base_arriba()
    {
        var res = await api.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
