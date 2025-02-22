// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Graph;
using Microsoft.Build.Locator;
using Microsoft.DotNet.Watcher.Internal;
using Microsoft.DotNet.Watcher.Tools;
using Microsoft.Extensions.Tools.Internal;
using IConsole = Microsoft.Extensions.Tools.Internal.IConsole;
using Resources = Microsoft.DotNet.Watcher.Tools.Resources;

namespace Microsoft.DotNet.Watcher
{
    public class Program : IDisposable
    {
        private const string Description = @"
Environment variables:

  DOTNET_USE_POLLING_FILE_WATCHER
  When set to '1' or 'true', dotnet-watch will poll the file system for
  changes. This is required for some file systems, such as network shares,
  Docker mounted volumes, and other virtual file systems.

  DOTNET_WATCH
  dotnet-watch sets this variable to '1' on all child processes launched.

  DOTNET_WATCH_ITERATION
  dotnet-watch sets this variable to '1' and increments by one each time
  a file is changed and the command is restarted.

Remarks:
  The special option '--' is used to delimit the end of the options and
  the beginning of arguments that will be passed to the child dotnet process.
  Its use is optional. When the special option '--' is not used,
  dotnet-watch will use the first unrecognized argument as the beginning
  of all arguments passed into the child dotnet process.

  For example: dotnet watch -- --verbose run

  Even though '--verbose' is an option dotnet-watch supports, the use of '--'
  indicates that '--verbose' should be treated instead as an argument for
  dotnet-run.

Examples:
  dotnet watch run
  dotnet watch test
";
        private readonly IConsole _console;
        private readonly string _workingDirectory;
        private readonly CancellationTokenSource _cts;
        private IReporter _reporter;

        public Program(IConsole console, string workingDirectory)
        {
            // We can register the MSBuild that is bundled with the SDK to perform MSBuild things. dotnet-watch is in
            // a nested folder of the SDK's root, we'll back up to it.
            // AppContext.BaseDirectory = $sdkRoot\$sdkVersion\DotnetTools\dotnet-watch\$version\tools\net6.0\any\
            // MSBuild.dll is located at $sdkRoot\$sdkVersion\MSBuild.dll
            var sdkRootDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..");
            MSBuildLocator.RegisterMSBuildPath(sdkRootDirectory);

            Ensure.NotNull(console, nameof(console));
            Ensure.NotNullOrEmpty(workingDirectory, nameof(workingDirectory));

            _console = console;
            _workingDirectory = workingDirectory;
            _cts = new CancellationTokenSource();
            console.CancelKeyPress += OnCancelKeyPress;
            _reporter = CreateReporter(verbose: true, quiet: false, console: _console);
        }

        public static async Task<int> Main(string[] args)
        {
            try
            {
                using var program = new Program(PhysicalConsole.Singleton, Directory.GetCurrentDirectory());
                return await program.RunAsync(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unexpected error:");
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        internal async Task<int> RunAsync(string[] args)
        {
            var rootCommand = CreateRootCommand(HandleWatch);
            return await rootCommand.InvokeAsync(args);
        }

        internal static RootCommand CreateRootCommand(Func<CommandLineOptions, Task<int>> handler)
        {
            var quiet = new Option<bool>(
                new[] { "--quiet", "-q" },
                "Suppresses all output except warnings and errors");

            var verbose = new Option<bool>(
                new[] { "--verbose", "-v" },
                "Show verbose output");

            verbose.AddValidator(v =>
            {
                if (v.FindResultFor(quiet) is not null && v.FindResultFor(verbose) is not null)
                {
                    return Resources.Error_QuietAndVerboseSpecified;
                }

                return null;
            });

            var root = new RootCommand(Description)
            {
                 quiet,
                 verbose,
                 new Option<string>(
                    new[] { "--project", "-p" },
                    "The project to watch"),
                 new Option<bool>(
                    "--list",
                    "Lists all discovered files without starting the watcher"),
            };

            root.TreatUnmatchedTokensAsErrors = false;
            root.Handler = CommandHandler.Create((CommandLineOptions options, ParseResult parseResults) =>
            {
                string[] remainingArguments;
                if (parseResults.UnparsedTokens.Any() && parseResults.UnmatchedTokens.Any())
                {
                    remainingArguments = parseResults.UnmatchedTokens.Append("--").Concat(parseResults.UnparsedTokens).ToArray();
                }
                else
                {
                    remainingArguments = parseResults.UnmatchedTokens.Concat(parseResults.UnparsedTokens).ToArray();
                }

                options.RemainingArguments = remainingArguments;
                return handler(options);
            });

            return root;
        }

        private async Task<int> HandleWatch(CommandLineOptions options)
        {
            // update reporter as configured by options
            _reporter = CreateReporter(options.Verbose, options.Quiet, _console);

            try
            {
                if (_cts.IsCancellationRequested)
                {
                    return 1;
                }

                if (options.List)
                {
                    return await ListFilesAsync(_reporter,
                        options.Project,
                        _cts.Token);
                }
                else
                {
                    return await MainInternalAsync(_reporter,
                        options.Project,
                        options.RemainingArguments,
                        _cts.Token);
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    // swallow when only exception is the CTRL+C forced an exit
                    return 0;
                }

                _reporter.Error(ex.ToString());
                _reporter.Error("An unexpected error occurred");
                return 1;
            }
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs args)
        {
            // suppress CTRL+C on the first press
            args.Cancel = !_cts.IsCancellationRequested;

            if (args.Cancel)
            {
                _reporter.Output("Shutdown requested. Press Ctrl+C again to force exit.");
            }

            _cts.Cancel();
        }

        private async Task<int> MainInternalAsync(
            IReporter reporter,
            string project,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken)
        {
            // TODO multiple projects should be easy enough to add here
            string projectFile;
            try
            {
                projectFile = MsBuildProjectFinder.FindMsBuildProject(_workingDirectory, project);
            }
            catch (FileNotFoundException ex)
            {
                reporter.Error(ex.Message);
                return 1;
            }

            var isDefaultRunCommand = false;
            if (args.Count == 1 && args[0] == "run")
            {
                isDefaultRunCommand = true;
            }
            else if (args.Count == 0)
            {
                isDefaultRunCommand = true;
                args = new[] { "run" };
            }

            var watchOptions = DotNetWatchOptions.Default;

            var fileSetFactory = new MsBuildFileSetFactory(reporter,
                watchOptions,
                projectFile,
                waitOnError: true,
                trace: false);
            var processInfo = new ProcessSpec
            {
                Executable = DotnetMuxer.MuxerPath,
                WorkingDirectory = Path.GetDirectoryName(projectFile),
                Arguments = args,
                EnvironmentVariables =
                {
                    ["DOTNET_WATCH"] = "1"
                },
            };

            if (CommandLineOptions.IsPollingEnabled)
            {
                _reporter.Output("Polling file watcher is enabled");
            }

            var defaultProfile = LaunchSettingsProfile.ReadDefaultProfile(_workingDirectory, reporter) ?? new();

            var context = new DotNetWatchContext
            {
                ProcessSpec = processInfo,
                Reporter = _reporter,
                SuppressMSBuildIncrementalism = watchOptions.SuppressMSBuildIncrementalism,
                DefaultLaunchSettingsProfile = defaultProfile,
            };

            if (isDefaultRunCommand && TryReadProject(projectFile, out var projectGraph) && IsHotReloadSupported(projectGraph))
            {
                context.ProjectGraph = projectGraph;
                _reporter.Verbose($"Project supports hot reload and was configured to run with the default run-command. Watching with hot-reload");

                // Use hot-reload based watching if
                // a) watch was invoked with no args or with exactly one arg - the run command e.g. `dotnet watch` or `dotnet watch run`
                // b) The launch profile supports hot-reload based watching.
                // The watcher will complain if users configure this for runtimes that would not support it.
                await using var watcher = new HotReloadDotNetWatcher(reporter, fileSetFactory, watchOptions, _console);
                await watcher.WatchAsync(context, cancellationToken);
            }
            else
            {
                _reporter.Verbose("Did not find a HotReloadProfile or running a non-default command. Watching with legacy behavior.");

                // We'll use the presence of a profile to decide if we're going to use the hot-reload based watching.
                // The watcher will complain if users configure this for runtimes that would not support it.
                await using var watcher = new DotNetWatcher(reporter, fileSetFactory, watchOptions);
                await watcher.WatchAsync(context, cancellationToken);
            }

            return 0;
        }

        private bool TryReadProject(string project, out ProjectGraph projectInstance)
        {
            projectInstance = default;
            try
            {
                projectInstance = new ProjectGraph(project);
                return true;
            }
            catch (Exception ex)
            {
                _reporter.Verbose("Reading the project instance failed.");
                _reporter.Verbose(ex.ToString());
                return false;
            }
        }

        private static bool IsHotReloadSupported(ProjectGraph projectGraph)
        {
            var projectInstance = projectGraph.EntryPointNodes.FirstOrDefault()?.ProjectInstance;
            if (projectInstance is null)
            {
                return false;
            }

            var projectCapabilities = projectInstance.GetItems("ProjectCapability");
            foreach (var item in projectCapabilities)
            {
                if (item.EvaluatedInclude == "SupportsHotReload")
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<int> ListFilesAsync(
            IReporter reporter,
            string project,
            CancellationToken cancellationToken)
        {
            // TODO multiple projects should be easy enough to add here
            string projectFile;
            try
            {
                projectFile = MsBuildProjectFinder.FindMsBuildProject(_workingDirectory, project);
            }
            catch (FileNotFoundException ex)
            {
                reporter.Error(ex.Message);
                return 1;
            }

            var fileSetFactory = new MsBuildFileSetFactory(
                reporter,
                DotNetWatchOptions.Default,
                projectFile,
                waitOnError: false,
                trace: false);
            var files = await fileSetFactory.CreateAsync(cancellationToken);

            if (files == null)
            {
                return 1;
            }

            foreach (var file in files)
            {
                _console.Out.WriteLine(file.FilePath);
            }

            return 0;
        }

        private static IReporter CreateReporter(bool verbose, bool quiet, IConsole console)
            => new PrefixConsoleReporter("watch : ", console, verbose || IsGlobalVerbose(), quiet);

        private static bool IsGlobalVerbose()
        {
            bool.TryParse(Environment.GetEnvironmentVariable("DOTNET_CLI_CONTEXT_VERBOSE"), out bool globalVerbose);
            return globalVerbose;
        }

        public void Dispose()
        {
            _console.CancelKeyPress -= OnCancelKeyPress;
            _cts.Dispose();
        }
    }
}
