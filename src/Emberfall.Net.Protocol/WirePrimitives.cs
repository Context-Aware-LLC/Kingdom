using System;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// Shared scalar conventions of the wire (design §5): InstanceId as "i&lt;n&gt;" strings
    /// (fixture precedent), enums as exact C# member-name strings — parsed with
    /// <c>Enum.TryParse(ignoreCase: false)</c> and a leading-digit guard so numeric strings are
    /// NOT silently accepted as enum values. Never throws on wire input.
    /// </summary>
    internal static class WirePrimitives
    {
        public static string InstanceToWire(InstanceId id) => id.ToString(); // "i<n>"

        public static bool TryInstance(string s, out InstanceId id)
        {
            id = default;
            if (s.Length < 2 || s[0] != 'i') return false;
            if (!int.TryParse(s.Substring(1), System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out int v)) return false;
            id = new InstanceId(v);
            return true;
        }

        /// <summary>Strict enum-name parse. Rejects numeric strings ("2" would otherwise parse as
        /// the value 2) and, for non-flags enums, undefined members.</summary>
        public static bool TryEnum<TEnum>(string s, out TEnum v) where TEnum : struct, Enum
        {
            v = default;
            if (s.Length == 0) return false;
            char c0 = s[0];
            if (c0 == '-' || (c0 >= '0' && c0 <= '9')) return false;
            if (!Enum.TryParse(s, ignoreCase: false, out v)) return false;
            // Flags enums legitimately parse combined names ("AegisAbsorbed, Lethal") that IsDefined
            // rejects; plain enums must be a defined member.
            return typeof(TEnum) == typeof(EventFlags) || Enum.IsDefined(typeof(TEnum), v);
        }
    }
}
