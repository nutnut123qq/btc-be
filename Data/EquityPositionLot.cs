using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// One manually-entered MBB purchase lot (MBB-05). A lot is a single acquisition:
/// quantity + cost basis + the local Vietnam trade date it was acquired on.
/// Settlement is derived, not stored: a lot is sellable once
/// AcquireDate + T+2 TRADING days (VietnamMarketCalendar) is on/before today.
/// Quantities are user-entered facts — never inferred from cash/NAV.
/// </summary>
public class EquityPositionLot
{
    public long Id { get; set; }

    /// <summary>Instrument symbol. v1 scope: "MBB" only (enforced by the service).</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Shares acquired in this lot.</summary>
    public int Quantity { get; set; }

    /// <summary>Cost basis per share in VND, as entered by the user.</summary>
    [Column(TypeName = "numeric")]
    public decimal CostPerShareVnd { get; set; }

    /// <summary>Local (Asia/Ho_Chi_Minh) trade date the lot was acquired on.</summary>
    public DateOnly AcquireDate { get; set; }

    /// <summary>Entry provenance. v1 only accepts "manual".</summary>
    public string Source { get; set; } = "manual";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
