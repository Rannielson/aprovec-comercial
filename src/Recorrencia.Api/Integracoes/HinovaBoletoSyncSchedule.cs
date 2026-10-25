namespace Recorrencia.Api.Integracoes;

public static class HinovaBoletoSyncSchedule
{
    // Include today (payments posted before 05:00) plus the five preceding dates.
    public static (DateOnly Inicio, DateOnly Fim)? Window(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/Recife"));
        if (local.Hour < 5) return null;
        var today = DateOnly.FromDateTime(local.DateTime);
        return (today.AddDays(-5), today);
    }
}
