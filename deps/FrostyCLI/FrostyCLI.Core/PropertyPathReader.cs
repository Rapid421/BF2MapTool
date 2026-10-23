using System;
using System.Collections;
using System.Reflection;

namespace FrostyCLI.Core
{
    public static class PropertyPathReader
    {
        public static object GetValue(object root, string dotPath)
        {
            if (root == null)    throw new ArgumentNullException("root");
            if (dotPath == null) throw new ArgumentNullException("dotPath");

            string[] segments = dotPath.Split('.');
            object current = root;

            for (int s = 0; s < segments.Length; s++)
            {
                string seg = segments[s];
                if (current == null) throw new InvalidOperationException("Null at segment '" + seg + "'");

                string propName = seg;
                int? idx = null;

                int br = seg.IndexOf('[');
                if (br >= 0)
                {
                    if (!seg.EndsWith("]")) throw new InvalidOperationException("Malformed segment: " + seg);
                    propName = seg.Substring(0, br);
                    idx = int.Parse(seg.Substring(br + 1, seg.Length - br - 2));
                }

                PropertyInfo pi = current.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (pi == null)
                    throw new InvalidOperationException("Property '" + propName + "' not found on " + current.GetType().Name);

                current = pi.GetValue(current);

                if (idx.HasValue)
                {
                    IList list = current as IList;
                    if (list == null) throw new InvalidOperationException("'" + propName + "' is not an IList");
                    current = list[idx.Value];
                }

                // Follow internal refs on the way down so sub-object fields are readable. The final
                // segment is left as-is, so reading a PointerRef still yields the reference itself.
                if (s < segments.Length - 1)
                    current = PointerRefUtil.Descend(current);
            }
            return current;
        }
    }
}
