using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    public static class TypeCoercion
    {
        public static object Coerce(Type targetType, string strValue)
        {
            if (targetType == null) throw new ArgumentNullException("targetType");

            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (strValue == null) return null;
                return Coerce(underlying, strValue);
            }

            if (targetType == typeof(string)) return strValue;

            if (targetType.Name == "CString")
            {
                try { return Activator.CreateInstance(targetType, strValue); }
                catch { return strValue; }
            }

            if (targetType == typeof(bool))   return bool.Parse(strValue);
            if (targetType == typeof(byte))   return byte.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(sbyte))  return sbyte.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(short))  return short.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort)) return ushort.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(int))    return int.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))   return uint.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))   return long.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong))
            {
                if (strValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return ulong.Parse(strValue.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return ulong.Parse(strValue, CultureInfo.InvariantCulture);
            }
            if (targetType == typeof(float))  return float.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(strValue, CultureInfo.InvariantCulture);
            if (targetType == typeof(Guid))   return Guid.Parse(strValue);

            // ResourceRef wraps a 64-bit RID. Needed to point a cloned resource-backed asset (a
            // MeshEmitterAsset, say) at a resource created with `res import --type`: the clone keeps
            // the donor's RID otherwise and silently samples the donor's geometry.
            if (targetType.Name == "ResourceRef")
            {
                ulong rid = strValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.Parse(strValue.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : ulong.Parse(strValue, CultureInfo.InvariantCulture);
                foreach (var ctor in targetType.GetConstructors())
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(ulong))
                        return ctor.Invoke(new object[] { rid });
                }
                object inst = Activator.CreateInstance(targetType);
                var f = targetType.GetField("resourceId",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public)
                        ?? targetType.GetField("ResourceId",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (f != null) { f.SetValue(inst, rid); return inst; }
                throw new InvalidOperationException("ebx set: cannot build a ResourceRef from " + strValue);
            }
            if (targetType.IsEnum)            return Enum.Parse(targetType, strValue, ignoreCase: true);

            // A reflected EBX value struct / class from "Member=value;Member=value" (e.g. an interface event
            // DynamicEvent "Id=-274579609"): a default instance, then each named member coerced recursively.
            // Built for authoring blueprint interfaces (Mygeeto destructible generators).
            if (strValue.Contains("=") && !targetType.IsPrimitive && targetType != typeof(string))
            {
                object inst = Activator.CreateInstance(targetType);
                foreach (string pair in strValue.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) throw new InvalidOperationException("ebx set: bad member assignment '" + pair + "'");
                    string name = pair.Substring(0, eq).Trim(), val = pair.Substring(eq + 1).Trim();
                    var pi = targetType.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    var fi = pi == null ? targetType.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public) : null;
                    if (pi == null && fi == null)
                        throw new InvalidOperationException("ebx set: " + targetType.Name + " has no member '" + name + "'");
                    Type mt = pi != null ? pi.PropertyType : fi.FieldType;
                    object mv = Coerce(mt, val);
                    if (pi != null) pi.SetValue(inst, mv); else fi.SetValue(inst, mv);
                }
                return inst;
            }

            throw new InvalidOperationException(
                "ebx set does not support type " + targetType.FullName);
        }

        public static object Coerce(Type targetType, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            return Coerce(targetType, token.Value<string>());
        }
    }
}
