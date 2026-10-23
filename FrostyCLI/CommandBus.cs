using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI
{
    internal sealed class CommandBus : IWriteOpBus
    {
        public int Execute(string op, string[] args, ExportLogger l, OutputFormatter f)
        {
            switch (op)
            {
                case "ebx-set":    return EbxWriteCommand.ExecuteSet(args, l, f);
                case "ebx-clone":  return EbxWriteCommand.ExecuteClone(args, l, f);
                case "ebx-delete":  return EbxWriteCommand.ExecuteDelete(args, l, f);
                case "ebx-revert":  return EbxWriteCommand.ExecuteRevert(args, l, f);
                case "ebx-set-ref": return EbxWriteCommand.ExecuteSetRef(args, l, f);
                case "ebx-create":  return EbxWriteCommand.ExecuteCreate(args, l, f);
                case "ebx-new-object": return EbxWriteCommand.ExecuteNewObject(args, l, f);
                case "ebx-array-add":    return EbxWriteCommand.ExecuteArrayAdd(args, l, f);
                case "ebx-array-remove": return EbxWriteCommand.ExecuteArrayRemove(args, l, f);
                case "level-add":    return LevelCommand.ExecuteAdd(args, l, f);
                case "level-place":  return LevelCommand.ExecutePlace(args, l, f);
                case "level-add-entity": return LevelCommand.ExecuteAddEntity(args, l, f);
                case "level-import-lights": return LevelCommand.ExecuteImportLights(args, l, f);
                case "level-remove":   return LevelCommand.ExecuteRemove(args, l, f);
                case "level-clear":    return LevelCommand.ExecuteClear(args, l, f);
                case "level-add-group": return LevelCommand.ExecuteAddGroup(args, l, f);
                case "level-move":     return LevelCommand.ExecuteMove(args, l, f);
                case "level-scaffold": return LevelCommand.ExecuteScaffold(args, l, f);
                case "level-dupe": return LevelDupe.Execute(args, l, f);
                case "level-create": return LevelCreate.Execute(args, l, f);
                case "mvdb-add":    return MvdbCommand.ExecuteAdd(args, l, f);
                case "connection-add":    return ConnectionCommand.ExecuteAdd(args, l, f);
                case "connection-remove": return ConnectionCommand.ExecuteRemove(args, l, f);
                case "mvdb-remove": return MvdbCommand.ExecuteRemove(args, l, f);
                case "mod-diff":    return ModBuildCommand.ExecuteDiff(args, l, f);
                case "mod-apply":   return ModBuildCommand.ExecuteApply(args, l, f);
                case "mod-test":    return ModBuildCommand.ExecuteTest(args, l, f);
                case "mod-lint":    return ModLintCommand.Execute(args, l, f);
                case "mvdb-copy":   return MvdbCommand.ExecuteCopy(args, l, f);
                case "level-validate":  return LevelCommand.ExecuteValidate(args, l, f);
                case "bundle-complete": return BundleCommand.ExecuteComplete(args, l, f);
                case "res-import":  return ResCommand.ExecuteImport(args, l, f);
                case "chunk-import": return ChunkCommand.ExecuteImport(args, l, f);
                case "loc-set":     return LocCommand.ExecuteSet(args, l, f);
                case "import-texture": return ImportCommand.ExecuteTexture(args, l, f);
                case "import-mesh":    return ImportCommand.ExecuteMesh(args, l, f);
                case "import-sound":   return ImportCommand.ExecuteSound(args, l, f);
                case "audio": return new AudioCommand().Execute(args, l, f);
                case "bundle-new":     return BundleCommand.ExecuteNew(args, l, f);
                case "bundle-add":     return BundleCommand.ExecuteAdd(args, l, f);
                case "bundle-remove":  return BundleCommand.ExecuteRemove(args, l, f);
                case "material-set":   return MaterialCommand.ExecuteSet(args, l, f);
                case "material-graft": return MaterialCommand.ExecuteGraft(args, l, f);
                case "cli":
                {
                    ICommand cmd = args.Length > 0 ? CommandRegistry.Get(args[0]) : null;
                    if (cmd == null) { l.LogError("CommandBus: cli: unknown command '" + (args.Length > 0 ? args[0] : "") + "'"); return 1; }
                    var rest = new string[args.Length - 1];
                    System.Array.Copy(args, 1, rest, 0, rest.Length);
                    return cmd.Execute(rest, l, f);
                }
                default:
                    l.LogError("CommandBus: unknown op '" + op + "'");
                    return 1;
            }
        }
    }
}
