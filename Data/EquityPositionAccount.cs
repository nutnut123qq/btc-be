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

    public DateTime UpdatedAtUtc { get; set; }
}
