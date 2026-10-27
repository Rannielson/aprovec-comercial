namespace Recorrencia.Api.Integracoes;

public static class HinovaBoletoStatus
{
    public static bool IsPaid(HinovaBoleto b) => b.DataPagamento is not null && b.ValorPagamento > 0;

    public static bool IsClosed(HinovaBoleto b)
    {
        var status = b.SituacaoBoleto?.Trim().ToUpperInvariant() ?? "";
        return status.Contains("CANCEL") || status.Contains("EXCLU") || status.Contains("ESTORN");
    }

    // An unknown state or an incomplete settlement must never authorize collection.
    public static bool CanCollect(HinovaBoleto b) => !IsPaid(b) && !IsClosed(b)
        && b.DataPagamento is null && b.ValorBoleto > 0
        && b.SituacaoBoleto?.Trim().ToUpperInvariant() is "ABERTO" or "EM ABERTO" or "VENCIDO" or "A VENCER" or "ATRASADO";
}
