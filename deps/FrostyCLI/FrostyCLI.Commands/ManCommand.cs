using System;
using System.Collections.Generic;
using System.Linq;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `man` -- lists every registered CLI command with a one-line synopsis, or `man <command>` for its
    // usage detail. Driven by the LIVE CommandRegistry, so a newly-registered command still shows up
    // (flagged "(undocumented)") even if this table wasn't updated -- the list can't silently drift.
    public class ManCommand : ICommand
    {
        public string Name => "man";

        private struct Doc
        {
            public string Synopsis;   // one line, for the list
            public string Usage;      // usage / subcommands, for `man <command>`
            public Doc(string synopsis, string usage) { Synopsis = synopsis; Usage = usage; }
        }

        // Keyed by command name (matches ICommand.Name). Keep in sync with the registrations in Program.cs.
        private static readonly Dictionary<string, Doc> Docs = new Dictionary<string, Doc>(StringComparer.OrdinalIgnoreCase)
        {
            { "ls",           new Doc("List assets by path prefix and/or type",            "ls [<prefix>] [--type <AssetType>] [--count] [--limit <n>]") },
            { "ebx",          new Doc("Inspect & edit EBX assets",                          "ebx <dump [--file <raw>] [--object <instance-guid>]|get|search|refs|diff-shipped|set [--object <sel> | --each-type <Type>] (prop may use [*])|set-ref|new-object|array-add|array-remove|clone|create|delete|revert> ... | ebx diff-shipped <ebx> --object <selector|guid> [--type <TypeName>] [--scan <path-prefix>] [--all] [--max-assets <n>] [--max-instances <n>] [--max-fields <n>] [--max-depth <n>] [--format table|json]") },
            { "where",        new Doc("Resolve an asset by path, GUID, or substring",       "where <name-or-guid> [--limit <n>]") },
            { "export",       new Doc("Export a mesh (FBX/OBJ) or texture (DDS)",           "export <ebx-path> --out <file> [--format fbx|obj|dds]") },
            { "export-batch", new Doc("Bulk-export many assets to a folder",                "export-batch --prefix <p> --out <dir> [--type <t>] [--continue-on-error]") },
            { "export-anim",  new Doc("Export one hero-strike animation clip to SEAnim",   "export-anim --clip <name> --out <file.seanim> [<state-machine-ebx>] [--skeleton <path>] [--any]  (--any: match any clip controller by its Name, not just hero strikes)") },
            { "export-anim-batch", new Doc("Export all hero-strike clips to SEAnim files",  "export-anim-batch --out <dir> [<state-machine-ebx>] [--skeleton <path>] [--filter <name-substr>] [--quiet]") },
            { "anim-info",    new Doc("List or dump clips in an animation bank by name (shared bank by default)", "anim-info <name-filter> [--dump] [--file <bank.bin>] [--res <bank res>]") },
            { "anim-bank",    new Doc("Browse any asset in an animation bank: types, find, dump, refs, tree, mask", "anim-bank <types|find|dump|refs|tree|mask> [name|guid] [--file <bank.bin>] [--res <bank res>] [--type T] [--depth n] [--max n]") },
            { "anim-frame-offset", new Doc("Move a bone (or, with --move-children, its children) in a Frame-codec pose, in place", "anim-frame-offset [--copy-pose from:to] (--edit | --move-children) <clip>:<bone>:<dx>,<dy>,<dz> [...] [--skeleton S] [--res <bank res>] --project <p> --save") },
            { "anim-index-remap", new Doc("Make one enum slot pick what another picks in every IndexChooser (or set one entry), in place", "anim-index-remap --enum <EnumGS> [--graph roots] [--also choosers] [--copy from:to]... [--set chooser:index=asset]... [--dry-run] --project <p> --save") },
            { "import-anim",  new Doc("Import a SEAnim over a hero-strike clip (then mod build)", "import-anim <file.seanim> --clip <name> [<state-machine-ebx>] [--skeleton <path>]") },
            { "export-skinmesh", new Doc("Export a skinned mesh to .faemesh (skin+bones+albedo) for FAAE", "export-skinmesh <ebx-path> --out <file.faemesh> [--skeleton <path>]") },
            { "export-face", new Doc("Export a facial-layer clip (FACS faceposer) to SEAnim with FACIAL_LOD1_* keys", "export-face --clip <name> --out <file.seanim> [--skeleton <path>] [--library <name>]") },
            { "audio", new Doc("Inspect, package, migrate sound graphs and place ambient sounds/SFX", "audio inspect <wave> [--out <json>] | audio inventory [name-filter] [--out <json>] | audio dependencies <patch> [--out <json>] | audio export <wave> --out <new-directory> | audio import <directory> --as <new-wave> --template <destination-wave> --bundle <bundle> | audio place <patch> --into <layer> --pos x,y,z [--play-on-creation true|false] | audio graph-inspect <patch> [--out <json>] | audio graph-validate <patch> [--out <json>] | audio graph-export <patch> --out <new-file.json> | audio graph-plan <package.json> --as <new-patch> [--map <migration.json>] [--out <report.json>] | audio graph-import <package.json> --as <new-patch> --bundle <bundle> [--map <migration.json>] [--out <report.json>]") },
            { "import",       new Doc("Import a texture/mesh/sound/resource into an asset", "import <texture|mesh|sound|resource> <file> --as <ebx-path> [--template <src>] [...] | import sound <file.wav> --as <wave> [--variation <n>] [--pcm|--ealayer3] [--flatten]") },
            { "level",        new Doc("Inspect/validate level trees & stats, edit entities and BFII static placements, place props end to end, scaffold SubWorlds, duplicate or create a level", "level <tree|stats|validate|roundtrip|list|add|add-entity|add-group|place|remove|clear|move|scaffold|dupe|create|combat-area> ... | level dupe <shipped-level> --as <new-level> [--types A,B] [--frosty-plugins <dir>] | level combat-area <level> [--area rect:x0,z0,x1,z1|x,z;x,z;...] [--floor y] [--height h] [--field <link field>] [--only <partition name part>] | level loading-screen <level> --like <shipped level> [--movie <file.webm> | --image <file> [--seconds s] [--codec vp8|vp9]]") },
            { "material",     new Doc("Set a mesh material / shader parameter",             "material set <mesh> --material <i> --param <name> --value <v>") },
            { "mvdb",         new Doc("Edit MeshVariationDatabase entries",                 "mvdb <list|add|copy|remove> ...") },
            { "port",         new Doc("Port static props and whole placement layers between games via package directories", "port export <blueprint> --out <dir> [--mvdb <db>]... | port import <dir> --as <blueprint> [--mesh-template <mesh>] [--keep-template-materials] [--blueprint-template <bp>] [--physics-template <havok>] [--texture-template <tex>] [--shader-fallback <shader>] [--mvdb <db>] [--bundle <name>] [--no-physics] [--keep-composite] | port export-layer <level> --out <dir> [--group <i>] [--filter <s>] | port import-layer [--as-members] [--only-ported] [--prefer-ported] <dir> --into <level> --group <i> [--source-group <i>] [--offset x,y,z] [--ported-prefix <path>] [--limit <n>] [--bundle <name>|--no-bundle] [--continue] [--only-guid] [--new-members-only]") },
            { "loc",          new Doc("Read & edit localized strings",                      "loc <get|set|search> ...") },
            { "bundle",       new Doc("Bundle membership (optionally with sub-resources), and the headless Bundle Manager pass",  "bundle depots [--match <s>]... [--skip <s>]... | bundle <ls|add|remove|new|complete|contents|mirror> <asset> [--to|--from <bundle>] [--with-resources] | bundle new <name> [--superbundle <sb>|--like <bundle>] [--type <t>] | bundle complete [--level <bundle>,...] [--frosty-plugins <dir>] [--cache-dir <dir>] [--cosmetic] [--no-save] [--set KEY=VALUE] | bundle contents <bundle> [--out f.tsv] | bundle mirror <src> --to <dst> [--skip-cloned a=b]") },
            { "connection",   new Doc("Inspect & author a blueprint's Property/Link/Event wiring", "connection ls <blueprint> [--kind property|link|event] [--names <file>] | connection add <blueprint> [--source-ebx <asset>] --source <selector|guid> --source-field <name|0xid> [--target-ebx <asset>] --target <selector|guid> --target-field <name|0xid> [--kind property|link|event] [--realm <name>] [--property-type <name>] [--non-static-source] [--flags <n>] | connection remove <blueprint> --index <n> [--kind property|link|event] | connection names [--names <file>] [--resolve <id>] [--hash <text>]") },
            { "enlighten",    new Doc("Inspect precomputed-GI databases and baked textures", "enlighten inspect <level-or-enlighten-ebx> [--format table|json] [--hash]") },
            { "terrain",      new Doc("Inspect terrain streaming trees and rewrite their elevations",
                "terrain <info|validate|export-heights|import-heights|retarget> ...\n" +
                "    terrain info <terrain> [--against <terrain>] [--format table|json]\n" +
                "    terrain validate <terrain> [--format table|json]\n" +
                "    terrain export-heights <terrain> --out <file.r16> [--size <n>]\n" +
                "    terrain import-heights <terrain> --from <file.r16> [--band <lo>:<hi>|auto] [--keep-derived] [--out <file>]\n" +
                "    terrain retarget <donor> --source <terrain-or-payload-file> [--band <lo>:<hi>|auto] [--keep-derived] [--out <file>]\n" +
                "    terrain retarget <donor> --flat <metres> [--out <file>]\n" +
                "\n" +
                "    <terrain> is a res name, the terrain's EBX path, or a payload file on disk.\n" +
                "    A rewrite touches only the heightfield record: mask, colour, material and layer\n" +
                "    trees are preserved byte for byte. Without --out the resource is written into the\n" +
                "    loaded project.") },
            { "res",          new Doc("Raw Res data: list / export / import bytes, show res meta", "res <ls|export|import> ... | res export <res> --out <file> [--framed] | res meta <res> | res copy <source-res> --to <target-res> [--fast] | res revert <res>") },
            { "rvm",          new Doc("Inspect a level's compiled shader permutation databases", "rvm ls <level> [--format table|json] | rvm check <level>|--all [--format table|json] [--strict] [--max-issues <n>]") },
            { "kyber",        new Doc("Declare a level to Kyber (online hosting): stored as Kyber Level Declarator's chunk, shipped as the embedded KyberLevelDeclarationData", "kyber declare --level <level path> --name <display name> --modes <ids> [--image <png|jpg>] | kyber show | kyber remove --level <level path>") },
            { "chunk",        new Doc("Raw Chunk data: export / import bytes",              "chunk <export|import> ...") },
            { "project",      new Doc("Manage the .fbproject (new/load/save)",              "project <new|load|save> [<file.fbproject>]") },
            { "mod",          new Doc("Build / diff / apply / lint / in-game test .fbmod mods", "mod <build|diff|apply|lint|test|review> ... | mod review <mod.fbmod>|--vanilla [--against <other.fbmod>|vanilla] [--level <path>] [--mode <GameMode>] [--shots <shots.json>] [--play-seconds 60] [--out <dir>] | mod apply <file.fbmod> [--only <name>,<prefix*>,...] | mod build --out <file.fbmod> --mod-name <n> [--mod-version <v>] [--mod-author <a>] [--mod-description <d>] [--mod-category <c>] [--embed <name>=<file>]... | mod lint [--format table|json] [--strict] [--max-issues <n>] [--max-depth <n>] [--no-levels] | mod test <file.fbmod>|--vanilla [--level <path>] [--mode <GameMode>] [--timeout <s>] [--task FrostyAutoTest] [--script <autotest.ps1>] [--module <FrostyGameLink.dll>] [--keep] [--deploy] [--play | --play-seconds <s>] [--feeds|--feeds-early] [--record <file> [--record-filter <f>] [--record-props]]") },
            { "logic",        new Doc("Offline logic: follow wiring across blueprints, lint wiring before a launch", "logic trace <blueprint> <Type[tag]>.<Port> [--back] [--depth <n>] [--max <n>] [--kinds event,property,link] | logic lint <blueprint>... [--scan <path-prefix>] [--max-assets <n>] [--summary] [--duplicates]") },
            { "game",         new Doc("Live game: logic-event / property feeds, in-game overlay, raw gamelink commands (see tools/gamelink/TRACE.md)", "game status | game trace <on [filter]|off|[n]|status|filter <f>|clear|pause|resume|dump|guid 0|1|2|where 0|1|addr 0|1> | game prop <on [filter]|off|[n]|status|filter <f>|clear> | game overlay <on [filter]|off|filter <f>|feed events|props|both> | game follow [--prop|--both] [--seconds <s>] [--keep-on] [filter] | game record <file> [--prop|--both] [--seconds <s>] [filter] | game diff <a.txt> <b.txt> [--loose] [--max <n>] | game wires <blueprint filter> [--events|--props] [--against <recording>]... [--never] | game budget | game timeline <recording> [--all] [--match <a,b>] | game fire|deliver <Type[tag]>.<Event> @<blueprint> [S|C] [all] | game inspect <Type[tag]> @<blueprint> [S|C] | game deploy [--timeout <s>] | game play [--seconds <s>] [--record <file>] | game shot --at x,y,z [--look x,y,z] [--out <file.png>] [--hud] [--wait <s>] [--keep-cam] | game shot --list <shots.json> --out <dir> | game where | game set <Type[tag]>.<Field> = <value> @<blueprint> [S|C] [all] | game crash [--log <gamelink.log>] [--known <known-crashes.json>] | game log [n] | game send <raw gamelink command> [--port <n>]") },
            { "corpus",       new Doc("Query the mod corpus: field values, conflicts, wiring, exact mod-vs-game diffs", "corpus <stats|field|asset|mod|conflicts|bundles|wires|vanilla|diff|donor|dump-vanilla> [--dir <corpus folder>] ... | corpus field <name> [--top n] | corpus diff <mod> [--limit n] [--multi] [--out f.jsonl] | corpus donor <added-asset> | corpus dump-vanilla [--limit n]") },
            { "index",        new Doc("Build & cache the asset index for fast lookups",     "index build [--refs]") },
            { "plugins",      new Doc("List loaded plugins, adapters & handlers",           "plugins") },
            { "run",          new Doc("Run a JSON pipeline of operations",                  "run <pipeline.json>") },
            { "man",          new Doc("List all commands, or show one command's usage",     "man [<command>]") },
        };

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            // `man --names`: bare command names, one per line (for shell-completion scripts to consume).
            if (args != null && Array.IndexOf(args, "--names") >= 0)
            {
                foreach (string n in CommandRegistry.All.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                    Console.Out.WriteLine(n);
                return 0;
            }

            string which = args != null && args.Length > 0 && !args[0].StartsWith("--") ? args[0] : null;

            // `man <command>`: show that command's usage detail.
            if (which != null)
            {
                Doc d;
                if (!Docs.TryGetValue(which, out d))
                {
                    Console.Error.WriteLine("man: unknown command '" + which + "'. Run 'man' for the full list.");
                    return 1;
                }
                Console.Out.WriteLine(which + " — " + d.Synopsis);
                Console.Out.WriteLine();
                Console.Out.WriteLine("  Usage: frostycli " + d.Usage);
                return 0;
            }

            // `man`: list every registered command (authoritative), with synopsis from the table.
            var names = CommandRegistry.All.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            int width = names.Count > 0 ? names.Max(n => n.Length) : 4;

            Console.Out.WriteLine("FrostyCLI commands (run 'man <command>' for usage, or '--help' style flags per command):");
            Console.Out.WriteLine();
            foreach (string name in names)
            {
                Doc d;
                string synopsis = Docs.TryGetValue(name, out d) ? d.Synopsis : "(undocumented)";
                Console.Out.WriteLine("  " + name.PadRight(width) + "  " + synopsis);
            }
            Console.Out.WriteLine();
            Console.Out.WriteLine("Global flags: --game swbf1|swbf2  --game-path <path>  --handler-plugins <dir>  --json  --quiet");
            return 0;
        }
    }
}
