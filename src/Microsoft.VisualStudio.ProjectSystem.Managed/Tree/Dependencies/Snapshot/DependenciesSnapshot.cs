﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot.Filters;

#nullable enable

namespace Microsoft.VisualStudio.ProjectSystem.VS.Tree.Dependencies.Snapshot
{
    /// <inheritdoc />
    internal sealed class DependenciesSnapshot : IDependenciesSnapshot
    {
        #region Factories and private constructor

        public static DependenciesSnapshot CreateEmpty(string projectPath)
        {
            return new DependenciesSnapshot(
                projectPath,
                activeTargetFramework: TargetFramework.Empty,
                dependenciesByTargetFramework: ImmutableDictionary<ITargetFramework, ITargetedDependenciesSnapshot>.Empty);
        }

        /// <summary>
        /// For each target framework in <paramref name="changes"/>, applies the corresponding
        /// <see cref="IDependenciesChanges"/> to <paramref name="previousSnapshot"/> in order to produce
        /// and return an updated <see cref="DependenciesSnapshot"/> object.
        /// If no changes are made, <paramref name="previousSnapshot"/> is returned unmodified.
        /// </summary>
        /// <remarks>
        /// As part of the update, each <see cref="IDependenciesSnapshotFilter"/> in <paramref name="snapshotFilters"/>
        /// is given a chance to influence the addition and removal of dependency data in the returned snapshot.
        /// </remarks>
        /// <returns>An updated snapshot, or <paramref name="previousSnapshot"/> if no changes occured.</returns>
        public static DependenciesSnapshot FromChanges(
            string projectPath,
            DependenciesSnapshot previousSnapshot,
            ImmutableDictionary<ITargetFramework, IDependenciesChanges> changes,
            IProjectCatalogSnapshot? catalogs,
            ITargetFramework? activeTargetFramework,
            ImmutableArray<IDependenciesSnapshotFilter> snapshotFilters,
            IReadOnlyDictionary<string, IProjectDependenciesSubTreeProvider> subTreeProviderByProviderType,
            IImmutableSet<string>? projectItemSpecs)
        {
            Requires.NotNullOrWhiteSpace(projectPath, nameof(projectPath));
            Requires.NotNull(previousSnapshot, nameof(previousSnapshot));
            Requires.NotNull(changes, nameof(changes));
            Requires.Argument(!snapshotFilters.IsDefault, nameof(snapshotFilters), "Cannot be default.");
            Requires.NotNull(subTreeProviderByProviderType, nameof(subTreeProviderByProviderType));

            var builder = previousSnapshot.DependenciesByTargetFramework.ToBuilder();

            bool builderChanged = false;

            foreach ((ITargetFramework targetFramework, IDependenciesChanges dependenciesChanges) in changes)
            {
                if (!builder.TryGetValue(targetFramework, out ITargetedDependenciesSnapshot previousTargetedSnapshot))
                {
                    previousTargetedSnapshot = TargetedDependenciesSnapshot.CreateEmpty(projectPath, targetFramework, catalogs);
                }

                ITargetedDependenciesSnapshot newTargetedSnapshot = TargetedDependenciesSnapshot.FromChanges(
                    projectPath,
                    previousTargetedSnapshot,
                    dependenciesChanges,
                    catalogs,
                    snapshotFilters,
                    subTreeProviderByProviderType,
                    projectItemSpecs);

                if (!ReferenceEquals(previousTargetedSnapshot, newTargetedSnapshot))
                {
                    builder[targetFramework] = newTargetedSnapshot;
                    builderChanged = true;
                }
            }

            builderChanged |= RemoveTargetFrameworksWithNoDependencies();

            activeTargetFramework ??= previousSnapshot.ActiveTargetFramework;

            if (builderChanged)
            {
                // Dependencies-by-target-framework has changed
                return new DependenciesSnapshot(
                    projectPath,
                    activeTargetFramework,
                    builder.ToImmutable());
            }

            if (!activeTargetFramework.Equals(previousSnapshot.ActiveTargetFramework))
            {
                // The active target framework changed
                return new DependenciesSnapshot(
                    projectPath,
                    activeTargetFramework,
                    previousSnapshot.DependenciesByTargetFramework);
            }

            if (projectPath != previousSnapshot.ProjectPath)
            {
                // The project path changed
                return new DependenciesSnapshot(
                    projectPath,
                    activeTargetFramework,
                    previousSnapshot.DependenciesByTargetFramework);
            }

            // Nothing has changed, so return the same snapshot
            return previousSnapshot;

            bool RemoveTargetFrameworksWithNoDependencies()
            {
                // This is a long-winded way of doing this that minimises allocations

                List<ITargetFramework>? emptyFrameworks = null;
                bool anythingRemoved = false;

                foreach ((ITargetFramework targetFramework, ITargetedDependenciesSnapshot targetedSnapshot) in builder)
                {
                    if (targetedSnapshot.DependenciesWorld.Count == 0)
                    {
                        if (emptyFrameworks == null)
                        {
                            anythingRemoved = true;
                            emptyFrameworks = new List<ITargetFramework>(builder.Count);
                        }

                        emptyFrameworks.Add(targetFramework);
                    }
                }

                if (emptyFrameworks != null)
                {
                    builder.RemoveRange(emptyFrameworks);
                }

                return anythingRemoved;
            }
        }

        public DependenciesSnapshot RemoveTargets(IEnumerable<ITargetFramework> targetToRemove)
        {
            ImmutableDictionary<ITargetFramework, ITargetedDependenciesSnapshot> newTargets = DependenciesByTargetFramework.RemoveRange(targetToRemove);

            // Return this if no targets changed
            return ReferenceEquals(newTargets, DependenciesByTargetFramework)
                ? this
                : new DependenciesSnapshot(ProjectPath, ActiveTargetFramework, newTargets);
        }

        // Internal, for test use -- normal code should use the factory methods
        internal DependenciesSnapshot(
            string projectPath,
            ITargetFramework activeTargetFramework,
            ImmutableDictionary<ITargetFramework, ITargetedDependenciesSnapshot> dependenciesByTargetFramework)
        {
            Requires.NotNullOrEmpty(projectPath, nameof(projectPath));
            Requires.NotNull(activeTargetFramework, nameof(activeTargetFramework));
            Requires.NotNull(dependenciesByTargetFramework, nameof(dependenciesByTargetFramework));

            ProjectPath = projectPath;
            ActiveTargetFramework = activeTargetFramework;
            DependenciesByTargetFramework = dependenciesByTargetFramework;
        }

        #endregion

        /// <inheritdoc />
        public string ProjectPath { get; }

        /// <inheritdoc />
        public ITargetFramework ActiveTargetFramework { get; }

        /// <inheritdoc />
        public ImmutableDictionary<ITargetFramework, ITargetedDependenciesSnapshot> DependenciesByTargetFramework { get; }

        /// <inheritdoc />
        public bool HasUnresolvedDependency => DependenciesByTargetFramework.Any(x => x.Value.HasUnresolvedDependency);

        /// <inheritdoc />
        public IDependency? FindDependency(string dependencyId, bool topLevel = false)
        {
            if (string.IsNullOrEmpty(dependencyId))
            {
                return null;
            }

            if (topLevel)
            {
                // if top level first try to find by top level id with full path,
                // if found - return, if not - try regular Id in the DependenciesWorld
                foreach ((ITargetFramework _, ITargetedDependenciesSnapshot targetedDependencies) in DependenciesByTargetFramework)
                {
                    IDependency dependency = targetedDependencies.TopLevelDependencies
                        .FirstOrDefault((x, id) => x.TopLevelIdEquals(id), dependencyId);

                    if (dependency != null)
                    {
                        return dependency;
                    }
                }
            }

            foreach ((ITargetFramework _, ITargetedDependenciesSnapshot targetedDependencies) in DependenciesByTargetFramework)
            {
                if (targetedDependencies.DependenciesWorld.TryGetValue(dependencyId, out IDependency dependency))
                {
                    return dependency;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public bool Equals(IDependenciesSnapshot other)
        {
            return other != null && other.ProjectPath.Equals(ProjectPath, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() => $"{DependenciesByTargetFramework.Count} target framework{(DependenciesByTargetFramework.Count == 1 ? "" : "s")} - {ProjectPath}";
    }
}
