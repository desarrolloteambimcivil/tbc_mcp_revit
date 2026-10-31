using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class PipeClashFinderTests
    {
        private static RoundCurve C(long id, double sx, double sy, double sz, double ex, double ey, double ez, double od, string comment = "Pluviales")
            => new RoundCurve { Id = id, Network = "EMA-Pluviales", Comment = comment, Sx = sx, Sy = sy, Sz = sz, Ex = ex, Ey = ey, Ez = ez, OuterRadiusMm = od / 2 };

        // EMA pilot, model coordinates (mm): Ø315 BA54C running east and the 2 x Ø160 from the south channel to manhole B crossing it.
        private static RoundCurve Collector() => C(1248371, -13641, -48272, 9312, 1237, -47866, 8982, 324);
        private static RoundCurve Branch1() => C(1248377, -1084, -49548, 9132, -836, -40925, 9046, 160);
        private static RoundCurve Branch2() => C(1248380, -684, -49547, 9133, -434, -40878, 9046, 160);

        [Fact]
        public void Finds_the_two_pilot_clashes_with_their_overlap_and_crossing_levels()
        {
            var all = new[] { Collector(), Branch1(), Branch2() };
            var clashes = PipeClashFinder.Find(all, null, null, 0, 100);

            Assert.Equal(2, clashes.Count);
            var c = clashes.Single(x => x.B.Id == 1248377 || x.A.Id == 1248377);
            Assert.InRange(c.OverlapMm, 150, 165);
            Assert.InRange(c.DistanceMm, 80, 90);
            Assert.DoesNotContain(clashes, x => (x.A.Id == 1248377 && x.B.Id == 1248380) || (x.A.Id == 1248380 && x.B.Id == 1248377));
        }

        [Fact]
        public void Clearance_turns_a_near_miss_into_a_clash()
        {
            var a = C(1, 0, -1000, 0, 0, 1000, 0, 100);
            var b = C(2, -1000, 0, 130, 1000, 0, 130, 100);   // 30 mm gap between the pipe walls
            Assert.Empty(PipeClashFinder.Find(new[] { a, b }, null, null, 0, 100));
            var c = Assert.Single(PipeClashFinder.Find(new[] { a, b }, null, null, 50, 100));
            Assert.Equal(20, c.OverlapMm, 3);
        }

        [Fact]
        public void Joined_pipes_and_crossings_inside_their_own_structure_are_not_clashes()
        {
            var a = C(1, 0, -1000, 0, 0, 1000, 0, 200);
            var b = C(2, -1000, 0, 0, 1000, 0, 0, 200);
            a.Adjacent.Add(2);
            Assert.Empty(PipeClashFinder.Find(new[] { a, b }, null, null, 0, 100));

            a.Adjacent.Clear();
            var manhole = new StructureBox { Id = 9, Name = "pozo", Comment = "Pluviales", MinX = -600, MinY = -600, MinZ = -1000, MaxX = 600, MaxY = 600, MaxZ = 1000 };
            Assert.Empty(PipeClashFinder.Find(new[] { a, b }, null, new[] { manhole }, 0, 100));

            manhole.Comment = "Electricidad";   // another network's chamber does not excuse the crossing
            Assert.Single(PipeClashFinder.Find(new[] { a, b }, null, new[] { manhole }, 0, 100));
        }

        [Fact]
        public void Ends_meeting_at_a_point_are_a_junction_not_a_clash()
        {
            var a = C(1, 0, 0, 0, 1000, 0, 0, 110);
            var b = C(2, 1000, 0, 0, 1000, 1000, 0, 110);
            Assert.Empty(PipeClashFinder.Find(new[] { a, b }, null, null, 0, 100));
        }

        [Fact]
        public void Scope_against_others_only_reports_pairs_with_a_scope_curve_once()
        {
            var clashes = PipeClashFinder.Find(new[] { Branch1() }, new[] { Collector(), Branch1(), Branch2() }, null, 0, 100);
            Assert.Equal(1248371, Assert.Single(clashes).B.Id);
        }

        [Fact]
        public void Raising_the_free_end_lifts_the_branch_over_the_collector()
        {
            // Manhole B end (index 1, +8.97 from the plan) stays; the channel end (index 0, assumed level) moves.
            var plan = PipeRegrade.Plan(Branch1(), Collector(), fixedEnd: 1, above: true, clearanceMm: 50, maxEndDeltaMm: 1000, keepFallDirection: true);

            Assert.True(plan.Feasible, plan.Reason);
            Assert.Equal(0, plan.MovedEnd);
            Assert.InRange(plan.EndDeltaMm, 200, 320);
            Assert.Equal(9046, plan.NewEz, 3);
            Assert.True(plan.SlopeAfterPct > plan.SlopeBeforePct);

            var moved = C(1248377, plan.NewSx, plan.NewSy, plan.NewSz, plan.NewEx, plan.NewEy, plan.NewEz, 160);
            var check = PipeClashFinder.Closest(moved, Collector());
            PipeClashFinder.AtPlanCrossing(check);
            Assert.Equal(check.ZbMm + 162 + 50, check.ZaMm - 80, 1);   // bottom of the branch = top of the collector + clearance
        }

        [Fact]
        public void Lowering_that_would_reverse_the_fall_is_refused()
        {
            // Passing the branch under the collector would need its upstream end below the fixed downstream end.
            var plan = PipeRegrade.Plan(Branch1(), Collector(), fixedEnd: 1, above: false, clearanceMm: 50, maxEndDeltaMm: 2000, keepFallDirection: true);
            Assert.False(plan.Feasible);
            Assert.Equal("reverses_fall", plan.Reason);
        }

        [Fact]
        public void Crossing_next_to_the_fixed_end_or_a_large_move_is_refused()
        {
            var startsAtCollector = C(5, -1084, -48000, 9132, -836, -40000, 9046, 160);   // crosses 0.1 m from its start
            Assert.Equal("crossing_too_close_to_fixed_end",
                PipeRegrade.Plan(startsAtCollector, Collector(), fixedEnd: 0, above: true, clearanceMm: 50, maxEndDeltaMm: 1000, keepFallDirection: true).Reason);
            Assert.Equal("end_move_exceeds_limit",
                PipeRegrade.Plan(Branch1(), Collector(), fixedEnd: 1, above: true, clearanceMm: 50, maxEndDeltaMm: 100, keepFallDirection: true).Reason);
        }
    }
}
