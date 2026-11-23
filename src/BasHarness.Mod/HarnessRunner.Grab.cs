using System;
using System.Collections;
using System.Globalization;
using BasHarness.Core;
using ThunderRoad;
using UnityEngine;

namespace BasHarness
{
    /// <summary>Steps where the player's hand holds the spawned NPC and moves it: player-grab-npc, player-slam.</summary>
    internal sealed partial class HarnessRunner
    {
        /// <summary>player-slam: how long the controller glides back afterwards (s); an instant snap would be a slam of its own.</summary>
        private const float SlamReturnSeconds = 0.8f;

        /// <summary>player-grab-npc: the controller reaches for the handle over this long (s), then waits for the hand.</summary>
        private const float GrabReachSeconds = 0.5f;
        private const float GrabSettleSeconds = 0.3f;

        /// <summary>player-slam: default time the controller stays at the end of the move (s).</summary>
        private const float DefaultSlamHold = 0.4f;

        /// <summary>player-slam: default controller speed (m/s), a hard downward swing.</summary>
        private const float DefaultSlamSpeed = 6f;

        /// <summary>
        /// player-grab-npc &lt;RagdollPart.Type&gt; [section=Full] [hand=right]: the player's hand grabs the first handle
        /// (HandleRagdoll) of that part of the spawned NPC through RagdollHand.Grab(handle, teleportToHand: true), as a real
        /// grab of a wrist or arm does. HandleRagdoll.OnGrab marks the ragdoll grabbed and, for an arm or hand, anchors the
        /// NPC's arm IK to the player's grip.
        /// </summary>
        private IEnumerator PlayerGrabNpc(Step step, Outcome outcome)
        {
            var player = Player.local != null ? Player.local.creature : null;
            if (player == null || _npc == null || _npc.isKilled)
            {
                outcome.Fail(player == null ? "no player creature" : _npc == null ? "no NPC (spawn-npc first)" : "the NPC is dead");
                yield break;
            }
            if (!Enum.TryParse(step.Args[0], true, out RagdollPart.Type type))
            {
                outcome.Fail("'" + step.Args[0] + "' is not a body part (" + string.Join(", ", Enum.GetNames(typeof(RagdollPart.Type))) + ")");
                yield break;
            }
            var section = RagdollPart.Section.Full;
            if (step.Named.TryGetValue("section", out var sectionText))
            {
                Enum.TryParse(sectionText, true, out section);
            }
            var part = _npc.ragdoll.GetPart(type, section);
            if (part == null || part.handles == null || part.handles.Count == 0)
            {
                outcome.Fail((part == null ? "the NPC has no " + type : "the NPC's " + type + " has no handle to grab") + "; parts with a handle: " + PartsWithHandles());
                yield break;
            }
            var side = SideOf(step);
            var hand = player.GetHand(side);
            if (hand.grabbedHandle != null)
            {
                hand.TryRelease();
                yield return null;
            }
            var handle = part.handles[0];
            // PlayerHand.ManagedUpdate drops a held handle that is farther from the shoulders than the arm reaches.
            var reach = player.morphology.GetArmCenterToFingerTipLenght();
            var away = Vector3.Distance(Player.local.GetShoulderCenter(), handle.transform.position);
            if (away > reach)
            {
                outcome.Fail("the NPC's " + type + " is " + away.ToString("0.00", CultureInfo.InvariantCulture) + " m from the player's shoulders, the arm reaches "
                    + reach.ToString("0.00", CultureInfo.InvariantCulture) + " m (the game drops a grab beyond that): spawn the NPC closer");
                yield break;
            }
            // Reach first: the controller moves to the handle and the hand follows, so the grab does not snap the
            // hand (and the NPC's arm with it) across the gap. The controller then stays there, tracking off.
            var controller = side == Side.Left ? Player.local.handLeft : Player.local.handRight;
            controller.SetTracking(false);
            var from = controller.transform.position;
            var start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < GrabReachSeconds)
            {
                controller.transform.position = Vector3.Lerp(from, handle.transform.position, (Time.realtimeSinceStartup - start) / GrabReachSeconds);
                yield return null;
            }
            var settle = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - settle < GrabSettleSeconds)
            {
                controller.transform.position = handle.transform.position;
                yield return null;
            }
            var gap = Vector3.Distance(controller.transform.position, handle.transform.position);
            var heldBefore = hand.grabbedHandle != null ? hand.grabbedHandle.name : "nothing";
            string releasedBy = null;
            RagdollHand.UnGrabEvent onUnGrab = (unGrabSide, unGrabbed, throwing, eventTime) =>
            {
                if (eventTime == EventTime.OnStart && unGrabbed == handle && releasedBy == null)
                {
                    releasedBy = Environment.StackTrace;
                }
            };
            hand.OnUnGrabEvent += onUnGrab;
            hand.Grab(handle, true);
            var grabbedAtOnce = hand.grabbedHandle == handle;
            yield return null;
            yield return null;
            hand.OnUnGrabEvent -= onUnGrab;
            if (releasedBy != null)
            {
                Debug.Log("[BasHarness] player-grab-npc: the NPC's " + type + " was let go by:\n" + releasedBy);
            }
            if (hand.grabbedHandle != handle)
            {
                outcome.Fail("the player's " + side + " hand did not keep the NPC's " + type + " (handle '" + handle.name + "'): held before "
                    + heldBefore + ", grabbed in the call " + grabbedAtOnce + ", holds now " + (hand.grabbedHandle != null ? hand.grabbedHandle.name : "nothing")
                    + ", hand " + gap.ToString("0.00", CultureInfo.InvariantCulture) + " m from the handle, NPC state " + _npc.state);
                yield break;
            }
            outcome.Detail = "player's " + side + " hand holds the NPC's " + type + " (handle '" + handle.name + "'); ragdoll grabbed " + _npc.ragdoll.isGrabbed
                + ", NPC state " + _npc.state + "; controller at height " + Height(controller.transform.position)
                + " m, NPC arm part at " + Height(part.transform.position) + " m above the player's feet; hand " + gap.ToString("0.00", CultureInfo.InvariantCulture)
                + " m between controller and handle before the grab, " + away.ToString("0.00", CultureInfo.InvariantCulture) + " m from the shoulders (the arm reaches "
                + reach.ToString("0.00", CultureInfo.InvariantCulture) + " m; " + Where(Player.local.GetShoulderCenter(), "shoulders") + ", " + Where(handle.transform.position, "handle") + "); NPC holds " + (Holds(_npc.handLeft) || Holds(_npc.handRight) ? "a weapon" : "nothing") + "; the controller stays there (tracking off)";
        }

        /// <summary>
        /// player-slam &lt;right,up,forward&gt; [speed=6] [hold=0.4] [back=true] [hand=right]: the player's controller (the PlayerHand
        /// pose the hand follows, tracking off) moves in a straight line by that many metres along the player's own right,
        /// up and forward at speed m/s, so whatever the hand holds is swung into what lies on that path (the floor for a
        /// downward move); stays there hold s; then glides back over 0.8 s and tracks the mock headset again (back=false: stays).
        /// Real physics: the game's own collision events fire if the held body meets something.
        /// </summary>
        private IEnumerator PlayerSlam(Step step, Outcome outcome)
        {
            var player = Player.local;
            if (player == null || player.creature == null || !ScenarioParser.TryParseVector(step.Args[0], out var right, out var up, out var forward))
            {
                outcome.Fail("no player");
                yield break;
            }
            var speed = step.Named.TryGetValue("speed", out var speedText) && ScenarioParser.TryParseNumber(speedText, out var s) ? s : DefaultSlamSpeed;
            var back = !step.Named.TryGetValue("back", out var b) || b != "false";
            var side = SideOf(step);
            var controller = side == Side.Left ? player.handLeft : player.handRight;
            var ragdollHand = player.creature.GetHand(side);
            var body = player.creature.transform;
            var move = (body.right * right) + (body.up * up) + (body.forward * forward);
            var metres = move.magnitude;
            var seconds = CombatMath.MoveSeconds(metres, speed);
            var rest = controller.transform.position;
            // A hand parked on an NPC by player-grab-npc keeps its controller there (tracking off) after the slam.
            var parked = ragdollHand.grabbedHandle is HandleRagdoll;
            var lowest = float.PositiveInfinity;
            var handStart = HeldY(ragdollHand);
            var heldFrom = HeldAt(ragdollHand);
            var way = metres > 0f ? move / metres : Vector3.zero;
            Debug.Log("[BasHarness] player-slam grip before the move: " + GripReport(ragdollHand));
            var contacts = new System.Text.StringBuilder();
            var logs = WatchContacts(ragdollHand.grabbedHandle, contacts);
            controller.SetTracking(false);
            var start = Time.realtimeSinceStartup;
            foreach (var l in logs)
            {
                l.Start = start;
            }
            float elapsed;
            var trace = new System.Text.StringBuilder();
            do
            {
                elapsed = Time.realtimeSinceStartup - start;
                controller.transform.position = rest + (move * CombatMath.MoveShare(metres, speed, elapsed));
                yield return null;
                lowest = Mathf.Min(lowest, HeldY(ragdollHand));
                Trace(trace, elapsed, controller, ragdollHand, heldFrom, way);
            }
            while (elapsed < seconds);
            // Hold at the end, as an arm stops on the table: the hand (a physics body pulled towards the controller)
            // catches up with the controller there.
            var hold = step.Named.TryGetValue("hold", out var holdText) && ScenarioParser.TryParseSeconds(holdText, out var h) ? h : DefaultSlamHold;
            var held0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - held0 < hold)
            {
                yield return null;
                lowest = Mathf.Min(lowest, HeldY(ragdollHand));
                Trace(trace, Time.realtimeSinceStartup - start, controller, ragdollHand, heldFrom, way);
            }
            Debug.Log("[BasHarness] player-slam (s: controller / player hand / held body, m above the feet / held body along the move, m / its speed along the move, m/s / grip to shoulders, m):" + trace);
            Debug.Log("[BasHarness] player-slam grip after the hold: " + GripReport(ragdollHand));
            foreach (var l in logs)
            {
                UnityEngine.Object.Destroy(l);
            }
            Debug.Log("[BasHarness] player-slam contacts (s: own collider (body) -> other collider (its body, layer) at relative speed), before any game filter:" + (contacts.Length > 0 ? contacts.ToString() : " none"));
            var held = ragdollHand.grabbedHandle != null ? ragdollHand.grabbedHandle.name : "nothing";
            if (back)
            {
                var from = controller.transform.position;
                var glide = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - glide < SlamReturnSeconds)
                {
                    controller.transform.position = Vector3.Lerp(from, rest, (Time.realtimeSinceStartup - glide) / SlamReturnSeconds);
                    yield return null;
                }
                controller.transform.position = rest;
                if (!parked)
                {
                    controller.SetTracking(true);
                }
            }
            outcome.Detail = side + " controller moved " + metres.ToString("0.00", CultureInfo.InvariantCulture) + " m at "
                + speed.ToString("0.0", CultureInfo.InvariantCulture) + " m/s in " + elapsed.ToString("0.00", CultureInfo.InvariantCulture)
                + " s, held " + hold.ToString("0.00", CultureInfo.InvariantCulture) + " s" + (back ? ", then back" : ", left there") + "; the held body from "
                + Height(new Vector3(0f, handStart, 0f)) + " m down to " + Height(new Vector3(0f, lowest, 0f))
                + " m above the player's feet; the hand holds " + held;
            if (parked && ragdollHand.grabbedHandle == null)
            {
                outcome.Fail(outcome.Detail + " at the bottom, nothing after the way back: it let go of the NPC during the slam (the game drops a grab beyond the arm's reach of "
                    + player.creature.morphology.GetArmCenterToFingerTipLenght().ToString("0.00", CultureInfo.InvariantCulture) + " m from the shoulders)");
            }
        }

        /// <summary>The spawned NPC's parts that have a HandleRagdoll, as "Type (Section)", for a failure message.</summary>
        private string PartsWithHandles()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var p in _npc.ragdoll.parts)
            {
                if (p.handles != null && p.handles.Count > 0)
                {
                    names.Add(p.type + " (" + p.section + ", " + p.handles.Count + ")");
                }
            }
            return names.Count > 0 ? string.Join(", ", names) : "none";
        }

        /// <summary>
        /// player-eyes: moves the mock headset to the player creature's own eye height, where a calibrated player's head is.
        /// The harness marks the calibration done without measuring anyone (KeepCalibrated), so the mock headset keeps the
        /// pose Unity's mock HMD gives it, about 0.6 m above the creature's eyes (2026-11-23: shoulders 2.09 m above the feet).
        /// Everything measured from the head is then off by that much, among it PlayerHand's reach check from the shoulders.
        /// Shifts the game's own height offset (Player.heightOffset, the console's setheightoffset) by the difference.
        /// </summary>
        private static IEnumerator PlayerEyes(Outcome outcome)
        {
            var player = Player.local;
            if (player == null || player.creature == null || player.head == null || player.creature.centerEyes == null)
            {
                outcome.Fail("no player head or creature eyes");
                yield break;
            }
            var feet = player.creature.transform;
            var morphology = player.creature.morphology;
            var headBefore = player.head.transform.position.y - feet.position.y;
            var shouldersBefore = player.GetShoulderCenter().y - feet.position.y;
            var eyes = player.creature.centerEyes.position.y - feet.position.y;
            var offsetBefore = Player.heightOffset;
            Player.heightOffset += eyes - headBefore;
            player.SetHeightOffset(player.GetHeightOffset());
            var log = new System.Text.StringBuilder();
            var t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.5f)
            {
                yield return null;
                log.Append(' ').Append(F2(Time.realtimeSinceStartup - t0)).Append(": head ").Append(F2(player.head.transform.position.y)).Append(" feet ")
                    .Append(F2(feet.position.y)).Append(" offset ").Append(F2(player.globalOffsetTransform.localPosition.y)).Append(" rig ")
                    .Append(F2(player.transform.position.y)).Append(';');
            }
            Debug.Log("[BasHarness] player-eyes (s: world y of head, feet, offset transform local y, player rig):" + log);
            var headAfter = player.head.transform.position.y - feet.position.y;
            outcome.Detail = "head from " + F2(headBefore) + " m to " + F2(headAfter) + " m above the feet, the creature's eyes at " + F2(eyes)
                + " m (height offset " + F2(offsetBefore) + " -> " + F2(Player.heightOffset) + "); shoulders from " + F2(shouldersBefore) + " m to "
                + F2(player.GetShoulderCenter().y - feet.position.y) + " m; morphology eyes " + F2(morphology.eyesHeight) + " m, arms to eyes "
                + F2(morphology.armsToEyesHeight) + " m, height " + F2(morphology.height) + " m, reach " + F2(morphology.GetArmCenterToFingerTipLenght()) + " m";
            if (Mathf.Abs(headAfter - eyes) > 0.05f)
            {
                outcome.Fail(outcome.Detail + ": the head did not reach the eyes");
            }
        }

        private static string F2(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>place-block: default edge length (m).</summary>
        private const float DefaultBlockSize = 0.3f;

        /// <summary>
        /// place-block &lt;right,up,forward&gt; [size=0.3]: a static box (a BoxCollider, no Rigidbody, no ColliderGroup: the
        /// world to the game's collision code) of that edge length, centred that many metres from what the player's right
        /// hand holds (else the right controller) along the player's own right, up and forward; on the ground's layer (the first
        /// static collider a ray down from the player meets), so it collides with what the floor collides with, and with the
        /// ground's physics material (without a known one the game raises no collision event). Something solid to slam a held hand onto.
        /// </summary>
        private static void PlaceBlock(Step step, Outcome outcome)
        {
            var player = Player.local;
            if (player == null || player.creature == null || !ScenarioParser.TryParseVector(step.Args[0], out var right, out var up, out var forward))
            {
                outcome.Fail("no player");
                return;
            }
            var size = step.Named.TryGetValue("size", out var sizeText) && ScenarioParser.TryParseNumber(sizeText, out var s) ? s : DefaultBlockSize;
            var body = player.creature.transform;
            // The ground: the first static collider (no rigidbody) under the player; a ray can hit a dropped blade first.
            Collider ground = null;
            var hits = Physics.RaycastAll(body.position + (Vector3.up * 0.5f), Vector3.down, 3f, ~0, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (var hit in hits)
            {
                if (hit.collider.attachedRigidbody == null)
                {
                    ground = hit.collider;
                    break;
                }
            }
            if (ground == null)
            {
                outcome.Fail("no static ground under the player to take the layer from");
                return;
            }
            // From what the right hand holds (the handle is a physics body), else from the controller. Not RagdollHand.grip:
            // read from a coroutine it sat up to 0.6 m off the held sword (2026-11-18) and 0.4 m off the held NPC arm here.
            var held = player.creature.handRight.grabbedHandle;
            var origin = held != null ? held.transform.position : player.handRight.transform.position;
            var originName = held != null ? "the held handle" : "the controller";
            if (step.Named.TryGetValue("from", out var from) && from == "npc-hand")
            {
                // Under the NPC hand whose arm the player holds: the centre of its colliders, at their lowest point. The hand
                // sits a hand's length past the forearm handle, and that is what a slam drives into the table.
                var npcHand = NpcHandHeld(held);
                if (npcHand == null)
                {
                    outcome.Fail("from=npc-hand: the player's right hand holds no NPC arm or hand (player-grab-npc first)");
                    return;
                }
                var bounds = npcHand.colliderGroup.colliders[0].bounds;
                foreach (var c in npcHand.colliderGroup.colliders)
                {
                    bounds.Encapsulate(c.bounds);
                }
                origin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                originName = "under the NPC's " + npcHand.side + " hand";
            }
            var center = origin + (body.right * right) + (body.up * up) + (body.forward * forward);
            // A block that appears inside a body makes the physics push that body out at once: a held NPC arm or its
            // sword is flung and the player's grab is lost before any slam (2026-11-23, owed/1-slam.txt).
            var inside = BodiesInside(center, size, body.rotation);
            if (inside != null)
            {
                outcome.Fail("the block (" + Where(center, "centre") + ", from " + Where(origin, originName) + ") would sit inside "
                    + inside + "; place it where nothing is (the physics would fling those out at once)");
                return;
            }
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "BasHarnessBlock";
            block.layer = ground.gameObject.layer;
            // The game drops a contact whose physics material it does not know (CollisionHandler.OnCollisionEnter:
            // "Target physics material not found", no collision event): the block gets the ground's.
            // The ground's material is itself an instance ("Dirt (Instance)"); Collider.material clones a shared material and
            // adds " (Instance)", so the block gets a material under the base name and reads back under the ground's name.
            var groundMaterial = ground.sharedMaterial;
            if (groundMaterial != null)
            {
                block.GetComponent<Collider>().sharedMaterial = new PhysicMaterial(groundMaterial.name.Replace(" (Instance)", string.Empty))
                {
                    dynamicFriction = groundMaterial.dynamicFriction,
                    staticFriction = groundMaterial.staticFriction,
                    bounciness = groundMaterial.bounciness,
                    frictionCombine = groundMaterial.frictionCombine,
                    bounceCombine = groundMaterial.bounceCombine,
                };
            }
            block.transform.SetPositionAndRotation(center, body.rotation);
            block.transform.localScale = Vector3.one * size;
            outcome.Detail = "block of " + size.ToString("0.00", CultureInfo.InvariantCulture) + " m, " + Where(center, "centre") + " (" + Where(origin, originName) + "), top at " + Height(center + (Vector3.up * (size / 2f)))
                + " m above the player's feet, on the ground's layer " + LayerMask.LayerToName(block.layer) + " ('" + ground.name + "'), physics material '" + block.GetComponent<Collider>().material.name + "' (ground: '" + (groundMaterial != null ? groundMaterial.name : "none") + "')";
        }

        /// <summary>The NPC hand at the end of the arm or hand a HandleRagdoll belongs to; null for anything else.</summary>
        private static RagdollHand NpcHandHeld(Handle held)
        {
            var part = held is HandleRagdoll ragdollHandle ? ragdollHandle.ragdollPart : null;
            if (part == null || part.ragdoll.creature.isPlayer)
            {
                return null;
            }
            var creature = part.ragdoll.creature;
            switch (part.type)
            {
                case RagdollPart.Type.LeftArm:
                case RagdollPart.Type.LeftHand:
                    return creature.handLeft;
                case RagdollPart.Type.RightArm:
                case RagdollPart.Type.RightHand:
                    return creature.handRight;
                default:
                    return null;
            }
        }

        /// <summary>
        /// What a box of that size and pose would overlap besides static world colliders: creature parts, items and other
        /// physics bodies, as a comma list; null when nothing.
        /// </summary>
        private static string BodiesInside(Vector3 center, float size, Quaternion rotation)
        {
            var hits = Physics.OverlapBox(center, Vector3.one * (size / 2f), rotation, ~0, QueryTriggerInteraction.Ignore);
            var names = new System.Collections.Generic.List<string>();
            foreach (var c in hits)
            {
                if (c.attachedRigidbody == null)
                {
                    continue;
                }
                var part = c.GetComponentInParent<RagdollPart>();
                var item = c.GetComponentInParent<Item>();
                var name = part != null ? (part.ragdoll.creature.isPlayer ? "the player's " : "the NPC's ") + part.type + " (" + part.section + ")"
                    : item != null ? "item '" + item.data.id + "'"
                    : "'" + c.attachedRigidbody.name + "'";
                // Where it is, so the next try can go beside it: the collider's bounds in the player's frame, lowest and highest point.
                var b = c.bounds;
                name += " " + Where(b.center, "around") + " from " + F2(b.min.y - Player.local.creature.transform.position.y) + " to "
                    + F2(b.max.y - Player.local.creature.transform.position.y) + " m up";
                names.Add(name);
            }
            return names.Count > 0 ? string.Join(", ", names) : null;
        }

        /// <summary>A point in the player's own frame (from the player's feet: right, up, forward, m), for the step details.</summary>
        private static string Where(Vector3 point, string what)
        {
            // In metres: InverseTransformPoint would divide by the creature's scale (the player creature is scaled to the calibrated height).
            var body = Player.local.creature.transform;
            var local = body.InverseTransformDirection(point - body.position);
            return what + " at " + local.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + local.y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + local.z.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>Height of what the hand holds (its handle, a physics body), else of the hand.</summary>
        private static float HeldY(RagdollHand hand)
        {
            return hand.grabbedHandle != null ? hand.grabbedHandle.transform.position.y : hand.transform.position.y;
        }

        /// <summary>Where what the hand holds is (its handle, a physics body), else the hand.</summary>
        private static Vector3 HeldAt(RagdollHand hand)
        {
            return hand.grabbedHandle != null ? hand.grabbedHandle.transform.position : hand.transform.position;
        }

        /// <summary>
        /// One trace entry: heights of controller, hand and held body; how far the held body has come along the move
        /// (it follows the controller only partly when it is an NPC's arm); the grip's distance from the shoulders.
        /// </summary>
        private static void Trace(System.Text.StringBuilder trace, float t, PlayerHand controller, RagdollHand hand, Vector3 heldFrom, Vector3 way)
        {
            trace.Append(' ').Append(t.ToString("0.00", CultureInfo.InvariantCulture)).Append(": ").Append(Height(controller.transform.position))
                .Append(" / ").Append(Height(hand.transform.position)).Append(" / ").Append(Height(new Vector3(0f, HeldY(hand), 0f)))
                .Append(" / ").Append(Vector3.Dot(HeldAt(hand) - heldFrom, way).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" / ").Append(HeldSpeed(hand, way))
                .Append(" / ").Append(Reach(hand)).Append(';');
        }

        /// <summary>
        /// Puts a <see cref="ContactLog"/> on the bodies a slam of a held NPC arm can hit with: the NPC's hand, its forearm
        /// and what that hand holds. Nothing for other holds.
        /// </summary>
        private static System.Collections.Generic.List<ContactLog> WatchContacts(Handle held, System.Text.StringBuilder lines)
        {
            var logs = new System.Collections.Generic.List<ContactLog>();
            var npcHand = held != null ? NpcHandHeld(held) : null;
            if (npcHand == null)
            {
                return logs;
            }
            var bodies = new System.Collections.Generic.List<GameObject> { npcHand.physicBody.gameObject };
            if (npcHand.lowerArmPart != null)
            {
                bodies.Add(npcHand.lowerArmPart.physicBody.gameObject);
            }
            var item = npcHand.grabbedHandle != null ? npcHand.grabbedHandle.item : null;
            if (item != null)
            {
                bodies.Add(item.physicBody.gameObject);
            }
            foreach (var b in bodies)
            {
                var log = b.AddComponent<ContactLog>();
                log.Lines = lines;
                logs.Add(log);
            }
            var data = held is HandleRagdoll hr ? hr.handleRagdollData : null;
            Debug.Log("[BasHarness] player-slam NPC hand colliders: " + Enabled(npcHand.colliderGroup) + ", collision events " + Events(npcHand.collisionHandler)
                + "; forearm: " + (npcHand.lowerArmPart != null ? Enabled(npcHand.lowerArmPart.colliderGroup) + ", collision events " + Events(npcHand.lowerArmPart.collisionHandler) : "-")
                + "; ragdoll state " + npcHand.ragdoll.state + ", collisionMinVelocity " + npcHand.ragdoll.collisionMinVelocity.ToString("0.0", CultureInfo.InvariantCulture)
                + "; the held handle turns collision events on for: " + (data != null && data.activateCollisionOnParts != null && data.activateCollisionOnParts.Count > 0 ? string.Join(", ", data.activateCollisionOnParts) : "nothing")
                + (data != null ? " (handle data '" + data.id + "')" : ""));
            return logs;
        }

        /// <summary>Whether a CollisionHandler raises the game's collision events (its <c>active</c> flag).</summary>
        private static string Events(CollisionHandler handler)
        {
            return handler == null ? "no handler" : handler.active ? "on" : "off";
        }

        private static string Enabled(ColliderGroup group)
        {
            if (group == null)
            {
                return "no group";
            }
            var on = 0;
            foreach (var c in group.colliders)
            {
                if (c != null && c.enabled)
                {
                    on++;
                }
            }
            return on + " of " + group.colliders.Count + " enabled";
        }

        /// <summary>Speed of the held physics body along the move (m/s), "-" when the hand holds nothing.</summary>
        private static string HeldSpeed(RagdollHand hand, Vector3 way)
        {
            var body = hand.grabbedHandle != null ? hand.grabbedHandle.physicBody : null;
            return body != null ? Vector3.Dot(body.velocity, way).ToString("0.00", CultureInfo.InvariantCulture) : "-";
        }

        /// <summary>
        /// How the game ties the held body to the controller (Handle.OnGrab, SetJointConfig): grip type, whether the player
        /// counts as grounded (on the ground the hand joint pulls; off it the player joint, climbing forces), both joints'
        /// position drives and the force they pull with now, and the held body's mass.
        /// </summary>
        private static string GripReport(RagdollHand hand)
        {
            var handle = hand.grabbedHandle;
            var grip = hand.gripInfo;
            if (handle == null || grip == null)
            {
                return "the hand holds nothing";
            }
            var body = handle.physicBody;
            return "type " + grip.type + ", player grounded " + Player.local.locomotion.isGrounded + ", player joint active " + handle.playerJointActive
                + "; hand joint " + Drive(grip.joint) + "; player joint " + Drive(grip.playerJoint)
                + "; held body " + (body != null ? body.mass.ToString("0.0", CultureInfo.InvariantCulture) + " kg, kinematic " + body.isKinematic : "-");
        }

        private static string Drive(ConfigurableJoint joint)
        {
            if (joint == null)
            {
                return "none";
            }
            var d = joint.xDrive;
            return "spring " + d.positionSpring.ToString("0", CultureInfo.InvariantCulture) + " damper " + d.positionDamper.ToString("0", CultureInfo.InvariantCulture)
                + " max " + d.maximumForce.ToString("0", CultureInfo.InvariantCulture) + " N, pulls " + joint.currentForce.magnitude.ToString("0", CultureInfo.InvariantCulture) + " N";
        }

        /// <summary>
        /// How far the grip is from the player's shoulders (m), the distance PlayerHand.ManagedUpdate compares with the arm's
        /// reach before it drops a held handle; "-" when the hand holds nothing.
        /// </summary>
        private static string Reach(RagdollHand hand)
        {
            if (hand.grabbedHandle == null || hand.gripInfo == null || Player.local == null)
            {
                return "-";
            }
            return Vector3.Distance(Player.local.GetShoulderCenter(), hand.gripInfo.transform.position).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static Side SideOf(Step step)
        {
            return step.Named.TryGetValue("hand", out var handText) && handText.Equals("left", StringComparison.OrdinalIgnoreCase) ? Side.Left : Side.Right;
        }

        /// <summary>Height above the player's feet (m), for the step details.</summary>
        private static string Height(Vector3 point)
        {
            var feet = Player.local != null && Player.local.creature != null ? Player.local.creature.transform.position.y : 0f;
            return (point.y - feet).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }
}
