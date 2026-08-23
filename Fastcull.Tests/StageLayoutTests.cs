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
    ///
    /// They now also cover the fit-the-set rule that replaced the equal-thirds one. Rotation is
    /// what made those cases reachable at runtime, and reaching them is how the equal-thirds bug
    /// (a 5px gap rendering as hundreds of pixels of black) got onto the screen at all.
    /// </summary>
    public class StageLayoutTests
    {
        private const double Landscape = 1.5;          // 3:2
        private const double Portrait = 2.0 / 3.0;     // 3:2 turned a quarter

        [Fact]
        public void AllPhotosGetTheSameHeight_RegardlessOfAspect()
        {
            var aspects = new[] { Portrait, 1.0, Landscape };
            var shared = StageLayout.ComputeSharedHeight(
                availableWidth: 1400, availableHeight: 10_000, totalGapWidth: 10, aspects);

            // Every photo takes that one height; only the widths differ.
            foreach (var aspect in aspects)
                Assert.Equal(shared * aspect, StageLayout.PhotoWidth(shared, aspect), precision: 6);
        }

        [Fact]
        public void TheSetExactlyFillsTheAvailableWidthWhenWidthConstrained()
        {
            // This is the property the whole rule exists for: sum of photo widths plus gaps
            // equals the width available, so no slack is left as dead black space.
            var aspects = new[] { Portrait, Landscape, Landscape };
            const double availableWidth = 1400;
            const double gaps = 10;

            var shared = StageLayout.ComputeSharedHeight(availableWidth, 10_000, gaps, aspects);
            var setWidth = StageLayout.ComputeSetWidth(shared, gaps, aspects);

            Assert.Equal(availableWidth, setWidth, precision: 6);
        }

        [Fact]
        public void MatchesTheOldEqualThirdsRuleWhenEveryPhotoSharesAnAspect()
        {
            // The common all-landscape case must not regress. Old rule: cellWidth/aspect where
            // cellWidth = (W - gaps)/3. New rule: (W - gaps)/sum(aspects) = (W - gaps)/(3*aspect).
            // Identical.
            foreach (var aspect in new[] { Landscape, 1.0, Portrait })
            {
                const double width = 1400;
                const double gaps = 10;
                var aspects = new[] { aspect, aspect, aspect };

                var oldRule = ((width - gaps) / 3) / aspect;
                var newRule = StageLayout.ComputeSharedHeight(width, 10_000, gaps, aspects);

                Assert.Equal(oldRule, newRule, precision: 6);
            }
        }

        [Fact]
        public void MixedAspectsProduceStrictlyTallerPhotosThanTheOldRule()
        {
            // One portrait beside two landscapes. The old rule sized everything to the widest
            // aspect and left the portrait's unused third black; this one spends that slack on
            // height for all three.
            var aspects = new[] { Portrait, Landscape, Landscape };
            const double width = 1400;
            const double gaps = 10;

            var oldRule = ((width - gaps) / 3) / Landscape;   // divide by the WIDEST
            var newRule = StageLayout.ComputeSharedHeight(width, 10_000, gaps, aspects);

            Assert.True(newRule > oldRule, $"expected taller photos: old {oldRule}, new {newRule}");
        }

        [Fact]
        public void AllPortraitSetIsSizedToFitTheWidthNotAThird()
        {
            var aspects = new[] { Portrait, Portrait, Portrait };
            const double width = 1400;
            const double gaps = 10;

            var shared = StageLayout.ComputeSharedHeight(width, 10_000, gaps, aspects);

            // sum = 2.0, so the height is (1400 - 10) / 2.
            Assert.Equal((width - gaps) / 2.0, shared, precision: 6);
            Assert.Equal(width, StageLayout.ComputeSetWidth(shared, gaps, aspects), precision: 6);
        }

        [Fact]
        public void HeightClampBindsForNarrowPhotosInAWideStage()
        {
            // Three portraits on an ultrawide: width would allow an enormous height, so the
            // available height has to win or the photos grow straight off the stage.
            var aspects = new[] { Portrait, Portrait, Portrait };
            var shared = StageLayout.ComputeSharedHeight(
                availableWidth: 5000, availableHeight: 400, totalGapWidth: 10, aspects);

            Assert.Equal(400, shared, precision: 6);

            // And when height binds, the set is NARROWER than the space - that leftover width is
            // exactly what the variable slot count spends on more photos.
            Assert.True(StageLayout.ComputeSetWidth(shared, 10, aspects) < 5000);
        }

        [Fact]
        public void WidestPhotoNeverOverflowsTheStage()
        {
            var aspects = new[] { 0.5, 1.0, 2.4 };
            const double width = 1400;
            const double gaps = 10;

            var shared = StageLayout.ComputeSharedHeight(width, 10_000, gaps, aspects);

            Assert.True(StageLayout.ComputeSetWidth(shared, gaps, aspects) <= width + 1e-9);
        }

        [Fact]
        public void MorePhotosMeansShorterPhotosOnceWidthBinds()
        {
            const double width = 1400;
            var three = StageLayout.ComputeSharedHeight(width, 10_000, 10, new[] { Landscape, Landscape, Landscape });
            var five = StageLayout.ComputeSharedHeight(width, 10_000, 20, new[] { Landscape, Landscape, Landscape, Landscape, Landscape });

            Assert.True(five < three, "adding photos must shrink them once the width is the binding constraint");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void UndecodedOrCorruptAspectFallsBackInsteadOfPoisoningTheResult(double bad)
        {
            var shared = StageLayout.ComputeSharedHeight(1400, 10_000, 0, new[] { bad });

            Assert.True(shared > 0 && double.IsFinite(shared));
            Assert.Equal(1400.0 / StageLayout.DefaultAspectRatio, shared, precision: 6);
        }

        [Fact]
        public void OneUndecodedPhotoDoesNotDistortTheOthers()
        {
            var withUnknown = StageLayout.ComputeSharedHeight(1400, 10_000, 10, new[] { Landscape, 0.0, Landscape });
            var allKnown = StageLayout.ComputeSharedHeight(1400, 10_000, 10, new[] { Landscape, Landscape, Landscape });

            // The fallback IS the landscape aspect, so these must agree exactly.
            Assert.Equal(allKnown, withUnknown, precision: 6);
        }

        [Fact]
        public void GapsAreOneFewerThanSlots()
        {
            // Three columns have two gaps between them, not three. Getting this wrong silently
            // mis-sizes every photo on stage.
            Assert.Equal(10, StageLayout.ComputeTotalGapWidth(3, 5), precision: 6);
            Assert.Equal(68, StageLayout.ComputeTotalGapWidth(3, 34), precision: 6);
            Assert.Equal(40, StageLayout.ComputeTotalGapWidth(9, 5), precision: 6);
            Assert.Equal(0, StageLayout.ComputeTotalGapWidth(1, 5));
            Assert.Equal(0, StageLayout.ComputeTotalGapWidth(0, 5));
            Assert.Equal(0, StageLayout.ComputeTotalGapWidth(3, double.NaN));
        }

        [Fact]
        public void TighterSpacingProducesTallerPhotos()
        {
            var wide = StageLayout.ComputeSharedHeight(1400, 10_000, StageLayout.ComputeTotalGapWidth(3, 34),
                new[] { Landscape, Landscape, Landscape });
            var tight = StageLayout.ComputeSharedHeight(1400, 10_000, StageLayout.ComputeTotalGapWidth(3, 5),
                new[] { Landscape, Landscape, Landscape });

            Assert.True(tight > wide, $"expected tighter spacing to grow photos: {wide} -> {tight}");
        }

        [Fact]
        public void NoRoomOrNoPhotosYieldsZeroRatherThanNegativeLayout()
        {
            Assert.Equal(0, StageLayout.ComputeSharedHeight(0, 400, 10, new[] { Landscape }));
            Assert.Equal(0, StageLayout.ComputeSharedHeight(1400, 0, 10, new[] { Landscape }));
            Assert.Equal(0, StageLayout.ComputeSharedHeight(1400, 400, 10, Array.Empty<double>()));
            Assert.Equal(0, StageLayout.PhotoWidth(0, Landscape));

            // Gaps alone exceeding the stage must not produce a negative height.
            Assert.Equal(0, StageLayout.ComputeSharedHeight(20, 400, 40, new[] { Landscape }));
        }
    }
}
