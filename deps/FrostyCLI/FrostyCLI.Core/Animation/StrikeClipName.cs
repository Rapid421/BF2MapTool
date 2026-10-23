using System;

namespace FrostyCLI.Core.Animation
{
    // Parses Frostbite soldier-state-machine controller names into hero-strike clip names,
    // matching FAAE's StateMachineParser so exported filenames line up with what FAAE expects
    // (FAAE uses the filename without extension as the animation's display name).
    //
    // A controller is a "strike" when its name contains "_Strike_" / "_Closer_", or "_Strike"
    // immediately followed by a digit (e.g. "Vader_Strike2"). The hero prefix is everything
    // before that marker; the suffix is normalized so "Strike2" -> "Strike_2".
    public static class StrikeClipName
    {
        public static bool IsStrikeOrCloser(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.Contains("_Strike_") || name.Contains("_Closer_")) return true;
            int idx = name.IndexOf("_Strike", StringComparison.Ordinal);
            if (idx < 0) return false;
            int afterStrike = idx + 7;
            return afterStrike < name.Length && char.IsDigit(name[afterStrike]);
        }

        // The hero prefix (text before the strike/closer marker), or null when not a strike.
        public static string ExtractHeroPrefix(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            int strikeIdx = fullName.IndexOf("_Strike_", StringComparison.Ordinal);
            int closerIdx = fullName.IndexOf("_Closer_", StringComparison.Ordinal);
            int splitIdx = strikeIdx >= 0 ? strikeIdx : closerIdx;
            if (splitIdx > 0) return fullName.Substring(0, splitIdx);

            int variantIdx = fullName.IndexOf("_Strike", StringComparison.Ordinal);
            if (variantIdx > 0)
            {
                int afterStrike = variantIdx + 7;
                if (afterStrike < fullName.Length && char.IsDigit(fullName[afterStrike]))
                    return fullName.Substring(0, variantIdx);
            }
            return null;
        }

        // "Strike2" -> "Strike_2"; otherwise unchanged.
        public static string NormalizeSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return suffix;
            if (suffix.StartsWith("Strike", StringComparison.Ordinal) && suffix.Length > 6 && char.IsDigit(suffix[6]))
                return "Strike_" + suffix.Substring(6);
            return suffix;
        }

        // "<Hero>_<NormalizedSuffix>" clip name, or null when the controller is not a strike.
        public static string BuildFullName(string controllerName)
        {
            if (!IsStrikeOrCloser(controllerName)) return null;
            string hero = ExtractHeroPrefix(controllerName);
            if (string.IsNullOrEmpty(hero)) return null;
            if (controllerName.Length < hero.Length + 1) return null;
            string rawSuffix = controllerName.Substring(hero.Length + 1);
            return hero + "_" + NormalizeSuffix(rawSuffix);
        }
    }
}
