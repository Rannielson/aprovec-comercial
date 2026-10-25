import type { PagamentoBeneficiario, RuleTotal } from './types';

export type DashboardBreakdown = {
  direct: number;
  team: number;
  directRules: RuleTotal[];
  teamRules: RuleTotal[];
};

export function dashboardBreakdown(person: PagamentoBeneficiario): DashboardBreakdown {
  const directRules = person.regrasArvore.filter((rule) => rule.ruleType === 'own');
  const teamRules = person.regrasArvore.filter((rule) => rule.ruleType !== 'own');
  const team = teamRules.reduce((sum, rule) => sum + rule.amount, 0);

  // Derive the direct remainder from the authoritative payout total so per-rule
  // rounding never makes the headline disagree with the confirmed statement.
  return {
    direct: Math.round((person.total - team) * 100) / 100,
    team: Math.round(team * 100) / 100,
    directRules,
    teamRules,
  };
}
