using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public class HinovaBoletoSyncScheduleTests
{
    [Fact]
    public void BeforeFiveInRecifeDoesNotSchedule() =>
        Assert.Null(HinovaBoletoSyncSchedule.Window(DateTimeOffset.Parse("2026-10-23T07:59:59Z")));

    [Theory]
    [InlineData("2026-10-23T08:00:00Z", "2026-10-18", "2026-10-23")]
    [InlineData("2026-11-02T08:00:00Z", "2026-10-28", "2026-11-02")]
    [InlineData("2027-01-01T08:00:00Z", "2026-12-27", "2027-01-01")]
    [InlineData("2026-10-24T02:59:59Z", "2026-10-18", "2026-10-23")]
    public void WindowUsesLocalDateAndCrossesMonthAndYear(string utc, string start, string end)
    {
        var window = HinovaBoletoSyncSchedule.Window(DateTimeOffset.Parse(utc));
        Assert.NotNull(window);
        Assert.Equal(DateOnly.Parse(start), window.Value.Inicio);
        Assert.Equal(DateOnly.Parse(end), window.Value.Fim);
    }
}
