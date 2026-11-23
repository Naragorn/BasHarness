using System.Globalization;
using System.Text;
using UnityEngine;

namespace BasHarness
{
    /// <summary>
    /// Records every physics contact (Unity's OnCollisionEnter, before any game filter) of the rigidbody it sits on, for
    /// player-slam: which collider met which, how fast, and whether the own collider is enabled. Added for the slam and
    /// removed after it.
    /// </summary>
    internal sealed class ContactLog : MonoBehaviour
    {
        public StringBuilder Lines;
        public float Start;

        private void OnCollisionEnter(Collision collision)
        {
            if (Lines == null || collision.contactCount == 0)
            {
                return;
            }
            var contact = collision.GetContact(0);
            var other = contact.otherCollider;
            Lines.Append(' ').Append((Time.realtimeSinceStartup - Start).ToString("0.00", CultureInfo.InvariantCulture)).Append(": '")
                .Append(contact.thisCollider != null ? contact.thisCollider.name : "?").Append("' (").Append(name).Append(") -> '")
                .Append(other != null ? other.name : "?").Append("' (")
                .Append(other != null && other.attachedRigidbody != null ? other.attachedRigidbody.name : "static")
                .Append(", layer ").Append(other != null ? LayerMask.LayerToName(other.gameObject.layer) : "?").Append(") at ")
                .Append(collision.relativeVelocity.magnitude.ToString("0.00", CultureInfo.InvariantCulture)).Append(" m/s;");
        }
    }
}
