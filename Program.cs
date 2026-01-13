#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Resources;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace LocalTempClear
{
    class Program
    {
        static int Main(string[] args)
        {
            var (dryRun, olderThanDays, deleteAll, tempPath, parallelism, autoDetect, throttleBytes, logPath, verbose) = ParseArgs(args);

            // start file logger if requested ログ出力フラグに応じて実行ログを生成
            if (!string.IsNullOrEmpty(logPath)) FileLogger.Start(logPath!);
            Logger.SetVerbose(verbose);

            Logger.Info(R.Get("Info_TempPath", tempPath));
            Logger.Info(dryRun ? R.Get("Mode_DryRun") : R.Get("Mode_Execute"));

            if (autoDetect && parallelism == 1)
            {
                try
                {
                    var detected = TempCleaner.AutoDetectParallelism(tempPath);
                    parallelism = detected;
                    Logger.Info(R.Get("Info_Parallelism", parallelism));
                }
                catch (Exception ex)
                {
                    Logger.Error(R.Get("Error_Unexpected", ex.Message));
                }
            }
            else if (parallelism > 1)
            {
                Logger.Info(R.Get("Info_Parallelism", parallelism));
            }

            TokenBucket? throttle = null;
            if (throttleBytes.HasValue && throttleBytes.Value > 0)
            {
                throttle = new TokenBucket(throttleBytes.Value);
                Logger.Info(R.Get("Info_Throttling", throttleBytes.Value));
            }

            try
            {
                if (deleteAll)
                {
                    TempCleaner.DeleteAllInTemp(tempPath, dryRun, parallelism, throttle);
                }
                else
                {
                    TempCleaner.CleanTemp(tempPath, olderThanDays, dryRun, parallelism, throttle);
                }
            }
            finally
            {
                FileLogger.Stop();
            }

            try
            {
                if (deleteAll)
                {
                    TempCleaner.DeleteAllInTemp(tempPath, dryRun, parallelism);
                }
                else
                {
                    TempCleaner.CleanTemp(tempPath, olderThanDays, dryRun, parallelism);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(R.Get("Error_Unexpected", ex.Message));
                return 2;
            }

            Logger.Info(R.Get("Done"));
            return 0;
        }

        static (bool dryRun, int days, bool deleteAll, string path, int parallelism, bool autoDetect, long? throttleBytes, string? logPath, bool verbose) ParseArgs(string[] args)
        {
            bool dryRun = true;
            int days = 7;
            bool deleteAll = false;
            string? path = null;
            int parallelism = 1; // 1 = no parallelism by default
            bool autoDetect = true; // auto-detect by default
            long? throttleBytes = null; // bytes per second
            string? logPath = null;
            bool verbose = false;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "-r" || a == "--run") { dryRun = false; continue; }
                if (a == "-a" || a == "--delete-all") { deleteAll = true; continue; }
                if (a == "-h" || a == "--help") { PrintHelp(); Environment.Exit(0); }
                if (a == "-d" || a == "--days")
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var v)) days = Math.Max(0, v);
                    continue;
                }

                if (a == "-p" || a == "--path")
                {
                    if (i + 1 < args.Length) path = args[++i];
                    continue;
                }

                if (a == "-P" || a == "--parallel")
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var p)) parallelism = Math.Max(1, p);
                    else parallelism = Environment.ProcessorCount; // if no number given, use CPU count
                    continue;
                }

                if (a == "--no-auto-detect")
                {
                    autoDetect = false; continue;
                }

                if (a == "-t" || a == "--throttle")
                {
                    if (i + 1 < args.Length && long.TryParse(args[++i], out var b)) throttleBytes = Math.Max(0, b);
                    continue;
                }

                if (a == "-l" || a == "--log")
                {
                    if (i + 1 < args.Length) logPath = args[++i];
                    continue;
                }

                if (a == "-v" || a == "--verbose") { verbose = true; continue; }
            }

            path ??= Path.GetTempPath();
            return (dryRun, days, deleteAll, path ?? Path.GetTempPath(), parallelism, autoDetect, throttleBytes, logPath, verbose);
        }

        static void PrintHelp()
        {
            Console.WriteLine(R.Get("Usage"));
            Console.WriteLine(R.Get("Help_Options"));
            Console.WriteLine(R.Get("Help_Run"));
            Console.WriteLine(R.Get("Help_Days"));
            Console.WriteLine(R.Get("Help_Path"));
            Console.WriteLine(R.Get("Help_Parallel"));
            Console.WriteLine(R.Get("Help_Throttle"));
            Console.WriteLine(R.Get("Help_Log"));
            Console.WriteLine(R.Get("Help_Verbose"));
            Console.WriteLine(R.Get("Help_AutoDetectOff"));
            Console.WriteLine(R.Get("Help_DeleteAll"));
            Console.WriteLine(R.Get("Help_Help"));
        }
    }

    static class Logger
    {
        static bool _verbose = false;
        public static void SetVerbose(bool v) => _verbose = v;
        public static void Info(string msg)
        {
            Console.WriteLine($"[INFO] {msg}");
            try { FileLogger.Log($"[INFO] {msg}"); } catch { }
        }
        public static void Error(string msg)
        {
            Console.Error.WriteLine($"[ERROR] {msg}");
            try { FileLogger.Log($"[ERROR] {msg}"); } catch { }
        }
        public static void Dry(string msg)
        {
            Console.WriteLine($"[DRY] {msg}");
            try { FileLogger.Log($"[DRY] {msg}"); } catch { }
        }
        public static void Verbose(string msg)
        {
            if (!_verbose) return;
            Console.WriteLine($"[VERB] {msg}");
            try { FileLogger.Log($"[VERB] {msg}"); } catch { }
        }
    }

    static class R
    {
        static readonly ResourceManager _rm = new ResourceManager("LocalTempClear.Resources", typeof(Program).Assembly);
        public static string Get(string key, params object?[] args)
        {
            var s = _rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
            return args?.Length > 0 ? string.Format(s, args) : s;
        }
    }

    public static class TempCleaner
    {
        // ファイル削除のリトライ設定
        private const int DeleteRetryCount = 3;
        private const int DeleteRetryDelayMs = 200;

        /// <summary>
        /// <paramref name="tempPath"/> 以下で指定日数より古い一時ファイルを削除します。
        /// <paramref name="tempPath"/> が null の場合はシステムの一時パスを使用します。
        /// <paramref name="dryRun"/> が true の場合は削除を行わず、削除候補を表示します。
        /// </summary>
        public static void CleanTemp(string? tempPath = null, int olderThanDays = 7, bool dryRun = true, int maxParallelism = 1, TokenBucket? throttle = null)
        {
            tempPath ??= Path.GetTempPath();
            var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);

            var files = new List<string>();
            var dirs = new List<string>();
            var stack = new Stack<string>();
            var root = tempPath!; // 上で null チェック済みのため非 null を断言
            stack.Push(root);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                foreach (var file in SafeEnumerateFiles(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < cutoff) files.Add(file);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(R.Get("Error_SkipFile", file, ex.Message));
                    }
                }

                foreach (var sub in SafeEnumerateDirectories(dir))
                {
                    dirs.Add(sub);
                    stack.Push(sub);
                }
            }

            if (maxParallelism > 1)
            {
                var po = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism };
                Parallel.ForEach(files, po, file =>
                {
                    try
                    {
                        if (dryRun) { Logger.Dry(R.Get("Dry_DeleteFile", file)); return; }
                        var size = GetFileSizeSafe(file);
                        if (throttle != null && size > 0) throttle.Acquire(size);
                        TryDeleteFile(file);
                    }
                    catch (Exception ex) { Logger.Error(R.Get("Error_SkipFile", file, ex.Message)); }
                });
            }
            else
            {
                foreach (var file in files)
                {
                    if (dryRun) { Logger.Dry(R.Get("Dry_DeleteFile", file)); continue; }
                    var size = GetFileSizeSafe(file);
                    if (throttle != null && size > 0) throttle.Acquire(size);
                    TryDeleteFile(file);
                }
            }

            foreach (var dir in dirs.OrderByDescending(GetDepth))
            {
                try
                {
                    bool isEmpty;
                    try { isEmpty = !Directory.EnumerateFileSystemEntries(dir).Any(); }
                    catch { isEmpty = false; }

                    if (!isEmpty) continue;

                    var lastWrite = Directory.GetLastWriteTimeUtc(dir);
                    if (lastWrite < cutoff)
                    {
                        if (dryRun) { Logger.Dry(R.Get("Dry_DeleteDir", dir)); continue; }
                        TryDeleteDirectory(dir);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(R.Get("Error_SkipDir", dir, ex.Message));
                }
            }
        }

        /// <summary>
        /// <paramref name="tempPath"/> の直下にあるエントリ（トップレベル）を削除します。
        /// ディレクトリは再帰削除を試み、失敗した場合は個別削除にフォールバックして失敗を無視します。
        /// </summary>
        public static void DeleteAllInTemp(string? tempPath = null, bool dryRun = true, int maxParallelism = 1, TokenBucket? throttle = null)
        {
            // コードを実行したユーザーの %TEMP% 環境変数値を取得
            // ex) c:\\Users\\%USER_NAME%\\AppData\\Local\\Temp %TEMP%\\AppData\\Local\\Temp
            // eq) Environment.CurrentDirectory = Environment.GetEnvironmentVariable("temp");
            tempPath ??= Path.GetTempPath();
            var root = tempPath!;

            if (maxParallelism > 1)
            {
                var po = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism };
                Parallel.ForEach(SafeEnumerateFiles(root), po, file =>
                {
                    try
                    {
                        if (dryRun) { Logger.Dry(R.Get("Dry_DeleteFile", file)); return; }
                        var size = GetFileSizeSafe(file);
                        if (throttle != null && size > 0) throttle.Acquire(size);
                        TryDeleteFile(file);
                    }
                    catch (Exception ex) { Logger.Error(R.Get("Error_SkipFile", file, ex.Message)); }
                });

                Parallel.ForEach(SafeEnumerateDirectories(root), po, dir =>
                {
                    if (dryRun) { Logger.Dry(R.Get("Dry_DeleteDirTree", dir)); return; }
                    if (!TryDeleteDirectoryRecursive(dir))
                    {
                        Logger.Error(R.Get("Info_FailedToFullyDeleteDir", dir));
                        DeleteDirectoryContentsSafe(dir, false);
                        TryDeleteDirectory(dir);
                    }
                });
            }
            else
            {
                foreach (var file in SafeEnumerateFiles(root))
                {
                    if (dryRun) { Logger.Dry(R.Get("Dry_DeleteFile", file)); continue; }
                    var size = GetFileSizeSafe(file);
                    if (throttle != null && size > 0) throttle.Acquire(size);
                    TryDeleteFile(file);
                }

                foreach (var dir in SafeEnumerateDirectories(root))
                {
                    if (dryRun) { Logger.Dry(R.Get("Dry_DeleteDirTree", dir)); continue; }
                    else
                    {
                        if (!TryDeleteDirectoryRecursive(dir))
                        {
                            Logger.Error(R.Get("Info_FailedToFullyDeleteDir", dir));
                            DeleteDirectoryContentsSafe(dir, false);
                            TryDeleteDirectory(dir);
                        }
                    }
                }
            }
        }

        static IEnumerable<string> SafeEnumerateFiles(string dir)
        {
            // 呼び出し元での二重走査を避けるため、トップレベルのみ列挙します
            IEnumerable<string> items;
            try
            {
                items = Directory.EnumerateFiles(dir);
            }
            catch (Exception ex)
            {
                Logger.Error(R.Get("Error_SkipListingFiles", dir, ex.Message));
                yield break;
            }

            foreach (var f in items) yield return f;
        }

        static IEnumerable<string> SafeEnumerateDirectories(string dir)
        {
            IEnumerable<string> items;
            try
            {
                items = Directory.EnumerateDirectories(dir);
            }
            catch (Exception ex)
            {
                Logger.Error(R.Get("Error_SkipListingDirs", dir, ex.Message));
                yield break;
            }

            foreach (var d in items) yield return d;
        }

        static void DeleteDirectoryContentsSafe(string dir, bool dryRun)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            var all = new List<string>();

            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                all.Add(cur);

                foreach (var file in SafeEnumerateFiles(cur))
                {
                    if (dryRun) { Logger.Dry(R.Get("Dry_DeleteFile", file)); continue; }
                    TryDeleteFile(file);
                }

                foreach (var sub in SafeEnumerateDirectories(cur)) stack.Push(sub);
            }

            foreach (var d in all.OrderByDescending(GetDepth)) TryDeleteDirectory(d);
        }

        /// <summary>
        /// 一時的なエラーを考慮して少回数リトライしながらファイルを削除します。成功したら true を返します。
        /// </summary>
        static bool TryDeleteFile(string file)
        {
            for (int i = 0; i < DeleteRetryCount; i++)
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    return true;
                }
                catch (IOException) { Thread.Sleep(DeleteRetryDelayMs); }
                catch (UnauthorizedAccessException) { Thread.Sleep(DeleteRetryDelayMs); }
                catch (Exception ex) { Logger.Error(R.Get("Error_SkipFile", file, ex.Message)); return false; }
            }

            Logger.Error(R.Get("Error_FailedDeleteAfterRetries", file));
            return false;
        }

        static bool TryDeleteDirectory(string dir)
        {
            try
            {
                Directory.Delete(dir, false);
                return true;
            }
            catch (Exception ex) { Logger.Error(R.Get("Error_FailedDeleteDir", dir, ex.Message)); return false; }
        }

        static bool TryDeleteDirectoryRecursive(string dir)
        {
            try
            {
                Directory.Delete(dir, true);
                return true;
            }
            catch { return false; }
        }

        static int GetDepth(string? path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            return path.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar).Length;
        }

        static long GetFileSizeSafe(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        public static int AutoDetectParallelism(string tempPath)
        {
            // 簡易ベンチ: 小さなファイルをいくつか作成・削除して1ファイルの削除速度を測定し、閾値で並列度を決定します
            var sw = Stopwatch.StartNew();
            var testDir = Path.Combine(tempPath, $"ltc_autodetect_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testDir);
            var files = new List<string>();
            try
            {
                const int sampleFiles = 4;
                const int sizeBytes = 64 * 1024; // 64KB
                var data = new byte[sizeBytes];
                for (int i = 0; i < sampleFiles; i++)
                {
                    var f = Path.Combine(testDir, i + ".tmp");
                    File.WriteAllBytes(f, data);
                    files.Add(f);
                }

                // measure delete time sequentially
                var t0 = Stopwatch.StartNew();
                foreach (var f in files) File.Delete(f);
                t0.Stop();
                var secsPerOp = t0.Elapsed.TotalSeconds / sampleFiles;

                // heuristic thresholds
                if (secsPerOp < 0.005) return Math.Max(2, Environment.ProcessorCount * 2); // very fast -> more parallelism
                if (secsPerOp < 0.02) return Math.Max(1, Environment.ProcessorCount); // fast
                return 1; // slow -> no parallelism
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

    }

    // トークンバケットによる簡易スロットリング（バイト/秒）
    public class TokenBucket
    {
        readonly double _rate; // bytes per second
        double _tokens;
        long _last; // ticks
        readonly object _lock = new object();

        public TokenBucket(double bytesPerSecond)
        {
            _rate = bytesPerSecond;
            _tokens = bytesPerSecond; // burst up to 1s
            _last = Stopwatch.GetTimestamp();
        }

        public void Acquire(long bytes)
        {
            while (true)
            {
                lock (_lock)
                {
                    Refill();
                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        return;
                    }
                }
                Thread.Sleep(50);
            }
        }

        void Refill()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _last) / (double)Stopwatch.Frequency;
            if (elapsed <= 0) return;
            _tokens = Math.Min(_tokens + elapsed * _rate, _rate); // capacity = rate
            _last = now;
        }
    }

    // シンプルなファイルログ（スレッドセーフ、バックグラウンド書込）
    public static class FileLogger
    {
        static readonly ConcurrentQueue<string> _q = new();
        static CancellationTokenSource? _cts;
        static Task? _task;
        static StreamWriter? _sw;

        public static void Start(string path)
        {
            if (_cts != null) return; // already started
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            Directory.CreateDirectory(dir);
            _sw = new StreamWriter(new FileStream(Path.GetFullPath(path), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => Worker(_cts.Token));
        }

        public static void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _task?.Wait(2000);
            try { _sw?.Dispose(); } catch { }
            _cts = null; _task = null; _sw = null;
        }

        public static void Log(string line)
        {
            if (_cts == null) return;
            _q.Enqueue($"[{DateTime.Now:O}] {line}");
        }

        static void Worker(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested || !_q.IsEmpty)
            {
                while (_q.TryDequeue(out var item))
                {
                    try { _sw?.WriteLine(item); } catch { }
                }
                Thread.Sleep(100);
            }
        }
    }
}



