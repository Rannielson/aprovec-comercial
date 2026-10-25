namespace Recorrencia.Domain.Commissions;

public sealed record BoletoVehicleContract(Guid BoletoId, int Order, DateOnly? ContractDate);
public sealed record ConsultantContractCutoff(Guid UserId, DateOnly CutoffDate);

public static class CommissionContractCutoff
{
    /// <summary>
    /// A selected consultant earns upline commission only on vehicles contracted
    /// after their cutoff. Their own 7% and global entries are never changed.
    /// </summary>
    public static IReadOnlyList<CommissionEntry> Apply(
        IReadOnlyList<CommissionEntry> entries,
        IReadOnlyList<BoletoVehicleContract> contracts,
        IReadOnlyList<ConsultantContractCutoff> cutoffs)
    {
        if (cutoffs.Count == 0) return entries;

        var datesByBoleto = contracts.GroupBy(c => c.BoletoId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Order).ToList());
        var cutoffByUser = cutoffs.ToDictionary(c => c.UserId, c => c.CutoffDate);
        var result = new List<CommissionEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (entry.RuleType != RuleType.Upline || !cutoffByUser.TryGetValue(entry.BeneficiaryId, out var cutoff))
            {
                result.Add(entry);
                continue;
            }

            if (!datesByBoleto.TryGetValue(entry.BoletoId, out var vehicles) || vehicles.Count == 0 ||
                vehicles.Any(v => v.ContractDate is null))
                throw new InvalidOperationException($"commission.contract_date_missing:{entry.BoletoId}");

            // SGA supplies one payment for the complete boleto, with no amount per
            // vehicle. Each vehicle receives an equal cent-precise share; the last
            // receives the rounding remainder, so the commission base is conserved.
            var cents = decimal.Round(entry.Base * 100m, 0, MidpointRounding.ToEven);
            var wholeShare = decimal.Truncate(cents / vehicles.Count);
            var remainder = (int)(cents - wholeShare * vehicles.Count);
            decimal newBase = 0;
            for (var i = 0; i < vehicles.Count; i++)
            {
                var share = (wholeShare + (i < remainder ? 1 : 0)) / 100m;
                if (vehicles[i].ContractDate > cutoff) newBase += share;
            }

            if (newBase > 0)
                result.Add(entry with
                {
                    Base = newBase,
                    Amount = Math.Round(newBase * entry.Rate, 2, MidpointRounding.ToEven),
                });
        }
        return result;
    }
}
