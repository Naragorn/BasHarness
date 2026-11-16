using System;
using System.Collections.Generic;

namespace BasHarness.Core
{
    /// <summary>Plain-value maths for the combat steps (no Unity types, so it is unit-testable).</summary>
    public static class CombatMath
    {
        /// <summary>
        /// A point <paramref name="distance"/> away on the ground plane, turned <paramref name="angleDegrees"/>
        /// clockwise (seen from above, like Unity's yaw) from the forward direction. No forward → world +z.
        /// </summary>
        public static (float X, float Z) OffsetXZ(float forwardX, float forwardZ, float angleDegrees, float distance)
        {
            var length = Math.Sqrt((forwardX * forwardX) + (forwardZ * forwardZ));
            double fx = 0, fz = 1;
            if (length > 1e-6)
            {
                fx = forwardX / length;
                fz = forwardZ / length;
            }
            var radians = angleDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            // Clockwise around +y in a left-handed (Unity) frame: x' = x cos + z sin, z' = -x sin + z cos.
            var x = (fx * cos) + (fz * sin);
            var z = (-fx * sin) + (fz * cos);
            return ((float)(x * distance), (float)(z * distance));
        }

        /// <summary>
        /// Yaw for an NPC turned <paramref name="facing"/> degrees (clockwise) away from looking at the
        /// player: 0 = faces the player, 180 = back to the player. Result in [0, 360).
        /// </summary>
        public static float FacingYaw(float lookAtPlayerYaw, float facing)
        {
            var yaw = (lookAtPlayerYaw + facing) % 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }

        /// <summary>min &lt;= value (&lt;= max, when a max is given).</summary>
        public static bool InRange(float value, float min, float? max)
        {
            return value >= min && (!max.HasValue || value <= max.Value);
        }
    }

    /// <summary>What deals a <c>hit-npc</c>: the player's bare hand, an item held in it, an item the player threw, a spell.</summary>
    public static class HitBy
    {
        public static readonly IReadOnlyList<string> Names = new[] { "hand", "held", "thrown", "spell" };
    }

    /// <summary>The NPC properties <c>expect-npc</c> can check.</summary>
    public static class NpcProperty
    {
        /// <summary>
        /// stagger = Brain.currentStagger, state = Creature.state, staggered = stagger != None or ragdoll destabilized,
        /// holding = either hand holds an item (True/False), brain = the loaded brain's id.
        /// </summary>
        public static readonly IReadOnlyList<string> Names = new[] { "stagger", "state", "staggered", "holding", "brain" };

        public static bool IsKnown(string name)
        {
            foreach (var known in Names)
            {
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool Matches(string expected, string actual)
        {
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
    }
}
