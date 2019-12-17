using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace DropboxConflictSolver
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var regex = new Regex(@"^(?<name>.*) \((Конфликтующая копия( \d+)? с компьютера|Conflicted Copy( \d+)? from) (?<computer>\S+)( on)? (?<date>[0-9\-\.]{10})( \d+)?\)( ?\(\d+\))?(?<extension>\.\S*)?$");
            string dropboxRoot = args[0];
            string recycleDirectory = args.Length >= 2 ? args[1] : Path.Combine(Path.GetDirectoryName(dropboxRoot), "DropboxConflictsRecycle");

            Console.WriteLine($"Dropbox root: {dropboxRoot}");
            Console.WriteLine($"Recycle directory: {recycleDirectory}");

            string GetBaseFileName(string fileName)
            {
                var dir = Path.GetDirectoryName(fileName);
                var onlyName = Path.GetFileName(fileName);
                var m = regex.Match(onlyName);
                if (m.Success && m.Groups["name"].Success && m.Groups["computer"].Success
                    && string.Equals(m.Groups["computer"].Value, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(dir, m.Groups["name"].Value + (m.Groups["extension"].Success ? m.Groups["extension"].Value : ""));
                return null;
            }

            var executionLoop = new BlockingCollection<Action>();
            
            FixDirectory(dropboxRoot, true);

            void FixDirectory(string path, bool recursive)
            {
                var files = Directory.GetFiles(path, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                var originalToCopy = new Dictionary<string, List<string>>();

                foreach (var f in files)
                {
                    var baseName = GetBaseFileName(f);
                    if (baseName != null)
                    {
                        if (!originalToCopy.TryGetValue(baseName, out var list))
                            originalToCopy[baseName] = list = new List<string>();

                        list.Add(f);
                    }
                }

                foreach (var kv in originalToCopy)
                {
                    var all = kv.Value.Append(kv.Key).Where(File.Exists).ToList();
                    var mostRecentDate = all.Max(x => new FileInfo(x).LastWriteTimeUtc);

                    var groups = all.GroupBy(x => new FileInfo(x).LastWriteTimeUtc >= mostRecentDate).ToDictionary(x => x.Key, x => x.ToList());

                    if (groups.Count < 2) continue;

                    bool moveRequired = !groups[true].Any(x => string.Equals(x, kv.Key, StringComparison.OrdinalIgnoreCase));
                    string mostRecentFile = groups[true].First();
                    if (moveRequired)
                    {
                        Console.WriteLine($"Preparing to move {mostRecentFile} to {kv.Key}, {new FileInfo(mostRecentFile).LastWriteTime}");
                        try
                        {
                            File.Move(mostRecentFile, kv.Key + ".fresh");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            continue;
                        }
                    }

                    foreach (var f in groups[false])
                    {
                        try
                        {
                            Console.WriteLine($"Deleting {f}, {new FileInfo(f).LastWriteTime}");
                            string fileDir = Path.GetDirectoryName(f);
                            if (fileDir.StartsWith(dropboxRoot, StringComparison.OrdinalIgnoreCase))
                                fileDir = fileDir.Substring(dropboxRoot.Length);
                            
                            while ((fileDir.Length > 0) && ((fileDir[0] == Path.DirectorySeparatorChar) || (fileDir[0] == Path.AltDirectorySeparatorChar)))
                                fileDir = fileDir.Substring(1);
                            fileDir = Path.Combine(recycleDirectory, fileDir);
                            Directory.CreateDirectory(fileDir);
                            File.Move(f, Path.Combine(fileDir, $"{DateTime.UtcNow.Ticks}. {Path.GetFileName(f)}"));
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                        }
                    }

                    if (!moveRequired) continue;

                    Console.WriteLine($"Moving {mostRecentFile} to {kv.Key}");
                    try
                    {
                        File.Move(kv.Key + ".fresh", kv.Key);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        File.Move(kv.Key + ".fresh", mostRecentFile);
                    }
                }
            }


            Console.WriteLine("Setting up monitoring");
            
            string fixingDirectory = null;
            SetupFsw(new FileSystemWatcher(dropboxRoot,"* (Конфликтующая копия* с компьютера *)*"));
            SetupFsw(new FileSystemWatcher(dropboxRoot,"* (Conflicted Copy* from * on *)*"));

            void SetupFsw(FileSystemWatcher fsw)
            {
                fsw.BeginInit();
                fsw.IncludeSubdirectories = true;
                fsw.Error += (s, a) => Console.WriteLine(a.GetException());
                fsw.Created += (s, a) => executionLoop.Add(() => DetectCopy(a));
                fsw.Renamed += (s, a) => executionLoop.Add(() => DetectCopy(a));
                fsw.EndInit();
                fsw.EnableRaisingEvents = true;
            }


            void DoFixes(string dir)
            {
                fixingDirectory = dir;
                try
                {
                    FixDirectory(dir, false);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    fixingDirectory = null;
                }
            }

            void DetectCopy(FileSystemEventArgs fileSystemEventArgs)
            {
                string copyPath = fileSystemEventArgs.FullPath;
                var basePath = GetBaseFileName(copyPath);
                if (basePath == null) return;
                string dir = Path.GetDirectoryName(basePath);
                if (dir == fixingDirectory) return;
                Console.WriteLine("Detected " + copyPath);
                if (File.Exists(basePath))
                {
                    DoFixes(dir);
                    return;
                }

                var fw = new FileSystemWatcher(dir,  Path.GetFileName(basePath));
                fw.BeginInit();
                fw.Error += (_, a) => Console.WriteLine(a.GetException());
                fw.Created += (s, a) => executionLoop.Add(() => DetectBaseFile(a));
                fw.Renamed += (s, a) => executionLoop.Add(() => DetectBaseFile(a));
                
                void DetectBaseFile(FileSystemEventArgs a)
                {
                    if (string.Equals(a.FullPath, basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (dir == fixingDirectory) return;
                        fw.Dispose();
                        if (File.Exists(copyPath))
                        {
                            Console.WriteLine($"Basefile {basePath} detected");
                            Thread.Sleep(5000);
                            DoFixes(dir);
                        }
                    }
                }

                fw.EndInit();
                fw.EnableRaisingEvents = true;

            }

            Console.WriteLine("Monitoring is running");
            while (true) executionLoop.Take()();
        }
    }
}