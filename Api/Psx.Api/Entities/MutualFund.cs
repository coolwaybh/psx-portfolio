namespace Psx.Api.Entities;

public class MutualFund
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string Name { get; set; } = "";
    public string Amc { get; set; } = "";
    public string Category { get; set; } = "";

    // Latest known NAV, kept in sync with the most recent FundNavHistory row for this
    // fund (updated together in the same nav-updates transaction, never independently -
    // see POST /api/funds/nav-updates) so callers that only need "today's value" don't
    // have to query the history table.
    public decimal CurrentNav { get; set; }
    public DateOnly NavUpdatedAt { get; set; }
}
