using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Mono.Options;

namespace processtimeout
{
    class MainClass
    {
        static string prefix;
        static bool countdown;
        static TimeSpan timeout = TimeSpan.FromMinutes(10);
        static DateTime startTime;

        static object lastWriteLock = new object();
        static DateTime lastWrite;

        public static int Main(string[] args)
        {
            var options = new OptionSet
            {
                { "prefix=", (v) => prefix = v },
                { "timeout=", (v) => timeout = TimeSpan.FromMinutes (int.Parse (v)) },
                { "countdown", (v) => countdown = true },
                { "tick-uptime", (v) => TickUpdate () },
            };

            var cmdline = options.Parse(args).ToList ();
            var executable = cmdline [0];
            var arguments = cmdline.Skip(1).ToArray();

            return Execute(executable, arguments, timeout);
        }

        static void WriteLine (string line)
        {
            if (!string.IsNullOrEmpty(prefix))
                line = prefix + line;
            if (countdown)
            {
                var spent = DateTime.UtcNow - startTime;
                var left = timeout - spent;
                var counter = left.ToString("hh\\:mm\\:ss\\.fff\\ ");
                line = counter + line;
            }
            Console.WriteLine(line);
        }

        static void TickUpdate()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(10));
                    var process = new Process();
                    process.StartInfo.FileName = "sysctl";
                    process.StartInfo.Arguments = "-n vm.loadavg";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();

                    var sb = new StringBuilder();

                    StreamReader(process.StandardOutput, (v) => sb.AppendLine (v), out var stdoutDone);
                    StreamReader(process.StandardError, (v) => sb.AppendLine (v), out var stderrDone);

                    process.WaitForExit();

                    var line = sb.ToString().Trim().Trim('{', '}').Trim ();
                    var sinceLastWrite = (DateTime.UtcNow - lastWrite).ToString("hh\\:mm\\:ss\\.fff");
                    WriteLine("⏱  " + line + " --- " + sinceLastWrite + "  ⏱");
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        static void StreamReader (StreamReader stream, Action<string> writeline, out ManualResetEvent done)
        {
            var readerDone = new ManualResetEvent(false);
            var outputReader = new Thread(() =>
            {
                while (true)
                {
                    var line = stream.ReadLine();
                    if (line is null)
                    {
                        readerDone.Set();
                        return;
                    }
                    writeline(line);
                }
            });
            outputReader.IsBackground = true;
            outputReader.Start();

            done = readerDone;
        }

        static int Execute(string executable, string[] arguments, TimeSpan timeout)
        {
            Console.WriteLine($"Executing '{executable} {string.Join(" ", arguments)}' with a timeout of {timeout.TotalMinutes} minutes.");
            var process = new Process();
            process.StartInfo.FileName = executable;
            process.StartInfo.Arguments = string.Join(" ", arguments);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            startTime = DateTime.UtcNow;
            process.Start();

            var pid = process.Id;

            static void writeLine (string line)
            {
                lock (lastWriteLock)
                    lastWrite = DateTime.UtcNow;
                WriteLine(line);
            };

            StreamReader(process.StandardOutput, writeLine, out var stdoutDone);
            StreamReader(process.StandardError, writeLine, out var stderrDone);

            if (process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                Console.WriteLine("Execution completed successfully");
                return process.ExitCode;
            }

            var offspring = ProcessManager.GetChildProcessIdsInternal(Console.Error, pid);
            Console.WriteLine($"Execution timed out. Will kill the following pids: {string.Join(", ", offspring.Select(v => v.ToString()))}");
            ProcessManager.Kill(Console.Error, offspring);
            return 1;
        }
    }
}
