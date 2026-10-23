using System;
using FrostyCLI.Commands;
using FrostyCLI.Core;
using FrostyCLI.Pipeline;

namespace FrostyCLI
{
    class Program
    {
        static int Main(string[] args)
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(args, out remaining);
            opts = ArgParser.ResolveDefaults(opts); // fill game-path/project/etc. from frostycli.json + env
            DryRun.Active = opts.DryRun;            // global: mutating commands report instead of writing
            var logger = new ExportLogger(quiet: opts.Quiet);
            var formatter = new OutputFormatter(json: opts.Json);

            // Wire write-op bus (lets Pipeline invoke write commands without referencing Commands' types directly)
            WriteOpBus.Instance = new CommandBus();
            // Wire the pipeline runner so RunCommand (in Commands) can invoke it without Commands
            // referencing Pipeline -- breaking the Commands<->Pipeline cold-build cycle.
            PipelineRunnerBus.Instance = new JsonPipelineRunner();

            if (remaining.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string cmdName = remaining[0];
            string[] cmdArgs = new string[remaining.Length - 1];
            Array.Copy(remaining, 1, cmdArgs, 0, cmdArgs.Length);

            CommandRegistry.Register(new LsCommand());
            CommandRegistry.Register(new ExportCommand());
            CommandRegistry.Register(new EbxGetCommand());
            CommandRegistry.Register(new PluginsCommand());
            CommandRegistry.Register(new RunCommand());
            CommandRegistry.Register(new ProjectCommand());
            CommandRegistry.Register(new ModBuildCommand());
            CommandRegistry.Register(new ExportBatchCommand());
            CommandRegistry.Register(new ExportAnimCommand());
            CommandRegistry.Register(new AnimInfoCommand());
            CommandRegistry.Register(new AnimBankCommand());
            CommandRegistry.Register(new AnimIndexRemapCommand());
            CommandRegistry.Register(new AnimFrameOffsetCommand());
            CommandRegistry.Register(new ExportAnimBatchCommand());
            CommandRegistry.Register(new ImportAnimCommand());
            CommandRegistry.Register(new ExportSkinMeshCommand());
            CommandRegistry.Register(new ExportFaceCommand());
            CommandRegistry.Register(new LevelCommand());
            CommandRegistry.Register(new ResCommand());
            CommandRegistry.Register(new RvmCommand());
            CommandRegistry.Register(new ChunkCommand());
            CommandRegistry.Register(new IndexCommand());
            CommandRegistry.Register(new MvdbCommand());
            CommandRegistry.Register(new MeshCommand());
            CommandRegistry.Register(new LocCommand());
            CommandRegistry.Register(new ImportCommand());
            CommandRegistry.Register(new AudioCommand());
            CommandRegistry.Register(new BundleCommand());
            CommandRegistry.Register(new MaterialCommand());
            CommandRegistry.Register(new ShaderDbCommand());
            CommandRegistry.Register(new WhereCommand());
            CommandRegistry.Register(new ConnectionCommand());
            CommandRegistry.Register(new PortCommand());
            CommandRegistry.Register(new EnlightenCommand());
            CommandRegistry.Register(new VeCommand());
            CommandRegistry.Register(new TerrainCommand());
            CommandRegistry.Register(new GameCommand());
            CommandRegistry.Register(new LogicCommand());
            CommandRegistry.Register(new NavmeshCommand());
            CommandRegistry.Register(new KyberCommand());
            CommandRegistry.Register(new CorpusCommand());
            CommandRegistry.Register(new ManCommand());

            var command = CommandRegistry.Get(cmdName);
            if (command == null)
            {
                Console.Error.WriteLine("Unknown command: " + cmdName);
                PrintUsage();
                return 1;
            }

            // `frostycli <cmd> --help|-h` -> show that command's usage via `man` (no game needed).
            if (cmdName != "man" && (Array.IndexOf(cmdArgs, "--help") >= 0 || Array.IndexOf(cmdArgs, "-h") >= 0))
                return new ManCommand().Execute(new[] { cmdName }, logger, formatter);

            // Make the global --handler-plugins flag (or frostycli.json "handler-plugins") available to
            // the `run` pipeline path, which self-initializes the Session and would otherwise lose it.
            Session.DefaultHandlerPluginsDir = opts.HandlerPluginsDir;
            Session.SaveRequested = opts.Save;
            Session.SaveAsPath    = opts.SaveAs;

            // `run` (pipeline) initializes its own session from the pipeline JSON's game/game-path, so
            // Program must NOT pre-init here (Session.Initialize is once-per-process) — otherwise a
            // resolved env/config game-path would clobber the pipeline's chosen game.
            // Corpus queries read corpus.db and need no game; only the few that read vanilla
            // values from the install do (see CorpusCommand.NeedsGame).
            bool gameFree = cmdName == "help" || cmdName == "man" || cmdName == "run"
                         || cmdName == "game"   // talks to a running game over gamelink's socket
                         || (cmdName == "corpus" && !CorpusCommand.NeedsGame(cmdArgs));
            if (!gameFree && opts.GamePath != null)
            {
                if (opts.Game != "swbf1" && opts.Game != "swbf2")
                {
                    Console.Error.WriteLine("Unknown --game '" + opts.Game + "' (expected swbf1 or swbf2).");
                    return 1;
                }
                try
                {
                    Session.Initialize(opts.Game, opts.GamePath, opts.PluginsDir, opts.AdaptersDir, logger, opts.HandlerPluginsDir);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Session init failed: " + ex.Message);
                    return 1;
                }

                // Auto-load a project named in frostycli.json / --project, so subsequent commands operate on it.
                if (!string.IsNullOrEmpty(opts.Project) && !Session.Project.IsLoaded)
                {
                    try { Session.Project.Load(opts.Project, logger); }
                    catch (Exception ex) { Console.Error.WriteLine("Auto-load project failed: " + ex.Message); return 1; }
                }

                // --save-as with nothing loaded means "collect this run's edits into a new project".
                // A write command needs a project to record into, and a fresh one has nothing to lose.
                if (opts.SaveAs != null && !Session.Project.IsLoaded)
                {
                    try { Session.Project.New(); }
                    catch (Exception ex) { Console.Error.WriteLine("Could not start a new project: " + ex.Message); return 1; }
                }
            }
            else if (!gameFree)
            {
                // The command will need a game but none was resolved — say so clearly rather than letting
                // it fail later with a confusing "AssetManager not initialized".
                Console.Error.WriteLine("No game path: pass --game-path <dir>, set FROSTYCLI_GAME_PATH_" +
                    (opts.Game ?? "swbf2").ToUpperInvariant() + ", or add \"game-path\" to frostycli.json.");
                return 1;
            }

            int exitCode = command.Execute(cmdArgs, logger, formatter);

            // A project lives only as long as this process: every write command mutates the loaded
            // project in memory and the change is gone at exit unless it is written back. Nothing
            // else in the CLI does that, so --save / --save-as is what makes a write stick.
            if (opts.Save || opts.SaveAs != null)
            {
                if (exitCode != 0)
                {
                    Console.Error.WriteLine("Not saving: the command failed (exit " + exitCode + ").");
                }
                else if (DryRun.Active)
                {
                    DryRun.Report(logger, "save project to {0}",
                        opts.SaveAs ?? Session.Project.FilePath ?? "(loaded file)");
                }
                else if (!Session.Project.IsLoaded)
                {
                    Console.Error.WriteLine("--save: no project is loaded. Pass --project <file.fbproject>.");
                    return 1;
                }
                else if (opts.SaveAs == null && string.IsNullOrEmpty(Session.Project.FilePath))
                {
                    Console.Error.WriteLine("--save: the loaded project has no file. Use --save-as <file.fbproject>.");
                    return 1;
                }
                else if (!Session.Project.Save(opts.SaveAs, logger))
                {
                    return 1;
                }
                else
                {
                    logger.Log("Saved project: {0}", opts.SaveAs ?? Session.Project.FilePath);
                }
            }

            return exitCode;
        }

        static void PrintUsage()
        {
            Console.Error.WriteLine("Usage: frostycli [--game swbf1|swbf2] [--game-path <path>] [--project <file>] [--save|--save-as <file>] [--json] [--quiet] <command> [args]");
            Console.Error.WriteLine("Run 'frostycli man' for the full list of commands, or 'frostycli man <command>' for one command's usage.");
        }
    }
}
