// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.ApiCompatibility;
using Microsoft.DotNet.ApiCompatibility.Abstractions;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.Frameworks;

namespace Microsoft.DotNet.PackageValidation
{
    /// <summary>
    /// Validates that no target framework / rid support is dropped in the latest package.
    /// Reports all the breaking changes in the latest package.
    /// </summary>
    public class BaselinePackageValidator
    {
        private static HashSet<string> s_diagList = new HashSet<string>{ DiagnosticIds.TargetFrameworkDropped, DiagnosticIds.TargetFrameworkAndRidPairDropped };

        private readonly Package _baselinePackage;
        private readonly bool _runApiCompat;
        private readonly ApiCompatRunner _apiCompatRunner;
        private readonly ILogger _log;
        private readonly DiagnosticBag<IDiagnostic> _diagnosticBag;

        public BaselinePackageValidator(Package baselinePackage, string noWarn, (string, string)[] ignoredDifferences, bool runApiCompat, ILogger log)
        {
            _baselinePackage = baselinePackage;
            _runApiCompat = runApiCompat;
            _log = log;
            _apiCompatRunner = new(noWarn, ignoredDifferences, _log);
            _diagnosticBag = new(noWarn?.Split(';')?.Where(t => s_diagList.Contains(t)), ignoredDifferences);
        }

        /// <summary>
        /// Validates the latest nuget package doesnot drop any target framework/rid and does not introduce any breaking changes.
        /// </summary>
        /// <param name="package">Nuget Package that needs to be validated.</param>
        public void Validate(Package package)
        {
            foreach (ContentItem baselineCompileTimeAsset in _baselinePackage.CompileAssets)
            {
                NuGetFramework baselineTargetFramework = (NuGetFramework)baselineCompileTimeAsset.Properties["tfm"];
                ContentItem latestCompileTimeAsset = package.FindBestCompileAssetForFramework(baselineTargetFramework);
                if (latestCompileTimeAsset == null)
                {
                    if (!_diagnosticBag.Filter(DiagnosticIds.TargetFrameworkDropped, baselineTargetFramework.ToString()))
                    {
                        string message = string.Format(Resources.MissingTargetFramework, baselineTargetFramework.ToString());
                        _log.LogError(DiagnosticIds.TargetFrameworkDropped + " " + message);
                    }
                }
                else if (_runApiCompat)
                {
                    _apiCompatRunner.QueueApiCompat(_baselinePackage.PackagePath,
                        baselineCompileTimeAsset.Path,
                        package.PackagePath,
                        latestCompileTimeAsset.Path,
                        Path.GetFileName(package.PackagePath),
                        Resources.BaselineVersionValidatorHeader,
                        string.Format(Resources.ApiCompatibilityHeader, baselineCompileTimeAsset.Path, latestCompileTimeAsset.Path));
                }
            }

            foreach (ContentItem baselineRuntimeAsset in _baselinePackage.RuntimeAssets)
            {
                NuGetFramework baselineTargetFramework = (NuGetFramework)baselineRuntimeAsset.Properties["tfm"];
                ContentItem latestRuntimeAsset = package.FindBestRuntimeAssetForFramework(baselineTargetFramework);
                if (latestRuntimeAsset == null)
                {
                    if (!_diagnosticBag.Filter(DiagnosticIds.TargetFrameworkDropped, baselineTargetFramework.ToString()))
                    {
                        string message = string.Format(Resources.MissingTargetFramework, baselineTargetFramework.ToString());
                        _log.LogError(DiagnosticIds.TargetFrameworkDropped + " " + message);
                    }
                }
                else
                {
                    if (_runApiCompat)
                    {
                        _apiCompatRunner.QueueApiCompat(_baselinePackage.PackagePath, 
                            baselineRuntimeAsset.Path,
                            package.PackagePath, 
                            latestRuntimeAsset.Path,
                            Path.GetFileName(package.PackagePath),
                            Resources.BaselineVersionValidatorHeader,
                            string.Format(Resources.ApiCompatibilityHeader, baselineRuntimeAsset.Path, latestRuntimeAsset.Path));
                    }
                }
            }

            foreach (ContentItem baselineRuntimeSpecificAsset in _baselinePackage.RuntimeSpecificAssets)
            {
                NuGetFramework baselineTargetFramework = (NuGetFramework)baselineRuntimeSpecificAsset.Properties["tfm"];
                string baselineRid = (string)baselineRuntimeSpecificAsset.Properties["rid"];
                ContentItem latestRuntimeSpecificAsset = package.FindBestRuntimeAssetForFrameworkAndRuntime(baselineTargetFramework, baselineRid);
                if (latestRuntimeSpecificAsset == null)
                {
                    if (!_diagnosticBag.Filter(DiagnosticIds.TargetFrameworkDropped, baselineTargetFramework.ToString() + "-" + baselineRid))
                    {
                        string message = string.Format(Resources.MissingTargetFrameworkAndRid, baselineTargetFramework.ToString(), baselineRid);
                        _log.LogError(DiagnosticIds.TargetFrameworkAndRidPairDropped + " " + message);
                    }
                }
                else
                {
                    if (_runApiCompat)
                    {
                        _apiCompatRunner.QueueApiCompat(_baselinePackage.PackagePath, 
                            baselineRuntimeSpecificAsset.Path,
                            package.PackagePath, 
                            latestRuntimeSpecificAsset.Path,
                            Path.GetFileName(package.PackagePath),
                            Resources.BaselineVersionValidatorHeader,
                            string.Format(Resources.ApiCompatibilityHeader, baselineRuntimeSpecificAsset.Path, latestRuntimeSpecificAsset.Path));
                    }
                }
            }
            
            _apiCompatRunner.RunApiCompat();
        }
    }
}
