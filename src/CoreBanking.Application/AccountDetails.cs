using CoreBanking.Domain.Accounts;

namespace CoreBanking.Application;

public sealed record AccountDetails(Account Account, long BalanceMinor);
