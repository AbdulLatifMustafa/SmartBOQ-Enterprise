using SmartBOQ.Domain.Interfaces;

namespace SmartBOQ.Infrastructure.Matching;

/// <summary>
/// Resolves the exact target Excel anchor row index where the rate cell must be injected
/// to satisfy native Excel formula dependencies (Col H = Col E * Col G).
/// </summary>
public sealed class AnchorRowResolver : IAnchorRowResolver
{
    public int ResolveAnchorRow(
        IReadOnlyList<int> itemRowIndices, 
        Func<int, bool> hasQuantity, 
        Func<int, bool> hasAmountFormula)
    {
        if (itemRowIndices == null || itemRowIndices.Count == 0)
        {
            return 0;
        }

        if (itemRowIndices.Count == 1)
        {
            return itemRowIndices[0];
        }

        // 1. Primary candidate: Row has both quantity and amount formula
        foreach (int row in itemRowIndices)
        {
            if (hasQuantity(row) && hasAmountFormula(row))
            {
                return row;
            }
        }

        // 2. Secondary candidate: Row has quantity
        foreach (int row in itemRowIndices)
        {
            if (hasQuantity(row))
            {
                return row;
            }
        }

        // 3. Tertiary candidate: Row has amount formula
        foreach (int row in itemRowIndices)
        {
            if (hasAmountFormula(row))
            {
                return row;
            }
        }

        // 4. Default: The last row of the block (standard BOQ convention for summary row)
        return itemRowIndices[^1];
    }
}
