using System;
using Fastcull.ViewModels;
using Xunit;

namespace Fastcull.Tests
{
    /// <summary>
    /// The Design/ handoff calls the equal-height rule load-bearing and says the prototype got it
    /// wrong twice. The sample corpus is all landscape 3:2, so the interesting cases - portrait
    /// beside landscape, height-constrained vs. width-constrained - never appear on screen to be
    /// checked by eye. These tests are the only thing that actually verifies them.
    /// </summary>
    public class StageLayoutTests
    {
        [Fact]
        public void AllPhotosGetTheSameHeight_RegardlessOfAspect()
        {
            // A portrait, a square and a wide landscape sharing a stage.
            var aspects = new[] { 2.0 / 3.0, 1.0, 1.5 };
            var shared = StageLayout.ComputeSharedHeight(cellWidth: 430, cellHeight: 400, aspects);

            // Sized by the widest (1.5): 430 / 1.5 = 286.67, which is under the 400 of height.
            Assert.Equal(430.0 / 1.5, shared, precision: 6);

            // Every photo takes that one height; only the widths differ.
            foreach (var aspect in aspects)
                Assert.Equal(shared * aspect, StageLayout.PhotoWidth(shared, aspect), precision: 6);
        }

        [Fact]
        public void WidestPhotoNeverOverflowsItsCell()
        {
            var aspects = new[] { 0.5, 1.0, 2.4 };
            const double cellWidth = 430;

            var shared = StageLayout.ComputeSharedHeight(cellWidth, cellHeight: 10_000, aspects);

            foreach (var aspect in aspects)
                Assert.True(StageLayout.PhotoWidth(shared, aspect) <= cellWidth + 1e-9,
                    $"aspect {aspect} overflowed its cell");
        }

        [Fact]
        public void HeightConstrainedWhenTheCellIsTallAndNarrow()
        {
            // Portrait-only stage in a short cell: height wins over width.
            var shared = StageLayout.ComputeSharedHeight(cellWidth: 900, cellHeight: 200, new[] { 0.667 });
            Assert.Equal(200, shared, precision: 6);
        }

        [Fact]
        public void WidthConstrainedWhenTheCellIsShortAndWide()
        {
            var shared = StageLayout.ComputeSharedHeight(cellWidth: 300, cellHeight: 900, new[] { 1.5 });
            Assert.Equal(200, shared, precision: 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void UndecodedOrCorruptAspectFallsBackInsteadOfPoisoningTheResult(double bad)
        {
            // A photo whose decode has not landed yet reports a non-usable aspect. It must not
            // drag the shared height to zero, and must not turn the division into Infinity.
            var shared = StageLayout.ComputeSharedHeight(430, 400, new[] { bad });

            Assert.True(shared > 0 && double.IsFinite(shared));
            Assert.Equal(430.0 / StageLayout.DefaultAspectRatio, shared, precision: 6);
        }

        [Fact]
        public void OneUndecodedPhotoDoesNotShrinkTheOthers()
        {
            var withUnknown = StageLayout.ComputeSharedHeight(430, 400, new[] { 1.5, 0.0, 1.5 });
            var allKnown = StageLayout.ComputeSharedHeight(430, 400, new[] { 1.5, 1.5, 1.5 });

            Assert.Equal(allKnown, withUnknown, precision: 6);
        }

        [Fact]
        public void CellWidthRemovesOneFewerGapsThanColumns()
        {
            // Three columns have two gaps between them, not three. Getting this wrong is the
            // trap the work order called out - it silently mis-sizes every photo on stage.
            Assert.Equal((1440.0 - 10) / 3, StageLayout.ComputeCellWidth(1440, columnSpacing: 5), precision: 6);
            Assert.Equal((1440.0 - 68) / 3, StageLayout.ComputeCellWidth(1440, columnSpacing: 34), precision: 6);
        }

        [Fact]
        public void TighterSpacingProducesWiderCells()
        {
            // The whole point of dropping the spacing from 34 to 5 is more photo per cell.
            var before = StageLayout.ComputeCellWidth(1440, columnSpacing: 34);
            var after = StageLayout.ComputeCellWidth(1440, columnSpacing: 5);

            Assert.True(after > before, $"expected tighter spacing to widen cells: {before} -> {after}");
        }

        [Fact]
        public void CellWidthHandlesDegenerateInput()
        {
            Assert.Equal(0, StageLayout.ComputeCellWidth(0, 5));
            Assert.Equal(0, StageLayout.ComputeCellWidth(1440, 5, columnCount: 0));
            Assert.Equal(0, StageLayout.ComputeCellWidth(4, 5));   // gaps exceed the stage

            // A spacing that never got a real value must not poison the arithmetic.
            Assert.Equal(1440.0 / 3, StageLayout.ComputeCellWidth(1440, double.NaN), precision: 6);
        }

        [Fact]
        public void NoRoomOrNoPhotosYieldsZeroRatherThanNegativeLayout()
        {
            Assert.Equal(0, StageLayout.ComputeSharedHeight(0, 400, new[] { 1.5 }));
            Assert.Equal(0, StageLayout.ComputeSharedHeight(430, 0, new[] { 1.5 }));
            Assert.Equal(0, StageLayout.ComputeSharedHeight(430, 400, Array.Empty<double>()));
            Assert.Equal(0, StageLayout.PhotoWidth(0, 1.5));
        }
    }
}
