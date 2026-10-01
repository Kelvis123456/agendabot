using AgendaBot.Api.Scheduling;

namespace AgendaBot.Tests;

public class ComputeSlotsTests
{
    private static readonly DateOnly Day = new(2030, 3, 4);
    private static readonly DateTime LongAgo = new(2000, 1, 1);

    private static List<string> Slots(int duration, IReadOnlyList<Busy>? busy = null, DateTime? now = null) =>
        Availability.ComputeSlots(Day, new(9, 0), new(11, 0), duration, 30, busy ?? [], now ?? LongAgo)
            .Select(s => s.ToString("HH:mm")).ToList();

    [Fact]
    public void Genera_slots_que_caben_antes_del_cierre()
    {
        Assert.Equal(["09:00", "09:30", "10:00", "10:30"], Slots(30));
        Assert.Equal(["09:00", "09:30", "10:00"], Slots(45));
    }

    [Fact]
    public void Salta_los_que_se_solapan_con_algo_ocupado()
    {
        var busy = new[] { new Busy(Day.ToDateTime(new(9, 30)), Day.ToDateTime(new(10, 0))) };
        Assert.Equal(["09:00", "10:00", "10:30"], Slots(30, busy));
        // Un servicio de 45 min a las 9:00 terminaría 9:45 y choca.
        Assert.Equal(["10:00"], Slots(45, busy));
    }

    [Fact]
    public void No_ofrece_horarios_que_ya_pasaron()
    {
        Assert.Equal(["10:00", "10:30"], Slots(30, now: Day.ToDateTime(new(9, 30))));
    }
}
