using System;
using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin.Handlers;
using Xunit;

namespace RvtMcp.Tests
{
    public class PipeJunctionPlannerTests
    {
        // An open end at (x, y, z) mm whose pipe body lies towards (bx, by, bz); the connector points away from it.
        private static PipeEnd End(long id, double x, double y, double z, double bx, double by, double bz, double dn = 110, long systemType = 1)
        {
            double dx = x - bx, dy = y - by, dz = z - bz, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return new PipeEnd { ElementId = id, ConnectorId = 0, X = x, Y = y, Z = z, Dx = dx / l, Dy = dy / l, Dz = dz / l, DiameterMm = dn, SystemTypeId = systemType };
        }

        private static StructureBox Manhole(double x, double y) => new StructureBox
        {
            Id = 99, Name = "EMA_PozoRegristo", MinX = x - 600, MinY = y - 600, MinZ = 8000, MaxX = x + 600, MaxY = y + 600, MaxZ = 10000
        };

        private static PipeJunctionPlan Plan(params PipeEnd[] ends) => Plan(ends, new StructureBox[0]);
        private static PipeJunctionPlan Plan(PipeEnd[] ends, StructureBox[] structures)
            => PipeJunctionPlanner.Plan(ends, structures, 5, 100);

        [Fact]
        public void Two_pipes_meeting_at_an_angle_get_an_elbow()
        {
            // Vertex of a drawn polyline: one segment comes from the south, the next leaves to the north-east (24ACC, 13°).
            var plan = Plan(End(1, 0, 0, 9900, 0, -7500, 9825), End(2, 0, 0, 9900, 1800, 7300, 9975));

            var j = Assert.Single(plan.Junctions);
            Assert.Equal("elbow", j.Kind);
            Assert.InRange(j.DeflectionDeg, 13, 14.5);
            Assert.Empty(plan.Unresolved);
        }

        [Fact]
        public void Straight_joint_gets_union_or_transition_by_diameter()
        {
            Assert.Equal("union", Assert.Single(Plan(End(1, 0, 0, 0, -1000, 0, 0), End(2, 0, 0, 0, 1000, 0, 0)).Junctions).Kind);
            Assert.Equal("transition", Assert.Single(Plan(End(1, 0, 0, 0, -1000, 0, 0, 160), End(2, 0, 0, 0, 1000, 0, 0, 200)).Junctions).Kind);
        }

        [Fact]
        public void Ends_within_tolerance_are_grouped_but_not_beyond()
        {
            Assert.Single(Plan(End(1, 0, 0, 0, -1000, 0, 0), End(2, 3, 0, 0, 1000, 500, 0)).Junctions);

            var apart = Plan(End(1, 0, 0, 0, -1000, 0, 0), End(2, 30, 0, 0, 1000, 500, 0));
            Assert.Empty(apart.Junctions);
            Assert.Equal(new[] { "open_end", "open_end" }, apart.Unresolved.Select(u => u.Reason));
        }

        [Fact]
        public void Ends_inside_a_structure_stay_open()
        {
            // Two collectors and a gully branch arriving at a manhole: the manhole is the junction.
            var ends = new[] { End(1, 0, 0, 8620, -9000, 0, 9000), End(2, 0, 0, 8620, 0, -9000, 8500), End(3, 0, 0, 8620, 5000, 2000, 9100) };
            var plan = Plan(ends, new[] { Manhole(0, 0) });

            Assert.Empty(plan.Junctions);
            Assert.Empty(plan.Unresolved);
            Assert.Equal(3, plan.InStructure.Count);
            Assert.All(plan.InStructure, s => Assert.Equal(99, s.Structure.Id));
        }

        [Fact]
        public void Lone_end_inside_a_structure_is_not_reported_as_open()
        {
            var plan = Plan(new[] { End(1, 700, 0, 9000, 5000, 0, 9100) }, new[] { Manhole(0, 0) });

            Assert.Single(plan.InStructure);
            Assert.Empty(plan.Unresolved);
        }

        [Fact]
        public void Three_ends_with_a_straight_run_get_a_tee_with_the_branch_last()
        {
            var plan = Plan(End(10, 0, 0, 0, -2000, 0, 0), End(20, 0, 0, 0, 0, 1500, 0, 90), End(30, 0, 0, 0, 2000, 0, 0));

            var j = Assert.Single(plan.Junctions);
            Assert.Equal("tee", j.Kind);
            Assert.Equal(20, j.Ends[2].ElementId);
            Assert.Equal(new long[] { 10, 30 }, j.Ends.Take(2).Select(e => e.ElementId).OrderBy(i => i));
        }

        [Fact]
        public void Three_ends_without_a_straight_run_are_unresolved()
        {
            var plan = Plan(End(1, 0, 0, 0, -1000, 0, 0), End(2, 0, 0, 0, 500, 866, 0), End(3, 0, 0, 0, 500, -866, 0));

            Assert.Empty(plan.Junctions);
            Assert.Equal("no_straight_run_for_tee", Assert.Single(plan.Unresolved).Reason);
        }

        [Fact]
        public void Four_ends_in_two_straight_runs_get_a_cross()
        {
            var plan = Plan(End(1, 0, 0, 0, -1000, 0, 0), End(2, 0, 0, 0, 0, 1000, 0), End(3, 0, 0, 0, 1000, 0, 0), End(4, 0, 0, 0, 0, -1000, 0));

            var j = Assert.Single(plan.Junctions);
            Assert.Equal("cross", j.Kind);
            var runs = new[] { j.Ends.Take(2), j.Ends.Skip(2) }.Select(r => string.Join(",", r.Select(e => e.ElementId).OrderBy(i => i))).OrderBy(s => s);
            Assert.Equal(new[] { "1,3", "2,4" }, runs);
        }

        [Fact]
        public void Bend_with_a_diameter_change_and_mixed_systems_are_unresolved()
        {
            Assert.Equal("diameter_change_at_bend",
                Assert.Single(Plan(End(1, 0, 0, 0, -1000, 0, 0, 160), End(2, 0, 0, 0, 0, 1000, 0, 200)).Unresolved).Reason);
            Assert.Equal("system_type_mismatch",
                Assert.Single(Plan(End(1, 0, 0, 0, -1000, 0, 0, 160, 1), End(2, 0, 0, 0, 0, 1000, 0, 160, 2)).Unresolved).Reason);
        }

        [Fact]
        public void Deflection_is_zero_for_a_straight_continuation_and_ninety_for_a_square_bend()
        {
            Assert.Equal(0, PipeJunctionPlanner.Deflection(End(1, 0, 0, 0, -1, 0, 0), End(2, 0, 0, 0, 1, 0, 0)), 6);
            Assert.Equal(90, PipeJunctionPlanner.Deflection(End(1, 0, 0, 0, -1, 0, 0), End(2, 0, 0, 0, 0, 1, 0)), 6);
        }
    }
}
