namespace DiskTrim.Cli;

static class CommandLine
{
    public static int Run(string[] argv)
    {
        var a = Args.Parse(argv);
        if (a.WantsHelp || a.Command is "help") { Help(); return 0; }

        try
        {
            return a.Command switch
            {
                "menu" => Interactive.Run(a.Get("path")),
                "home" => Commands.Home(a),
                "scan" or "tree" => Commands.Scan(a),
                "files" => Commands.Files(a),
                "types" => Commands.Types(a),
                "duplicates" or "dupes" => Commands.Duplicates(a),
                "cleanup" or "clean" => Commands.Cleanup(a),
                "clean-safe" => Commands.CleanSafe(a),
                "apps" => Commands.Apps(a),
                "history" => Commands.History(a),
                "report" => Commands.Report(a),
                "inuse" or "in-use" => Commands.InUse(a),
                "search" or "find" => Commands.Search(a),
                "move" => Commands.Move(a),
                "settings" => Commands.Settings(a),
                null => Interactive.Run(a.Get("path")),
                _ => Unknown(a.Command),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException)
        {
            Ui.Error(ex.Message);
            return 2;
        }
    }

    static int Unknown(string command)
    {
        Ui.Error($"Unknown command '{command}'.");
        Help();
        return 2;
    }

    static void Help()
    {
        Ui.Line($"DiskTrim {AppPaths.Version} — find what fills your disk and free space safely.", ConsoleColor.Cyan);
        Ui.Line("""

            Usage: DiskTrim [command] [path] [options]

            Without a command DiskTrim opens its window ("DiskTrim --path <folder>" opens it on that folder).

              menu                              Interactive menu in the console

              home                              Drives and recommendations (biggest wins first)
              scan  [path] [--depth 2] [--top 20]
                                                Biggest folders and files, each marked Safe/Caution/Decide/Keep
              files [path] [--min-size 100MB] [--older-than 1y] [--type Videos] [--top 50] [--all]
                                                Large & old files (--all also lists 'Keep' files)
              types [path] [--top 25]           Space by file type and extension
              duplicates [path] [--min-size 1MB] [--keep oldest|newest|shortest] [--top 25] [--recycle] [--yes]
                                                Identical files (SHA-256); --recycle moves the extra copies to the Recycle Bin
              cleanup [--run safe|1,4,edge] [--yes]
                                                Measure temp files & caches; --run cleans the chosen items
              apps [filter] [--uninstall name] [--top 40]
                                                Installed programs by size; starts the program's own uninstaller
              history [path] [--compare] [--min-change 100MB]
                                                Saved scans, and what grew between the last two
              report [path] [--out file.html]   Save an HTML report
              search <name|*.ext> [path] [--top 50]
                                                Find files and folders in the scan by name
              move <path> [more…] --to <folder> [--yes]
                                                Move to another drive: copy, verify (SHA-256), then remove the original
              inuse <path>                      Which running programs use a file or folder
              settings                          Show settings
              settings exclude add|remove|list <folder>
              settings schedule daily|weekly|monthly HH:mm | off | status
              settings shell install|uninstall  Right-click menu 'Analyze with DiskTrim (console)'
              settings logs                     Deletion logs (every deleted file is recorded)

            path defaults to the system drive. Ctrl+C stops a scan and keeps the results so far.

            Compatible with DiskTrim's switches:
              --scan [path]   --report [file]   --clean-safe   --path <folder>
            """);
    }
}
