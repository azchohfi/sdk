// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.CommandLine;
using Microsoft.DotNet.Tools.Tool.Common;
using LocalizableStrings = Microsoft.DotNet.Tools.Tool.Install.LocalizableStrings;

namespace Microsoft.DotNet.Cli
{
    internal static class ToolInstallCommandParser
    {
        public static readonly Argument PackageIdArgument = new Argument<string>(LocalizableStrings.PackageIdArgumentName)
        {
            Description = LocalizableStrings.PackageIdArgumentDescription
        };

        public static readonly Option VersionOption = new Option<string>("--version", LocalizableStrings.VersionOptionDescription)
        {
            Argument = new Argument<string>(LocalizableStrings.VersionOptionName)
        };

        public static readonly Option ConfigOption = new Option<string>("--configfile", LocalizableStrings.ConfigFileOptionDescription)
        {
            Argument = new Argument<string>(LocalizableStrings.ConfigFileOptionName)
        };

        public static readonly Option AddSourceOption = new Option<IEnumerable<string>>("--add-source", LocalizableStrings.AddSourceOptionDescription)
        {
            Argument = new Argument<IEnumerable<string>>(LocalizableStrings.AddSourceOptionName)
        };

        public static readonly Option FrameworkOption = new Option<string>("--framework", LocalizableStrings.FrameworkOptionDescription)
        {
            Argument = new Argument<string>(LocalizableStrings.FrameworkOptionName)
        };

        public static readonly Option VerbosityOption = CommonOptions.VerbosityOption();

        public static Command GetCommand()
        {
            var command = new Command("install", LocalizableStrings.CommandDescription);

            command.AddArgument(PackageIdArgument);
            command.AddOption(ToolAppliedOption.GlobalOption);
            command.AddOption(ToolAppliedOption.LocalOption);
            command.AddOption(ToolAppliedOption.ToolPathOption);
            command.AddOption(VersionOption);
            command.AddOption(ConfigOption);
            command.AddOption(ToolAppliedOption.ToolManifestOption);
            command.AddOption(AddSourceOption);
            command.AddOption(FrameworkOption);
            command.AddOption(ToolCommandRestorePassThroughOptions.DisableParallelOption);
            command.AddOption(ToolCommandRestorePassThroughOptions.IgnoreFailedSourcesOption);
            command.AddOption(ToolCommandRestorePassThroughOptions.NoCacheOption);
            command.AddOption(ToolCommandRestorePassThroughOptions.InteractiveRestoreOption);
            command.AddOption(VerbosityOption);
            command.AddOption(CommonOptions.DiagOption());

            return command;
        }
    }
}
