using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Per-symbol cash sleeve for the manually tracked equity position (MBB-05).
/// Holds the user-entered spendable cash in VND. Cash is entered as a fact —
/// the system never infers position size or cash from a target NAV.
/// One row per symbol (unique index).
/// </summary>
public class EquityPositionAccount
{
    public long Id { get; set; }

    /// <summary>Instrument symbol. v1 scope: "MBB" only.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Spendable (settled) cash available for further buys, in VND.</summary>
    [Column(TypeName = "numeric")]
    public decimal AvailableCashVnd { get; set; }

    /// <summary>
    /// Declared buy-more budget for this symbol, drawn from the shared EquityCashAccount pool (EQX v2 A3).
    /// Null = undeclared ⇒ no buy quantity is proposed. AvailableCashVnd above is legacy and no longer
    /// used for buying power (per-symbol cash double-counted the same broker balance).
    /// </summary>
    [Column(TypeName = "numeric")]
    public decimal? BuyBudgetVnd { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
