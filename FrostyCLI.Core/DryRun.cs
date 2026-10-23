namespace FrostyCLI.Core
{
    // Ambient flag for the global `--dry-run`: when active, mutating commands run all their resolution
    // and validation but SKIP the actual write (ModifyEbx / AddEbx / ModifyRes / AddToBundle / Save /
    // Build / ...), reporting what they would have done. A process-wide static is fine for a one-shot CLI.
    // NOTE: dry-run validates each command against the CURRENT (unmodified) state — in a multi-step
    // pipeline, a later step that depends on an earlier step's write won't see it. It's a pre-write
    // sanity check (asset/property/target resolves), not a full simulation.
    public static class DryRun
    {
        public static bool Active;

        // Emits a uniform "DRY-RUN: would <message>" line for a skipped write.
        public static void Report(ExportLogger logger, string format, params object[] args)
        {
            string msg = (args != null && args.Length > 0) ? string.Format(format, args) : format;
            if (logger != null) logger.Log("DRY-RUN: would " + msg);
        }
    }
}
