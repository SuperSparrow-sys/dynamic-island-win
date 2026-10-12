using DynamicBay.Settings;

namespace DynamicBay.Tests;

/// <summary>"Insel-Inhalte": dragging a widget by its grip between Nook, System and Aus.</summary>
public class WidgetBoardTests
{
    [Theory]
    [InlineData(-10, 0)]   // above the column: first
    [InlineData(10, 0)]    // upper half of the first chip: before it
    [InlineData(30, 1)]    // lower half of the first chip: after it
    [InlineData(95, 2)]
    [InlineData(500, 3)]   // below the column: last
    public void The_gap_opens_before_the_first_chip_whose_middle_is_below_the_pointer(double y, int index) =>
        Assert.Equal(index, BoardMath.InsertIndex(y, 44, 3));

    [Fact]
    public void Within_its_own_column_the_chip_itself_is_counted_for_Place()
    {
        // [A, B, C]: A (at 0) dragged behind B lands at insert position 1 among the others, Place wants 2.
        Assert.Equal(2, BoardMath.PlaceIndex(1, 0));
        // C (at 2) dragged to the top.
        Assert.Equal(0, BoardMath.PlaceIndex(0, 2));
        // From another column nothing shifts.
        Assert.Equal(1, BoardMath.PlaceIndex(1, -1));
    }
}
