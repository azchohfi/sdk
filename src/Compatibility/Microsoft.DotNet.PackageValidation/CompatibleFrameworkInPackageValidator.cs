// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Client;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.Frameworks;

namespace Microsoft.DotNet.PackageValidation
{
    /// <summary>
    /// Validates that the api surface of the compatible frameworks.
    /// </summary>
    public class CompatibleFrameworkInPackageValidator
    {
        private readonly ApiCompatRunner _apiCompatRunner;
        private readonly ILogger _log;

        public CompatibleFrameworkInPackageValidator(string noWarn, (string, string)[] ignoredDifferences, ILogger log)
        {
            _log = log;
            _apiCompatRunner = new(noWarn, ignoredDifferences, _log);
        }

        /// <summary>
        /// Validates that the compatible frameworks have compatible surface area.
        /// </summary>
        /// <param name="package">Nuget Package that needs to be validated.</param>
        public void Validate(Package package)
        {       
            IEnumerable<ContentItem> compileAssets = package.CompileAssets.OrderByDescending(t => ((NuGetFramework)t.Properties["tfm"]).Version);
            ManagedCodeConventions conventions = new ManagedCodeConventions(null);
            Queue<ContentItem> compileAssetsQueue = new Queue<ContentItem>(compileAssets);

            while (compileAssetsQueue.Count > 0)
            {
                ContentItem compileTimeAsset = compileAssetsQueue.Dequeue();
                ContentItemCollection contentItemCollection = new();
                contentItemCollection.Load(compileAssetsQueue.Select(t => t.Path));

                NuGetFramework framework = (NuGetFramework)compileTimeAsset.Properties["tfm"];
                SelectionCriteria managedCriteria = conventions.Criteria.ForFramework(framework);

                ContentItem compatibleFrameworkAsset = null;
                if (package.HasRefAssemblies)
                {
                    compatibleFrameworkAsset = contentItemCollection.FindBestItemGroup(managedCriteria, conventions.Patterns.CompileRefAssemblies)?.Items.FirstOrDefault();
                }
                else
                {
                    compatibleFrameworkAsset = contentItemCollection.FindBestItemGroup(managedCriteria, conventions.Patterns.CompileLibAssemblies)?.Items.FirstOrDefault();
                }

                if (compatibleFrameworkAsset != null)
                {
                    _apiCompatRunner.QueueApiCompat(package.PackagePath, 
                        compatibleFrameworkAsset.Path,
                        package.PackagePath,
                        compileTimeAsset.Path,
                        Path.GetFileName(package.PackagePath),
                        Resources.CompatibleFrameworkInPackageValidatorHeader,
                        string.Format(Resources.ApiCompatibilityHeader, compatibleFrameworkAsset.Path, compileTimeAsset.Path));
                }
            }

            _apiCompatRunner.RunApiCompat();
        }
    }
}
